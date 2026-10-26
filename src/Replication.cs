using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using System.Security.Cryptography;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        sealed class NpcSample
        {
            public Vector3 From, To;
            public float FromYaw, ToYaw, At, Duration;
        }
        readonly Dictionary<NPC, NpcSample> npcSamples = new Dictionary<NPC, NpcSample>();
        int npcRenderSteps;
        bool flowSync;
        int flowStage;
        float flowAt;
        readonly Dictionary<string, Door> extraDoors = new Dictionary<string, Door>();
        static readonly System.Reflection.FieldInfo brokenWindow = AccessTools.Field(typeof(Window), "broken");

        public void PrepareRunSeed()
        {
            // Reproducible developer runs retain their explicit seed; normal rooms never reuse settings.
            if (Lab.Enabled && !flowSync) { int.TryParse(seedText, out Seed); return; }
            byte[] bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            int previous = Seed;
            Seed = System.BitConverter.ToInt32(bytes, 0) & int.MaxValue;
            if (Seed == previous) Seed = (Seed + 1) & int.MaxValue;
            Log("New multiplayer run seed: " + Seed);
        }

        void ResetReplication() { npcSamples.Clear(); extraDoors.Clear(); npcRenderSteps = 0; }

        void BuildExtraDoors(EnvironmentController ec)
        {
            foreach (Door door in Object.FindObjectsOfType<Door>())
                if ((door is SwingDoor || door is Window) && door.ec == ec)
                {
                    string path = "";
                    Transform node = door.transform;
                    while (node != null && node != ec.transform)
                    { path = node.GetSiblingIndex() + "/" + path; node = node.parent; }
                    path += System.Array.IndexOf(door.GetComponents<Door>(), door);
                    using (var sha = SHA256.Create())
                        path = System.BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path))).Replace("-", "").Substring(0, 16);
                    extraDoors.Add(Key(door) + ":" + (int)door.direction + ":" + path, door);
                }
            if (extraDoors.Count > 1024) throw new IOException("Extra door limit exceeded.");
        }

        void WriteExtraDoors(BinaryWriter writer)
        {
            // Remote players have no native collider on the host; open unlocked swing doors in reach.
            foreach (Door door in extraDoors.Values)
                if (door is SwingDoor && !door.locked && peers.Values.Any(p => p.Id != 0 && p.Ready && p.HasPose && !p.Eliminated && Time.realtimeSinceStartup - p.PoseAt < 2 && Vector3.Distance(p.Position, door.transform.position + door.transform.forward * 5) < 8))
                    door.OpenTimed(2, true);
            writer.Write((ushort)extraDoors.Count);
            foreach (var pair in extraDoors)
            {
                Wire.Text(writer, pair.Key); writer.Write(pair.Value.open); writer.Write(pair.Value.locked);
                writer.Write(pair.Value is Window && (bool)brokenWindow.GetValue(pair.Value));
            }
        }

        void ReadExtraDoors(BinaryReader reader, bool apply)
        {
            int count = reader.ReadUInt16(); if (count > 1024) throw new IOException("Extra door limit exceeded.");
            for (int i = 0; i < count; i++)
            {
                string key = Wire.Text(reader, 256); bool open = reader.ReadBoolean(), locked = reader.ReadBoolean(), broken = reader.ReadBoolean();
                Door door;
                if (!apply || !extraDoors.TryGetValue(key, out door) || door == null) continue;
                if (locked != door.locked) { if (locked) door.Lock(true); else door.Unlock(); }
                Window window = door as Window;
                if (window != null && broken && !(bool)brokenWindow.GetValue(window)) window.Break(false);
                if (open && door is SwingDoor) door.OpenTimed(.3f, false);
                else if (open && !door.open) door.Open(true, false);
                else if (!open && door.open) door.Shut();
            }
        }

        ElevatorScreen clientElevatorStarted;
        ElevatorScreen clientElevatorOwned;
        void ClearClientElevator()
        {
            if(clientElevatorOwned!=null){clientElevatorOwned.gameObject.SetActive(false);Destroy(clientElevatorOwned.gameObject);}
            clientElevatorOwned=clientElevatorStarted=null;
        }
        void EnsureClientElevator()
        {
            if(ReuseResultsElevator())return;
            clientElevatorStarted = null;
            // Its Singleton rejects a replacement until OnDestroy clears the old instance.
            foreach(var old in Object.FindObjectsOfType<ElevatorScreen>()) Object.DestroyImmediate(old.gameObject);
            var field=AccessTools.Field(typeof(BaseGameManager),"elevatorScreenPre");
            var prefab=Resources.FindObjectsOfTypeAll<BaseGameManager>().Select(m=>field.GetValue(m) as ElevatorScreen).FirstOrDefault(s=>s!=null);
            if(prefab==null)throw new IOException("Native elevator screen is unavailable.");
            var screen=Object.Instantiate(prefab);clientElevatorOwned=screen;Object.DontDestroyOnLoad(screen.gameObject);screen.Initialize();
            Log("Client elevator created for generation "+epoch);
        }

        public bool ElevatorStarting(ElevatorScreen screen)
        {
            if(!Active)return true;
            if(Hosting){SendAll(Wire.Packet(Op.Begin,w=>w.Write(epoch)));return true;}
            if(!worldReady||!readyAcknowledged||!beginRequested||clientElevatorStarted==screen)return false;
            clientElevatorStarted=screen;Log("Client native elevator exit started for generation "+epoch);return true;
        }

        static readonly System.Reflection.FieldInfo ElevatorReadyField=AccessTools.Field(typeof(ElevatorScreen),"readyToStart"),ElevatorBusyField=AccessTools.Field(typeof(ElevatorScreen),"busy");
        void UpdateClientElevator()
        {
            if(!Active||Hosting||!worldReady||!readyAcknowledged||!beginRequested)return;
            // ElevatorScreen is a native Singleton: no scene search, and nothing to do once this floor's screen started.
            var screen=Singleton<ElevatorScreen>.Instance;
            if(screen!=null&&clientElevatorStarted!=screen&&(bool)ElevatorReadyField.GetValue(screen)&&!(bool)ElevatorBusyField.GetValue(screen))screen.StartGame();
        }

        int elevatorLabGeneration;
        float elevatorLabAt;
        void UpdateElevatorLab()
        {
            if(!Lab.Enabled||!Hosting||!Lab.HasArg("-bt-elevator-tour")||!worldReady)return;
            var manager=Singleton<BaseGameManager>.Instance;
            if(manager==null||!manager.Ec.Active||!peers.Values.Any(p=>p.Id!=0&&p.Ready))return;
            if(elevatorLabGeneration!=epoch){elevatorLabGeneration=epoch;elevatorLabAt=Time.realtimeSinceStartup+25;}
            if(Time.realtimeSinceStartup<elevatorLabAt)return;
            elevatorLabAt=float.MaxValue;
            if(epoch==1)manager.RestartLevel();
            else if(epoch==2)manager.LoadNextLevel();
            else Log("LAB elevator tour reached active generation "+epoch);
        }

        void ReceiveNpcPose(NPC npc, Vector3 position, float yaw)
        {
            float now = Time.realtimeSinceStartup;
            NpcSample sample;
            if (!npcSamples.TryGetValue(npc, out sample))
            {
                npcSamples[npc] = sample = new NpcSample();
                sample.At = now - .1f;
            }
            sample.Duration = Mathf.Clamp(now - sample.At, .05f, .2f);
            sample.From = npc.transform.position; sample.FromYaw = npc.transform.eulerAngles.y;
            sample.To = position; sample.ToYaw = yaw; sample.At = now;
            // Teleports and missed updates snap; ordinary network steps render between packets.
            if (Vector3.Distance(sample.From, position) > 20)
            { sample.From = position; sample.FromYaw = yaw; }
        }

        void UpdateNpcReplication()
        {
            UpdateFlowLab();
            if (!Active || Hosting || !worldReady) return;
            foreach (var pair in npcSamples)
            {
                NPC npc = pair.Key; if (npc == null) continue;
                NpcSample sample = pair.Value;
                float t = Mathf.Clamp01((Time.realtimeSinceStartup - sample.At) / sample.Duration);
                Vector3 position = Vector3.Lerp(sample.From, sample.To, t);
                if ((position - npc.transform.position).sqrMagnitude > .000001f) npcRenderSteps++;
                if (npc.Entity != null) npc.Entity.Teleport(position); else npc.transform.position = position;
                npc.transform.rotation = Quaternion.Euler(0, Mathf.LerpAngle(sample.FromYaw, sample.ToYaw, t), 0);
            }
        }

        void UpdateFlowLab()
        {
            if (!flowSync || !Hosting || epoch != 1 || !worldReady || !peers.Values.Any(p => p.Id != 0 && p.Ready)) return;
            float now = Time.realtimeSinceStartup;
            if (flowStage == 0) { flowAt = now + 2; flowStage = 1; }
            if (now < flowAt) return;
            if (flowStage == 1)
            {
                foreach (var door in extraDoors.Values)
                {
                    Window window = door as Window;
                    if (window != null) window.Break(false);
                    else door.Lock(true);
                }
                flowStage = 2; flowAt = now + 5;
            }
            else if (flowStage == 2)
            {
                foreach (var door in extraDoors.Values) if (door is SwingDoor) { door.Unlock(); door.OpenTimed(10, false); }
                flowStage = 3;
            }
            else if (flowStage == 3 && hidingFinished) { flowStage = 4; flowAt = now + 10; }
            else if (flowStage == 4)
            {
                flowStage = 5;
                Singleton<BaseGameManager>.Instance.RestartLevel();
            }
        }

        string ReplicationDiagnostic()
        {
            return ",\"brokenWindows\":" + extraDoors.Values.Count(d => d is Window && (bool)brokenWindow.GetValue(d))
                + ",\"lockedSwingDoors\":" + extraDoors.Values.Count(d => d is SwingDoor && d.locked)
                + ",\"openSwingDoors\":" + extraDoors.Values.Count(d => d is SwingDoor && d.open)
                + ",\"unreadyPeers\":" + peers.Values.Count(p => p.Id != self && p.Accepted && !p.Ready);
        }
    }

    [HarmonyPatch(typeof(SwingDoor), "OnTriggerStay")]
    static class ReplicaSwingTrigger
    { static bool Prefix() { return !ModHooks.ClientWorld || TogetherRuntime.Applying; } }

    [HarmonyPatch(typeof(ElevatorScreen), "StartGame")]
    static class MultiplayerElevatorStart
    { static bool Prefix(ElevatorScreen __instance) {var r=ModHooks.Current;return r==null||r.ElevatorStarting(__instance);} }

    [HarmonyPatch(typeof(Window), "Break")]
    static class ReplicaWindowBreak
    { static bool Prefix() { return !ModHooks.ClientWorld || TogetherRuntime.Applying; } }
}
