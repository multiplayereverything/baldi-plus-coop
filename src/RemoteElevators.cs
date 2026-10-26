using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        static readonly System.Reflection.FieldInfo remoteElevatorInside = AccessTools.Field(typeof(Elevator), "insideCollider");
        static readonly System.Reflection.FieldInfo remoteElevatorManager = AccessTools.Field(typeof(Elevator), "manager");
        static readonly System.Reflection.MethodInfo remoteElevatorBreak = AccessTools.Method(typeof(Elevator), "BreakElevator");
        static readonly System.Reflection.MethodInfo remoteElevatorFinish = AccessTools.Method(typeof(Elevator), "FinishLevel");
        bool remoteElevatorLabStarted;

        void WriteElevatorWorld(BinaryWriter writer)
        {
            var elevators = UnityEngine.Object.FindObjectsOfType<Elevator>().OrderBy(e => Key(e)).ToArray();
            if (elevators.Length > 64) throw new IOException("Too many elevators.");
            writer.Write((byte)elevators.Length);
            foreach (var elevator in elevators) { Wire.Text(writer, Key(elevator)); writer.Write((byte)elevator.CurrentState); }
        }

        void ReadElevatorWorld(BinaryReader reader, bool apply)
        {
            int count = reader.ReadByte(); if (count > 64) throw new IOException("Too many elevators.");
            var known = apply ? UnityEngine.Object.FindObjectsOfType<Elevator>().ToDictionary(e => Key(e)) : null;
            var seen = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                string key = Wire.Text(reader, 256); int state = reader.ReadByte();
                if (!seen.Add(key) || !Enum.IsDefined(typeof(ElevatorState), state)) throw new IOException("Invalid elevator state.");
                Elevator elevator;
                if (apply && known.TryGetValue(key, out elevator) && elevator.CurrentState != (ElevatorState)state) elevator.SetState((ElevatorState)state);
            }
        }

        // The center point uses the same world position transmitted for a native player.
        // Transforming into each box's coordinates also handles rotated elevator prefabs.
        static bool InsideNativeElevator(Elevator elevator, Vector3 position)
        {
            var group = remoteElevatorInside.GetValue(elevator) as ColliderGroup;
            if (group == null || !group.gameObject.activeInHierarchy) return false;
            foreach (var box in group.GetComponentsInChildren<BoxCollider>())
            {
                if (!box.enabled || !box.gameObject.activeInHierarchy) continue;
                Vector3 point = box.transform.InverseTransformPoint(position) - box.center;
                Vector3 half = box.size * .5f;
                if (Mathf.Abs(point.x) <= half.x && Mathf.Abs(point.y) <= half.y && Mathf.Abs(point.z) <= half.z) return true;
            }
            return false;
        }

        bool ValidRemoteElevatorRequest(Peer sender, Elevator elevator, int generation)
        {
            return Hosting && worldReady && !deathSent && !MigrationInProgress && generation == epoch &&
                sender != null && sender.Accepted && sender.Ready && sender.HasPose && !sender.Eliminated && !sender.InLocker &&
                Time.realtimeSinceStartup - sender.PoseAt < 2 && elevator != null && elevator.CurrentState == ElevatorState.OpenForExit &&
                InsideNativeElevator(elevator, sender.Position);
        }

        public bool BeforeElevatorButton(Elevator elevator)
        {
            if (!Active || !Running) return true;
            if (LocalEliminated || MigrationInProgress) return false;
            if (Hosting) return true;
            if (worldReady && readyAcknowledged && elevator != null)
                Network.Send(0, Wire.Packet(Op.ElevatorRequest, writer => { writer.Write(epoch); Wire.Text(writer, Key(elevator)); }));
            return false;
        }

        bool HandleRemoteElevatorClient(int id, Peer sender, Op op, BinaryReader reader)
        {
            if (op != Op.ElevatorRequest) return false;
            int generation = reader.ReadInt32(); string key = Wire.Text(reader, 256);
            var elevator = UnityEngine.Object.FindObjectsOfType<Elevator>().FirstOrDefault(e => Key(e) == key);
            if (!ValidRemoteElevatorRequest(sender, elevator, generation) || Time.realtimeSinceStartup - sender.LastAction < .15f) return true;
            sender.LastAction = Time.realtimeSinceStartup;
            var manager = remoteElevatorManager.GetValue(elevator) as ElevatorManager;
            if (manager == null || !manager.Elevators.Contains(elevator)) return true;
            // Retain the native failure quota and final-exit decision. Only the
            // physical local-player trigger requirement is replaced by host-checked pose.
            if (manager.ShouldFail(elevator))
            {
                remoteElevatorBreak.Invoke(elevator, null);
                Log("Remote player " + sender.Name + " triggered a native elevator failure.");
            }
            else if (manager.ExitAvailable)
            {
                remoteElevatorFinish.Invoke(elevator, null);
                elevator.SetState(ElevatorState.FinishingLevel);
                manager.Elevators.Clear();
                Log("Remote player " + sender.Name + " completed the floor through its native elevator.");
            }
            return true;
        }

        public void UpdateRemoteElevatorLab()
        {
            if (!Lab.Enabled || remoteElevatorLabStarted || !TitleVisible || !Lab.HasArg("-bt-remote-elevator-tour")) return;
            remoteElevatorLabStarted = true; StartCoroutine(TourRemoteElevator());
        }

        void LabPressRemoteElevator(Peer sender, Elevator elevator, int generation)
        {
            var packet = Wire.Packet(Op.ElevatorRequest, w => { w.Write(generation); Wire.Text(w, Key(elevator)); });
            using (var stream = new MemoryStream(packet)) using (var reader = new BinaryReader(stream))
            { var op = (Op)reader.ReadByte(); HandleRemoteElevatorClient(sender.Id, sender, op, reader); }
        }

        IEnumerator TourRemoteElevator()
        {
            var checks = new Dictionary<string, bool>();
            var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            reservation.Start(); portText = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port.ToString(); reservation.Stop();
            Connect(true); AutoStartRun();
            while (!worldReady) yield return null;
            yield return new WaitForSecondsRealtime(2);
            var game = Singleton<BaseGameManager>.Instance;
            var elevator = UnityEngine.Object.FindObjectsOfType<Elevator>().First();
            var manager = (ElevatorManager)remoteElevatorManager.GetValue(elevator);
            elevator.SetState(ElevatorState.OpenForExit);
            var inside = (ColliderGroup)remoteElevatorInside.GetValue(elevator);
            var box = inside.GetComponentsInChildren<BoxCollider>().First();
            Vector3 point = box.transform.TransformPoint(box.center);
            checks["native-box-center-inside"] = InsideNativeElevator(elevator, point);
            checks["native-box-exterior-outside"] = !InsideNativeElevator(elevator, point + new Vector3(100, 0, 100));
            var remote = new Peer { Id = 3, Name = "Elevator survivor", Accepted = true, Ready = true, HasPose = true, Position = point, PoseAt = Time.realtimeSinceStartup };
            peers.Add(3, remote);
            EliminatePeer(self, null);
            game.CollectNotebooks(game.Ec.notebookTotal);
            manager.SetTotalOutOfOrderElevators(1 + Singleton<StickerManager>.Instance.StickerValue(Sticker.Elevator));
            elevator.SetState(ElevatorState.OpenForExit);
            checks["host-dead-survivor-valid"] = LocalEliminated && ValidRemoteElevatorRequest(remote, elevator, epoch);
            checks["wrong-generation-rejected"] = !ValidRemoteElevatorRequest(remote, elevator, epoch - 1);
            remote.Eliminated = true;
            checks["dead-player-rejected"] = !ValidRemoteElevatorRequest(remote, elevator, epoch); remote.Eliminated = false;
            remote.InLocker = true;
            checks["locker-player-rejected"] = !ValidRemoteElevatorRequest(remote, elevator, epoch); remote.InLocker = false;
            remote.PoseAt = Time.realtimeSinceStartup - 3;
            checks["stale-pose-rejected"] = !ValidRemoteElevatorRequest(remote, elevator, epoch); remote.PoseAt = Time.realtimeSinceStartup;
            remote.Position = point + new Vector3(100, 0, 100);
            checks["distant-player-rejected"] = !ValidRemoteElevatorRequest(remote, elevator, epoch); remote.Position = point;
            checks["native-failure-required"] = manager.ShouldFail(elevator);
            LabPressRemoteElevator(remote, elevator, epoch);
            checks["remote-native-failure"] = elevator.CurrentState == ElevatorState.OutOfOrder;
            byte[] snapshot;
            using (var stream = new MemoryStream()) { using (var writer = new BinaryWriter(stream)) WriteElevatorWorld(writer); snapshot = stream.ToArray(); }
            elevator.SetState(ElevatorState.Open);
            using (var stream = new MemoryStream(snapshot)) using (var reader = new BinaryReader(stream))
            { ReadElevatorWorld(reader, true); checks["elevator-state-roundtrip"] = stream.Position == stream.Length && elevator.CurrentState == ElevatorState.OutOfOrder; }
            using (var stream = new MemoryStream())
            {
                var writer = new BinaryWriter(stream); writer.Write((byte)1); Wire.Text(writer, Key(elevator)); writer.Write((byte)255); stream.Position = 0;
                try { ReadElevatorWorld(new BinaryReader(stream), false); checks["invalid-state-rejected"] = false; }
                catch (IOException) { checks["invalid-state-rejected"] = true; }
            }
            manager.SetTotalOutOfOrderElevators(0);
            elevator.SetState(ElevatorState.OpenForExit); remote.LastAction = -100; remote.PoseAt = Time.realtimeSinceStartup;
            checks["native-final-exit-available"] = manager.ExitAvailable && !manager.ShouldFail(elevator);
            LabPressRemoteElevator(remote, elevator, epoch);
            checks["remote-native-final-exit"] = elevator.CurrentState == ElevatorState.FinishingLevel && manager.Elevators.Count == 0 &&
                (bool)AccessTools.Field(typeof(BaseGameManager), "levelFinishDelayStarted").GetValue(game);
            // Keep the evidence on this floor rather than allowing the lab's finish coroutine to load another.
            game.StopAllCoroutines();
            checks["host-remains-dead-before-next-round"] = LocalEliminated;
            string json = "{\"passed\":" + checks.Count(p => p.Value) + ",\"failed\":" + checks.Count(p => !p.Value) + ",\"checks\":{" +
                string.Join(",", checks.Select(p => "\"" + p.Key + "\":" + p.Value.ToString().ToLowerInvariant()).ToArray()) + "}}";
            string folder = Path.Combine(Root, "MultiplayerMod/artifacts/tests"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "remote-elevator-results.json"), json); Log("LAB remote elevator " + json);
        }
    }

    [HarmonyPatch(typeof(Elevator), "ButtonPressed")]
    static class MultiplayerRemoteElevatorButton
    {
        static bool Prefix(Elevator __instance)
        {
            var runtime = ModHooks.Current;
            return runtime == null || runtime.BeforeElevatorButton(__instance);
        }
    }

    [HarmonyPatch(typeof(Elevator), "StateUpdate")]
    static class MultiplayerRemoteElevatorState
    {
        // Clients receive host states; their local physical trigger must never
        // independently consume a failure or start floor progression.
        static bool Prefix() { return !ModHooks.ClientWorld; }
    }
}
