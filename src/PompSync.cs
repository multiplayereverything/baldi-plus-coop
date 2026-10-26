using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // Host-side record of one Mrs. Pomp (NoLateTeacher) appointment for a remote player.
    // The host has no PlayerManager for that player, so the custom states below read the peer pose
    // from this record and the owner client applies the UI, timer audio and drag to its own player.
    sealed class PompAppointment
    {
        public NoLateTeacher Pomp;
        public RoomController Room;
        public int Peer, Token, Index;
        public byte Phase; // 1 inform, 2 waiting, 3 timed out (ambush), 4 attacked (chase/drag)
        public float Time, Initial, ClaimAt = -100;
        public bool Dragging, Attached, InLocker;
        public Vector3 Position;
    }

    // NoLateTeacher multiplayer sync. Owned by one feature; called from NpcFeatures.cs.
    // Ops: PompToClient = 74 (host to clients), PompToHost = 75 (client to host).
    public sealed partial class TogetherRuntime
    {
        sealed class PompLocal
        {
            public NoLateTeacher Pomp; public RoomController Room; public NoLateIcon Icon;
            public int Index, Token; public byte Phase = 1;
            public float Time, Total, AssignedAt, NextClaim;
            public bool Dragging; public MovementModifier Mod; public ActivityModifier Attached;
        }
        // PompToClient sub-commands.
        const byte PompCmdAssign = 1, PompCmdInTime = 2, PompCmdTimedOut = 3, PompCmdAttach = 4, PompCmdRelease = 5, PompCmdCancel = 6;
        // PompToHost sub-commands.
        const byte PompReportArrived = 1, PompReportDoor = 2;
        // Native catch is the Wander trigger; a remote player has no collider on the host.
        const float PompCatchDistance = 4.5f;
        // minDragDistance is 2; the remote pose is up to one network tick behind.
        const float PompDragTolerance = 1.5f;
        readonly List<PompAppointment> pompAppointments = new List<PompAppointment>();
        readonly List<PompLocal> pompLocal = new List<PompLocal>();
        readonly Dictionary<NoLateTeacher,int> pompSighted = new Dictionary<NoLateTeacher,int>();
        readonly HashSet<int> pompAppliedTokens = new HashSet<int>();
        int pompNextToken, pompAssigns, pompRewards, pompTimeouts, pompAttacks, pompTeaches, pompCancels, pompOwnerAssigns, pompOwnerDragFrames, pompOwnerPoints;
        byte[] pompLastOwnerPacket;
        // An assignment can arrive before the snapshot that creates this client's Pomp replica; retry it briefly.
        struct PompPendingAssign { public int Generation, Index, Token, Room; public float ClassTime, Until; }
        readonly List<PompPendingAssign> pompPending = new List<PompPendingAssign>();
        int pompLateAssigns;
        bool pompLabStarted;

        static T PompGet<T>(NoLateTeacher pomp, string name) { return (T)AccessTools.Field(typeof(NoLateTeacher), name).GetValue(pomp); }
        static void PompSet(NoLateTeacher pomp, string name, object value) { AccessTools.Field(typeof(NoLateTeacher), name).SetValue(pomp, value); }

        void ResetPomp()
        {
            foreach (var appointment in pompAppointments.ToArray()) CancelPompAppointment(appointment, false);
            foreach (var local in pompLocal.ToArray()) ReleasePompLocal(local, true);
            pompAppointments.Clear(); pompLocal.Clear(); pompSighted.Clear(); pompAppliedTokens.Clear(); pompPending.Clear();
        }

        void UpdatePomp()
        {
            if (!Active || !Running || !worldReady) return;
            var manager = Singleton<BaseGameManager>.Instance;
            if (manager == null || manager.Ec == null) return;
            if (pompPending.Count > 0) RetryPompAssigns();
            UpdatePompOwner(manager.Ec);
            if (Hosting) UpdatePompHost(manager.Ec);
        }

        // ---------------- Host: remote targeting and appointments ----------------

        void UpdatePompHost(EnvironmentController ec)
        {
            foreach (var a in pompAppointments.ToArray())
            {
                Peer peer;
                if (a.Pomp == null) { pompAppointments.Remove(a); continue; }
                if (!peers.TryGetValue(a.Peer, out peer)) { CancelPompAppointment(a, false); continue; }
                if (peer.Eliminated || !peer.Ready) { CancelPompAppointment(a, true); continue; }
                var state = a.Pomp.behaviorStateMachine.CurrentState as TogetherPompState;
                if (state == null || state.Appt != a) { CancelPompAppointment(a, true); continue; }
                if (peer.HasPose) a.Position = peer.Position;
                a.InLocker = peer.InLocker;
            }
            if (!ec.Active) return;
            foreach (NPC npc in npcs)
            {
                var pomp = npc as NoLateTeacher;
                if (pomp == null) continue;
                var current = pomp.behaviorStateMachine.CurrentState;
                // Exactly NoLateTeacher_Wander: Cooldown derives from it but ignores players.
                if (current == null || current.GetType() != typeof(NoLateTeacher_Wander)) { pompSighted.Remove(pomp); continue; }
                ScanPomp(pomp, (NoLateTeacher_Wander)current);
            }
        }

        void ScanPomp(NoLateTeacher pomp, NoLateTeacher_Wander state)
        {
            Peer closest = NearestVisiblePeer(pomp);
            if (closest == null || closest.Id == self) { pompSighted.Remove(pomp); return; }
            if (pompAppointments.Any(a => a.Peer == closest.Id)) return;
            if (Vector3.Distance(closest.Position, pomp.transform.position) <= PompCatchDistance) { pompSighted.Remove(pomp); RemotePompCaught(pomp, closest); return; }
            // Native PlayerSighted / PlayerInSight for the host's own Looker, applied to the remote pose.
            var nav = pomp.behaviorStateMachine.CurrentNavigationState as NavigationState_TargetPlayer;
            int sighted;
            bool known = pompSighted.TryGetValue(pomp, out sighted);
            if (!known || sighted != closest.Id || nav == null)
            {
                nav = new NavigationState_TargetPlayer(pomp, 63, closest.Position);
                AccessTools.Field(typeof(NoLateTeacher_Wander), "playerTargetNavigationState").SetValue(state, nav);
                pomp.behaviorStateMachine.ChangeNavigationState(nav);
                if (!known) pomp.CallPlayer();
                pompSighted[pomp] = closest.Id;
            }
            else nav.UpdatePosition(closest.Position);
            pomp.ChaseSpeed();
        }

        // Native AssignClassRoom with the remote pose in place of player.transform.position.
        RoomController PompClassRoomFor(NoLateTeacher pomp, Vector3 position)
        {
            var ec = pomp.ec;
            var dijkstra = PompGet<DijkstraMap>(pomp, "dijkstraMap");
            Cell pompCell = ec.CellFromPosition(pomp.transform.position), peerCell = ec.CellFromPosition(position);
            RoomController pompRoom = pompCell == null ? null : pompCell.room, peerRoom = peerCell == null ? null : peerCell.room;
            var candidates = new List<WeightedRoomController>();
            if (dijkstra != null) dijkstra.Calculate();
            foreach (RoomController room in ec.rooms)
            {
                if (room.category != RoomCategory.Class || room == pompRoom || room == peerRoom) continue;
                int best = 0;
                if (dijkstra != null) foreach (Door door in room.doors) { int value = dijkstra.Value(door.aTile.position); if (value > best) best = value; }
                if (best > 0 && best != int.MaxValue) candidates.Add(new WeightedRoomController { selection = room, weight = best });
            }
            if (candidates.Count == 0)
                foreach (RoomController room in ec.rooms)
                    if (room.category == RoomCategory.Class && room != pompRoom && room != peerRoom) candidates.Add(new WeightedRoomController { selection = room, weight = 1 });
            if (candidates.Count == 0) return null;
            return WeightedSelection<RoomController>.ControlledRandomSelectionList(WeightedRoomController.Convert(candidates), new System.Random());
        }

        static void QueuePompIntro(NoLateTeacher pomp, float classTime)
        {
            var audio = PompGet<AudioManager>(pomp, "audMan");
            var numbers = PompGet<SoundObject[]>(pomp, "audNumbers");
            int minutes = Mathf.RoundToInt(classTime / 60f);
            audio.FlushQueue(true);
            audio.QueueAudio(PompGet<SoundObject>(pomp, "audIntro"));
            if (numbers != null && minutes >= 0 && minutes < numbers.Length) audio.QueueAudio(numbers[minutes]);
            audio.QueueAudio(PompGet<SoundObject>(pomp, "audMinutes"));
            audio.QueueAudio(PompGet<SoundObject>(pomp, "audInsructions"));
        }

        void RemotePompCaught(NoLateTeacher pomp, Peer peer)
        {
            int index = Array.IndexOf(npcs, (NPC)pomp);
            if (index < 0) return;
            var room = PompClassRoomFor(pomp, peer.Position);
            if (room == null)
            {
                pomp.behaviorStateMachine.ChangeState(new NoLateTeacher_Cooldown(pomp, pomp.Cooldown));
                pomp.behaviorStateMachine.ChangeNavigationState(new NavigationState_WanderRounds(pomp, 0));
                return;
            }
            PompSet(pomp, "classRoom", room);
            pomp.Navigator.SetSpeed(0); pomp.Navigator.maxSpeed = 0;
            float classTime = PompGet<float>(pomp, "classTime");
            var a = new PompAppointment { Pomp = pomp, Room = room, Peer = peer.Id, Token = ++pompNextToken, Index = index, Phase = 1, Time = classTime, Initial = classTime, Position = peer.Position, InLocker = peer.InLocker };
            pompAppointments.Add(a);
            QueuePompIntro(pomp, classTime);
            pomp.behaviorStateMachine.ChangeState(new TogetherPompInform(pomp, a, 3f));
            int roomIndex = pomp.ec.rooms.IndexOf(room);
            SendPompOwner(a, PompCmdAssign, w => { w.Write(roomIndex); w.Write(classTime); });
            pompAssigns++;
        }

        void SendPompOwner(PompAppointment a, byte sub, Action<BinaryWriter> extra)
        {
            byte[] packet = Wire.Packet(Op.PompToClient, w => { w.Write(epoch); w.Write(sub); w.Write(a.Index); w.Write(a.Token); if (extra != null) extra(w); });
            pompLastOwnerPacket = packet;
            if (a.Peer != self && peers.ContainsKey(a.Peer)) Network.Send(a.Peer, packet);
        }

        internal void PompHeadToClass(NoLateTeacher pomp, PompAppointment a)
        {
            pomp.Navigator.SetSpeed(PompGet<float>(pomp, "runSpeed"));
            float classTime = PompGet<float>(pomp, "classTime");
            a.Phase = 2; a.Time = classTime; a.Initial = classTime;
            pomp.behaviorStateMachine.ChangeState(new TogetherPompWaiting(pomp, a));
        }

        bool PompPoseInRoom(PompAppointment a)
        {
            Peer peer;
            if (!peers.TryGetValue(a.Peer, out peer) || peer.InLocker || peer.Eliminated || !peer.HasPose || Time.realtimeSinceStartup - peer.PoseAt > 2) return false;
            var cell = a.Pomp.ec.CellFromPosition(peer.Position);
            return cell != null && cell.room == a.Room;
        }

        // Arrival needs both the owner's report and the host's view of the remote pose in the room.
        internal bool PompArrived(PompAppointment a) { return Time.realtimeSinceStartup - a.ClaimAt <= 3 && PompPoseInRoom(a); }
        internal bool PompInClass(PompAppointment a) { return PompPoseInRoom(a); }

        internal void PompInTime(NoLateTeacher pomp, PompAppointment a, int multiplier)
        {
            pompAppointments.Remove(a);
            pomp.behaviorStateMachine.ChangeState(new NoLateTeacher_Cooldown(pomp, pomp.Cooldown));
            pomp.behaviorStateMachine.ChangeNavigationState(new NavigationState_WanderRounds(pomp, 0));
            PompGet<AudioManager>(pomp, "audMan").PlaySingle(PompGet<SoundObject>(pomp, "audInTime"));
            pomp.Dismiss();
            int points = Mathf.Max(0, multiplier) * PompGet<int>(pomp, "successPoints");
            // Host award path: player 0 on the host is the shared team wallet (ShopSync replicates it).
            Singleton<CoreGameManager>.Instance.AddPoints(points, 0, true);
            SendPompOwner(a, PompCmdInTime, w => w.Write(points));
            pompRewards++;
        }

        internal void PompTimeOut(NoLateTeacher pomp, PompAppointment a)
        {
            a.Phase = 3;
            PompGet<SpriteRenderer>(pomp, "sprite").gameObject.SetActive(false);
            pomp.Navigator.SetSpeed(PompGet<float>(pomp, "angrySpeed"));
            PompGet<Collider>(pomp, "trigger").enabled = false;
            // The native "time's up" plays on the late player's own CoreGameManager audio: owner side.
            SendPompOwner(a, PompCmdTimedOut, null);
            pomp.behaviorStateMachine.ChangeState(new TogetherPompTimeOut(pomp, a));
            pompTimeouts++;
        }

        void PompAttack(NoLateTeacher pomp, PompAppointment a)
        {
            var sprite = PompGet<SpriteRenderer>(pomp, "sprite");
            pomp.Navigator.SetSpeed(PompGet<float>(pomp, "runSpeed"));
            sprite.gameObject.SetActive(true);
            PompGet<Collider>(pomp, "trigger").enabled = true;
            a.Phase = 4; a.Attached = true; a.Dragging = false;
            SendPompOwner(a, PompCmdAttach, null);
            pomp.ec.MakeNoise(pomp.ec.RealRoomMid(a.Room), PompGet<int>(pomp, "noiseVal"));
            sprite.sprite = PompGet<Sprite>(pomp, "angrySprite");
            PompGet<AudioManager>(pomp, "audMan").PlaySingle(PompGet<SoundObject>(pomp, "audScream"));
            pomp.behaviorStateMachine.ChangeState(new TogetherPompAttackDelay(pomp, a, PompGet<float>(pomp, "attackDelay")));
            pompAttacks++;
        }

        internal bool PompCanDrag(NoLateTeacher pomp, PompAppointment a)
        { return !a.InLocker && Vector3.Distance(pomp.transform.position, a.Position) <= PompGet<float>(pomp, "minDragDistance") + PompDragTolerance; }

        // Native Drag decides whether the pull continues; the owner computes the pull vector itself.
        internal bool PompDrag(NoLateTeacher pomp, PompAppointment a)
        {
            float distance = Vector3.Distance(pomp.transform.position, a.Position);
            a.Dragging = !(distance > PompGet<float>(pomp, "dragBreakDistance") || pomp.Navigator.Entity.InteractionDisabled || a.InLocker);
            return a.Dragging;
        }

        internal bool PompTooFar(NoLateTeacher pomp, PompAppointment a)
        { return Vector3.Distance(pomp.transform.position, a.Position) > PompGet<float>(pomp, "maxDragDistance"); }

        internal void PompTeach(NoLateTeacher pomp, PompAppointment a)
        {
            a.Dragging = false; a.Attached = false;
            pompAppointments.Remove(a);
            SendPompOwner(a, PompCmdRelease, null);
            float teach = PompGet<float>(pomp, "angryTeachTime");
            foreach (Door door in a.Room.doors) { door.Shut(); door.LockTimed(teach); }
            pomp.ec.MakeNoise(pomp.ec.RealRoomMid(a.Room), PompGet<int>(pomp, "noiseVal"));
            pomp.behaviorStateMachine.ChangeState(new NoLateTeacher_AngryTeach(pomp, teach));
            pompTeaches++;
        }

        void CancelPompAppointment(PompAppointment a, bool notify)
        {
            pompAppointments.Remove(a);
            if (notify) SendPompOwner(a, PompCmdCancel, null);
            pompCancels++;
            var pomp = a.Pomp;
            if (pomp == null) return;
            var state = pomp.behaviorStateMachine.CurrentState as TogetherPompState;
            if (state == null || state.Appt != a) return;
            var nav = pomp.behaviorStateMachine.CurrentNavigationState;
            if (nav != null) nav.priority = 0;
            var sprite = PompGet<SpriteRenderer>(pomp, "sprite");
            sprite.gameObject.SetActive(true); sprite.sprite = PompGet<Sprite>(pomp, "normalSprite");
            PompGet<Collider>(pomp, "trigger").enabled = true;
            pomp.behaviorStateMachine.ChangeState(new NoLateTeacher_Cooldown(pomp, pomp.Cooldown));
            pomp.behaviorStateMachine.ChangeNavigationState(new NavigationState_WanderRounds(pomp, 0));
        }

        // Native Dismiss hides the map icon; a host-side remote appointment never created one.
        public bool PompDismissNative(NoLateTeacher pomp)
        {
            if (!Active || PompGet<NoLateIcon>(pomp, "mapIcon") != null) return true;
            PompGet<SpriteRenderer>(pomp, "sprite").sprite = PompGet<Sprite>(pomp, "normalSprite");
            PompGet<AudioManager>(pomp, "audMan").QueueAudio(PompGet<SoundObject>(pomp, "audDismissed"));
            return false;
        }

        bool HandlePompClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.PompToHost) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte(); int index = r.ReadInt32(), token = r.ReadInt32();
            Vector3 door = Vector3.zero;
            if (sub == PompReportDoor) door = ReadVector(r);
            else if (sub != PompReportArrived) throw new IOException("Invalid Pomp report.");
            if (generation != epoch || !Hosting || !worldReady || !sender.Ready || sender.Eliminated || sender.InLocker) return true;
            var a = pompAppointments.FirstOrDefault(x => x.Index == index && x.Peer == id && x.Token == token);
            if (a == null || a.Pomp == null) return true;
            if (sub == PompReportArrived) { if (a.Phase == 2) a.ClaimAt = Time.realtimeSinceStartup; return true; }
            var state = a.Pomp.behaviorStateMachine.CurrentState as TogetherPompTimeOut;
            if (a.Phase != 3 || state == null || state.TargetedDoor == null || state.TargetedDoor.locked || a.Pomp.Navigator.HasDestination) return true;
            if ((state.TargetedDoor.transform.position - door).sqrMagnitude > 2.25f || (sender.Position - door).sqrMagnitude > 400) return true;
            PompAttack(a.Pomp, a);
            return true;
        }

        // ---------------- Owner client: native appointment UI, arrival report and drag ----------------

        bool HandlePompHost(Op op, BinaryReader r)
        {
            if (op != Op.PompToClient) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte(); int index = r.ReadInt32(), token = r.ReadInt32();
            int roomIndex = -1, points = 0; float classTime = 0;
            if (sub == PompCmdAssign)
            {
                roomIndex = r.ReadInt32(); classTime = Wire.Number(r);
                if (classTime < 1 || classTime > 3600) throw new IOException("Invalid Pomp class time.");
            }
            else if (sub == PompCmdInTime) { points = r.ReadInt32(); if (points < 0 || points > 1000000) throw new IOException("Invalid Pomp reward."); }
            else if (sub < PompCmdTimedOut || sub > PompCmdCancel) throw new IOException("Invalid Pomp command.");
            if (token < 1) throw new IOException("Invalid Pomp token.");
            var manager = Singleton<BaseGameManager>.Instance; var core = Singleton<CoreGameManager>.Instance;
            if (generation != epoch || !worldReady || (!Hosting && !readyAcknowledged) || manager == null || core == null) return true;
            if (sub == PompCmdAssign)
            {
                if (!ApplyPompAssign(index, token, roomIndex, classTime) && pompPending.Count < 16)
                    pompPending.Add(new PompPendingAssign { Generation = generation, Index = index, Token = token, Room = roomIndex, ClassTime = classTime, Until = Time.realtimeSinceStartup + 5 });
                return true;
            }
            var local = pompLocal.FirstOrDefault(l => l.Index == index && l.Token == token);
            if (local == null || local.Pomp == null) return true;
            var player = core.GetPlayer(0);
            Applying = true;
            try
            {
                if (sub == PompCmdInTime)
                {
                    PompGet<AudioManager>(local.Pomp, "audMan").PlaySingle(PompGet<SoundObject>(local.Pomp, "audInTime"));
                    PompGet<AudioManager>(local.Pomp, "audMan").QueueAudio(PompGet<SoundObject>(local.Pomp, "audDismissed"));
                    pompOwnerPoints += points;
                    ReleasePompLocal(local, false);
                }
                else if (sub == PompCmdTimedOut)
                {
                    if (local.Phase < 3)
                    {
                        if (local.Icon != null) local.Icon.gameObject.SetActive(false);
                        core.audMan.PlaySingle(PompGet<SoundObject>(local.Pomp, "audTimesUp"));
                    }
                    local.Phase = 3;
                }
                else if (sub == PompCmdAttach)
                {
                    local.Phase = 4;
                    if (local.Attached == null && !LocalEliminated && player != null)
                    {
                        local.Mod = PompGet<MovementModifier>(local.Pomp, "moveMod");
                        local.Mod.movementAddend = Vector3.zero; local.Mod.movementMultiplier = 1;
                        local.Attached = player.plm.am; local.Attached.moveMods.Add(local.Mod);
                        PompGet<AudioManager>(local.Pomp, "audMan").PlaySingle(PompGet<SoundObject>(local.Pomp, "audScream"));
                    }
                }
                else ReleasePompLocal(local, false);
            }
            finally { Applying = false; }
            return true;
        }

        // False only when this client has no replica at that index yet, so the caller can retry.
        bool ApplyPompAssign(int index, int token, int roomIndex, float classTime)
        {
            var manager = Singleton<BaseGameManager>.Instance;
            if (LocalEliminated || index < 0 || manager == null || manager.Ec == null) return true;
            if (index >= npcs.Length || npcs[index] == null) return false;
            var pomp = npcs[index] as NoLateTeacher; var ec = manager.Ec;
            if (pomp == null || roomIndex < 0 || roomIndex >= ec.rooms.Count || ec.rooms[roomIndex].category != RoomCategory.Class) return true;
            if (pompAppliedTokens.Count >= 4096) pompAppliedTokens.Clear();
            if (!pompAppliedTokens.Add(token)) return true;
            foreach (var old in pompLocal.Where(l => l.Pomp == pomp).ToArray()) ReleasePompLocal(old, false);
            BeginPompLocal(pomp, index, token, ec.rooms[roomIndex], classTime);
            return true;
        }

        void RetryPompAssigns()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var p in pompPending.ToArray())
            {
                if (p.Generation != epoch || now > p.Until) { pompPending.Remove(p); continue; }
                if (!ApplyPompAssign(p.Index, p.Token, p.Room, p.ClassTime)) continue;
                pompPending.Remove(p); pompLateAssigns++;
            }
        }

        // The UI half of native PlayerCaught, on the owner's replica of the same NoLateTeacher.
        void BeginPompLocal(NoLateTeacher pomp, int index, int token, RoomController room, float classTime)
        {
            var ec = pomp.ec; var core = Singleton<CoreGameManager>.Instance;
            Applying = true;
            try
            {
                PompSet(pomp, "classRoom", room);
                for (int i = 0; i < room.TileCount; i++)
                { var cell = room.TileAtIndex(i); ec.map.Find(cell.position.x, cell.position.z, cell.ConstBin, room); }
                var mid = ec.CellFromPosition(IntVector2.GetGridPosition(ec.RealRoomMid(room)));
                if (mid != null) ec.map.Find(mid.position.x, mid.position.z, mid.ConstBin, room);
                var target = new GameObject("Pomp appointment");
                if (room.objectObject != null) target.transform.parent = room.objectObject.transform;
                target.transform.position = ec.RealRoomMid(room);
                var icon = ec.map.AddIcon(PompGet<MapIcon>(pomp, "mapIconPre"), target.transform, Color.white).GetComponent<NoLateIcon>();
                icon.timeText.text = Mathf.Floor(classTime / 60f).ToString("0") + ":" + (classTime % 60f).ToString("00");
                PompSet(pomp, "mapIcon", icon);
                var canvas = PompGet<Canvas>(pomp, "popupCanvas");
                canvas.worldCamera = core.GetCamera(0).canvasCam;
                canvas.transform.SetParent(null);
                canvas.gameObject.SetActive(true);
                QueuePompIntro(pomp, classTime);
                pompLocal.Add(new PompLocal { Pomp = pomp, Room = room, Icon = icon, Index = index, Token = token, Time = classTime, Total = classTime, AssignedAt = Time.realtimeSinceStartup });
                pompOwnerAssigns++;
            }
            finally { Applying = false; }
        }

        void ReleasePompLocal(PompLocal local, bool reset)
        {
            pompLocal.Remove(local);
            if (local.Mod != null) { local.Mod.movementAddend = Vector3.zero; local.Mod.movementMultiplier = 1; }
            if (local.Attached != null && local.Mod != null) local.Attached.moveMods.Remove(local.Mod);
            local.Attached = null;
            if (local.Icon != null) local.Icon.gameObject.SetActive(false);
            if (reset && local.Pomp != null)
            {
                // Native PlayerCaught detaches the popup canvas from Pomp; hide it when the session ends.
                var canvas = PompGet<Canvas>(local.Pomp, "popupCanvas");
                if (canvas != null) canvas.gameObject.SetActive(false);
            }
        }

        void UpdatePompOwner(EnvironmentController ec)
        {
            if (pompLocal.Count == 0) return;
            var core = Singleton<CoreGameManager>.Instance;
            var player = core == null ? null : core.GetPlayer(0);
            if (player == null) return;
            float now = Time.realtimeSinceStartup;
            foreach (var local in pompLocal.ToArray())
            {
                if (local.Pomp == null || LocalEliminated) { ReleasePompLocal(local, false); continue; }
                if (local.Phase == 2)
                {
                    local.Time = Mathf.Max(0, local.Time - Time.deltaTime * local.Pomp.TimeScale);
                    if (PompGet<NoLateIcon>(local.Pomp, "mapIcon") == local.Icon) local.Pomp.UpdateTimer(local.Time);
                    var cell = ec.CellFromPosition(player.transform.position);
                    if (!Hosting && cell != null && cell.room == local.Room && !player.plm.Entity.InteractionDisabled && player.plm.Entity.InBounds && now >= local.NextClaim)
                    { local.NextClaim = now + .5f; SendPompReport(local, PompReportArrived, null); }
                }
                if (local.Attached != null && local.Mod != null)
                {
                    Vector3 delta = local.Pomp.transform.position - player.transform.position;
                    float distance = delta.magnitude;
                    if (local.Dragging && distance <= PompGet<float>(local.Pomp, "dragBreakDistance"))
                    {
                        local.Mod.movementAddend = delta.normalized * (PompGet<float>(local.Pomp, "dragSpeed") + distance) * local.Pomp.TimeScale;
                        local.Mod.movementMultiplier = PompGet<float>(local.Pomp, "dragMultiplier");
                        pompOwnerDragFrames++;
                    }
                    else { local.Mod.movementAddend = Vector3.zero; local.Mod.movementMultiplier = 1; }
                }
            }
        }

        void SendPompReport(PompLocal local, byte sub, Action<BinaryWriter> extra)
        {
            if (Hosting) return;
            Network.Send(0, Wire.Packet(Op.PompToHost, w => { w.Write(epoch); w.Write(sub); w.Write(local.Index); w.Write(local.Token); if (extra != null) extra(w); }));
        }

        // Native TimeOut attacks when the late player clicks the door Pomp waits behind.
        public void ReportPompClick(PlayerClick click)
        {
            if (!Active || Hosting || pompLocal.Count == 0 || click == null || click.clickedThisFrame == null) return;
            var core = Singleton<CoreGameManager>.Instance;
            if (core == null || core.GetPlayer(0) == null || core.GetPlayer(0).pc != click) return;
            var door = click.clickedThisFrame.GetComponent<Door>();
            if (door == null) return;
            Vector3 position = door.transform.position;
            foreach (var local in pompLocal) if (local.Phase == 3) SendPompReport(local, PompReportDoor, w => WriteVector(w, position));
        }

        // ---------------- World section ----------------

        void WritePompWorld(BinaryWriter w)
        {
            var list = pompAppointments.Where(a => a.Pomp != null).ToList();
            if (list.Count > 16) throw new IOException("Pomp appointment limit exceeded.");
            w.Write((byte)list.Count);
            foreach (var a in list)
            { w.Write(a.Index); w.Write(a.Peer); w.Write(a.Token); w.Write(a.Phase); w.Write(Mathf.Max(0, a.Time)); w.Write(a.Initial); w.Write(a.Dragging); }
        }

        void ReadPompWorld(BinaryReader r, bool apply)
        {
            int count = r.ReadByte();
            if (count > 16) throw new IOException("Pomp appointment limit exceeded.");
            var seen = new HashSet<PompLocal>();
            for (int i = 0; i < count; i++)
            {
                int index = r.ReadInt32(), peer = r.ReadInt32(), token = r.ReadInt32(); byte phase = r.ReadByte();
                float time = Wire.Number(r), total = Wire.Number(r); bool dragging = r.ReadBoolean();
                if (peer < 0 || token < 1 || phase < 1 || phase > 4 || total < 1 || total > 3600 || time < 0 || time > total + 1) throw new IOException("Invalid Pomp appointment.");
                if (!apply || peer != self) continue;
                var local = pompLocal.FirstOrDefault(l => l.Index == index && l.Token == token);
                if (local == null) continue;
                seen.Add(local);
                if (phase >= 3 && local.Phase < 3 && local.Icon != null) local.Icon.gameObject.SetActive(false);
                local.Phase = phase; local.Time = time; local.Total = total; local.Dragging = dragging;
            }
            if (!apply) return;
            float now = Time.realtimeSinceStartup;
            foreach (var local in pompLocal.ToArray())
                if (!seen.Contains(local) && now - local.AssignedAt > 1.5f) ReleasePompLocal(local, false);
        }

        string PompDiagnostic()
        {
            return ",\"pompRemoteAssigns\":" + pompAssigns + ",\"pompRemoteRewards\":" + pompRewards + ",\"pompRemoteTimeouts\":" + pompTimeouts
                + ",\"pompRemoteAttacks\":" + pompAttacks + ",\"pompRemoteTeaches\":" + pompTeaches + ",\"pompCancels\":" + pompCancels
                + ",\"pompAppointments\":" + pompAppointments.Count + ",\"pompOwnerAssigns\":" + pompOwnerAssigns + ",\"pompLateAssigns\":" + pompLateAssigns + ",\"pompOwnerAppointments\":" + pompLocal.Count
                + ",\"pompOwnerDragFrames\":" + pompOwnerDragFrames + ",\"pompOwnerPoints\":" + pompOwnerPoints;
        }

        // ---------------- Lab tour ----------------

        void UpdatePompLab()
        {
            if (!Lab.Enabled || pompLabStarted || !TitleVisible || !Lab.HasArg("-bt-pomp-tour")) return;
            pompLabStarted = true; StartCoroutine(TourPomp());
        }

        void PompLabOwner(byte[] packet)
        { using (var reader = new BinaryReader(new MemoryStream(packet))) { reader.ReadByte(); HandlePompHost(Op.PompToClient, reader); } }

        void PompLabReport(Peer sender, int generation, byte sub, int index, int token, Vector3 door)
        {
            byte[] packet = Wire.Packet(Op.PompToHost, w => { w.Write(generation); w.Write(sub); w.Write(index); w.Write(token); if (sub == PompReportDoor) WriteVector(w, door); });
            using (var reader = new BinaryReader(new MemoryStream(packet))) { reader.ReadByte(); HandlePompClient(sender.Id, sender, Op.PompToHost, reader); }
        }

        IEnumerator TourPomp()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); if (!Active) yield break; AutoStartRun();
            while (!worldReady) yield return null;
            yield return new WaitForSecondsRealtime(2);
            var ec = Singleton<BaseGameManager>.Instance.Ec; var core = Singleton<CoreGameManager>.Instance; var player = core.GetPlayer(0);
            // Halls: ec.mainHall may be outside ec.rooms; accept RoomType.Hall or RoomCategory.Hall.
            var allRooms = ec.rooms.Where(r => r != null).ToList();
            if (ec.mainHall != null && !allRooms.Contains(ec.mainHall)) allRooms.Add(ec.mainHall);
            var halls = allRooms.Where(r => r.type == RoomType.Hall || r.category == RoomCategory.Hall).ToList();
            Cell spot = null; float farthest = -1; int hallCells = 0;
            foreach (var room in halls)
                for (int i = 0; i < room.TileCount; i++)
                {
                    var cell = room.TileAtIndex(i);
                    if (cell == null || !ec.CheckPath(player.transform.position, cell.CenterWorldPosition, PathType.Nav)) continue;
                    hallCells++;
                    float d = (cell.CenterWorldPosition - player.transform.position).sqrMagnitude;
                    if (d > farthest) { farthest = d; spot = cell; }
                }
            if (spot == null) spot = ec.CellFromPosition(player.transform.position);
            Log("LAB Pomp setup: rooms=" + ec.rooms.Count + " halls=" + halls.Count + " reachableHallCells=" + hallCells + " spot=" + (spot == null ? "none" : spot.position.x + "," + spot.position.z)
                + " floorPomps=" + ec.Npcs.OfType<NoLateTeacher>().Count());
            var pomp = ec.Npcs.OfType<NoLateTeacher>().FirstOrDefault();
            if (pomp == null)
            {
                var prefab = Resources.FindObjectsOfTypeAll<NoLateTeacher>().FirstOrDefault(n => !n.gameObject.scene.IsValid());
                if (prefab != null && spot != null) pomp = (NoLateTeacher)ec.SpawnNPC(prefab, spot.position);
            }
            CheckNativeUi("pomp-prefab-found", pomp != null);
            CheckNativeUi("pomp-spawn-cell-found", spot != null);
            if (pomp == null || spot == null) { WriteLabResults("pomp-results.json", "Pomp"); yield break; }
            npcs = ec.Npcs.ToArray();
            pomp.Navigator.Entity.Teleport(spot.CenterWorldPosition);
            pomp.behaviorStateMachine.ChangeState(new NoLateTeacher_Wander(pomp));
            var sprite = PompGet<SpriteRenderer>(pomp, "sprite"); var trigger = PompGet<Collider>(pomp, "trigger");
            int successPoints = PompGet<int>(pomp, "successPoints"); float classTime = PompGet<float>(pomp, "classTime");
            bool hostTagged = peers[self].Tagged; peers[self].Tagged = true; // keep the host player out of NearestVisiblePeer
            var remote = new Peer { Id = 3, Name = "Late", Accepted = true, Ready = true, HasPose = true, PoseAt = Time.realtimeSinceStartup, Position = pomp.transform.position + pomp.transform.forward };
            peers[3] = remote;

            // 1. Eliminated and locker players are never targeted.
            remote.Eliminated = true; UpdatePompHost(ec); bool skippedDead = pompAppointments.Count == 0;
            remote.Eliminated = false; remote.InLocker = true; UpdatePompHost(ec); bool skippedLocker = pompAppointments.Count == 0;
            remote.InLocker = false;
            CheckNativeUi("pomp-eliminated-and-locker-ignored", skippedDead && skippedLocker && pomp.behaviorStateMachine.CurrentState.GetType() == typeof(NoLateTeacher_Wander));

            // 2. Host catch of a remote player assigns a classroom.
            remote.PoseAt = Time.realtimeSinceStartup; UpdatePompHost(ec);
            var appt = pompAppointments.FirstOrDefault(a => a.Peer == 3);
            var peerCell = ec.CellFromPosition(remote.Position);
            CheckNativeUi("pomp-remote-catch-assigns-classroom", appt != null && appt.Room.category == RoomCategory.Class && (peerCell == null || appt.Room != peerCell.room)
                && pomp.behaviorStateMachine.CurrentState is TogetherPompInform && pomp.TargetClassRoom == appt.Room && pompLastOwnerPacket != null);
            if (appt == null) { peers.Remove(3); peers[self].Tagged = hostTagged; WriteLabResults("pomp-results.json", "Pomp"); yield break; }

            // 3-4. Owner shows the native appointment UI exactly once.
            byte[] assign = pompLastOwnerPacket;
            PompLabOwner(assign);
            var local = pompLocal.FirstOrDefault();
            var canvas = PompGet<Canvas>(pomp, "popupCanvas");
            string expected = Mathf.Floor(classTime / 60f).ToString("0") + ":" + (classTime % 60f).ToString("00");
            CheckNativeUi("pomp-owner-native-appointment-ui", local != null && local.Icon != null && local.Icon.gameObject.activeSelf && local.Icon.timeText.text == expected
                && canvas.gameObject.activeSelf && PompGet<NoLateIcon>(pomp, "mapIcon") == local.Icon);
            var icons = NpcField<List<MapIcon>>(ec.map, "icons"); int iconCount = icons.Count;
            PompLabOwner(assign);
            CheckNativeUi("pomp-owner-assign-applied-once", pompLocal.Count == 1 && icons.Count == iconCount);

            // 5-7. Waiting: stale packets and outside-room claims do nothing; a valid arrival pays once.
            PompHeadToClass(pomp, appt);
            int before = core.GetPoints(0);
            int roomIndex = ec.rooms.IndexOf(appt.Room);
            PompLabOwner(Wire.Packet(Op.PompToClient, w => { w.Write(epoch + 1); w.Write(PompCmdAssign); w.Write(appt.Index); w.Write(appt.Token + 1000); w.Write(roomIndex); w.Write(classTime); }));
            Vector3 inside = ec.RealRoomMid(appt.Room); inside.y = remote.Position.y;
            Vector3 outside = remote.Position;
            remote.Position = inside; remote.PoseAt = Time.realtimeSinceStartup;
            PompLabReport(remote, epoch + 1, PompReportArrived, appt.Index, appt.Token, Vector3.zero);
            PompLabReport(remote, epoch, PompReportArrived, appt.Index, appt.Token + 1, Vector3.zero);
            yield return null; remote.PoseAt = Time.realtimeSinceStartup; yield return null;
            CheckNativeUi("pomp-stale-packets-ignored", pompLocal.Count == 1 && icons.Count == iconCount && pomp.behaviorStateMachine.CurrentState is TogetherPompWaiting && core.GetPoints(0) == before);
            remote.Position = outside; remote.PoseAt = Time.realtimeSinceStartup;
            PompLabReport(remote, epoch, PompReportArrived, appt.Index, appt.Token, Vector3.zero);
            yield return null; remote.PoseAt = Time.realtimeSinceStartup; yield return null;
            CheckNativeUi("pomp-arrival-outside-room-rejected", pomp.behaviorStateMachine.CurrentState is TogetherPompWaiting && core.GetPoints(0) == before && pompAppointments.Contains(appt));
            remote.Position = inside; remote.PoseAt = Time.realtimeSinceStartup;
            PompLabReport(remote, epoch, PompReportArrived, appt.Index, appt.Token, Vector3.zero);
            for (int i = 0; i < 10 && pomp.behaviorStateMachine.CurrentState is TogetherPompWaiting; i++) { remote.PoseAt = Time.realtimeSinceStartup; yield return null; }
            int gained = core.GetPoints(0) - before; int afterReward = core.GetPoints(0);
            byte[] inTime = pompLastOwnerPacket;
            PompLabReport(remote, epoch, PompReportArrived, appt.Index, appt.Token, Vector3.zero);
            yield return null;
            CheckNativeUi("pomp-on-time-reward-once", gained > 0 && successPoints > 0 && gained % successPoints == 0 && pomp.behaviorStateMachine.CurrentState is NoLateTeacher_Cooldown
                && !pompAppointments.Contains(appt) && core.GetPoints(0) == afterReward);

            // 8. Owner clears its UI on the in-time result.
            var firstIcon = local == null ? null : local.Icon;
            PompLabOwner(inTime);
            CheckNativeUi("pomp-owner-in-time-clears-ui", pompLocal.Count == 0 && firstIcon != null && !firstIcon.gameObject.activeSelf);

            // 9. Late: timer runs out, Pomp hides; the owner gets the time's-up.
            pomp.behaviorStateMachine.ChangeState(new NoLateTeacher_Wander(pomp));
            remote.Position = pomp.transform.position + pomp.transform.forward; remote.PoseAt = Time.realtimeSinceStartup;
            UpdatePompHost(ec);
            var late = pompAppointments.FirstOrDefault(a => a.Peer == 3);
            PompLocal lateLocal = null;
            if (late != null)
            {
                PompLabOwner(pompLastOwnerPacket); lateLocal = pompLocal.FirstOrDefault();
                PompHeadToClass(pomp, late); late.Time = .001f;
                yield return null; remote.PoseAt = Time.realtimeSinceStartup; yield return null;
                if (lateLocal != null) PompLabOwner(pompLastOwnerPacket);
            }
            CheckNativeUi("pomp-late-timeout-hides-pomp", late != null && lateLocal != null && pomp.behaviorStateMachine.CurrentState is TogetherPompTimeOut && !sprite.gameObject.activeSelf
                && !trigger.enabled && late.Phase == 3 && lateLocal.Phase == 3 && !lateLocal.Icon.gameObject.activeSelf);

            // 10. The late player opens the door Pomp waits behind: ambush (a wrong token is ignored).
            bool ambushed = false; MovementModifier mod = PompGet<MovementModifier>(pomp, "moveMod");
            var hideRoom = late == null ? null : ec.rooms.FirstOrDefault(room => room.type != RoomType.Hall && room != late.Room && room.doors.Any(d => d.bTile.room.type == RoomType.Hall));
            if (hideRoom != null)
            {
                Vector3 hidden = ec.RealRoomMid(hideRoom); hidden.y = remote.Position.y;
                remote.Position = hidden; remote.PoseAt = Time.realtimeSinceStartup;
                yield return null;
                var timeout = pomp.behaviorStateMachine.CurrentState as TogetherPompTimeOut;
                if (timeout != null && timeout.TargetedDoor != null)
                {
                    pomp.Navigator.Entity.Teleport(timeout.TargetedDoor.bTile.CenterWorldPosition);
                    float until = Time.realtimeSinceStartup + 4;
                    while (Time.realtimeSinceStartup < until && pomp.Navigator.HasDestination) { remote.PoseAt = Time.realtimeSinceStartup; yield return null; }
                    remote.Position = timeout.TargetedDoor.aTile.CenterWorldPosition; remote.PoseAt = Time.realtimeSinceStartup;
                    Vector3 doorPosition = timeout.TargetedDoor.transform.position;
                    PompLabReport(remote, epoch, PompReportDoor, late.Index, late.Token + 1, doorPosition);
                    bool wrongIgnored = pomp.behaviorStateMachine.CurrentState is TogetherPompTimeOut;
                    if (timeout.TargetedDoor.locked) timeout.TargetedDoor.Unlock();
                    PompLabReport(remote, epoch, PompReportDoor, late.Index, late.Token, doorPosition);
                    ambushed = wrongIgnored && pomp.behaviorStateMachine.CurrentState is TogetherPompAttackDelay && late.Attached && late.Phase == 4 && sprite.gameObject.activeSelf;
                }
            }
            CheckNativeUi("pomp-door-click-ambush-attacks", ambushed);

            // 11. Owner attaches the native drag modifier exactly once.
            byte[] attach = pompLastOwnerPacket;
            if (ambushed) { PompLabOwner(attach); PompLabOwner(attach); }
            CheckNativeUi("pomp-owner-drag-attached-once", ambushed && player.plm.am.moveMods.Count(m => m == mod) == 1 && lateLocal.Phase == 4);

            // 12. World section round trip drives the owner's timer and drag flag.
            bool roundTrip = false;
            if (late != null && lateLocal != null)
            {
                late.Dragging = true; late.Time = 42; int realPeer = late.Peer; late.Peer = self;
                byte[] data;
                using (var stream = new MemoryStream()) { using (var writer = new BinaryWriter(stream)) WritePompWorld(writer); data = stream.ToArray(); }
                late.Peer = realPeer; lateLocal.Dragging = false;
                using (var stream = new MemoryStream(data))
                using (var reader = new BinaryReader(stream)) { ReadPompWorld(reader, true); roundTrip = stream.Position == stream.Length; }
                roundTrip &= lateLocal.Dragging && lateLocal.Phase == late.Phase && Mathf.Abs(lateLocal.Time - 42) < .01f && pompLocal.Contains(lateLocal);
            }
            CheckNativeUi("pomp-world-section-round-trip", roundTrip);

            // 13. Owner pull points from the player toward Pomp, as native Drag.
            bool pulls = false;
            if (ambushed && lateLocal != null)
            {
                pomp.transform.position = player.transform.position + player.transform.forward * 4;
                lateLocal.Dragging = true; UpdatePompOwner(ec);
                Vector3 toward = pomp.transform.position - player.transform.position;
                pulls = Vector3.Dot(mod.movementAddend, toward) > 0 && Mathf.Abs(mod.movementMultiplier - PompGet<float>(pomp, "dragMultiplier")) < .001f;
            }
            CheckNativeUi("pomp-owner-drag-pulls-toward-pomp", pulls);

            // 14. Disconnect restores the host Pomp.
            peers.Remove(3); UpdatePompHost(ec);
            CheckNativeUi("pomp-disconnect-restores-pomp", pompAppointments.Count == 0 && pomp.behaviorStateMachine.CurrentState is NoLateTeacher_Cooldown && sprite.gameObject.activeSelf
                && trigger.enabled && sprite.sprite == PompGet<Sprite>(pomp, "normalSprite"));

            // 15. Reset releases the owner's drag and UI (balanced moveMods).
            ResetPomp();
            CheckNativeUi("pomp-reset-releases-owner-drag", pompLocal.Count == 0 && !player.plm.am.moveMods.Contains(mod) && mod.movementAddend == Vector3.zero && !canvas.gameObject.activeSelf);

            peers[self].Tagged = hostTagged;
            WriteLabResults("pomp-results.json", "Pomp");
        }
    }

    // ---------------- Host states for a remote appointment (mirror the native NoLateTeacher_* states) ----------------

    abstract class TogetherPompState : NoLateTeacher_StateBase
    {
        public readonly PompAppointment Appt;
        protected TogetherPompState(NoLateTeacher pomp, PompAppointment appt) : base(pomp) { Appt = appt; }
        protected static TogetherRuntime Runtime { get { return ModHooks.Current; } }
    }

    sealed class TogetherPompInform : TogetherPompState
    {
        float time;
        public TogetherPompInform(NoLateTeacher pomp, PompAppointment appt, float time) : base(pomp, appt) { this.time = time; }
        public override void Update()
        {
            base.Update();
            time -= Time.deltaTime;
            var runtime = Runtime;
            if (time <= 0f && !pomp.IsSpeaking && runtime != null) runtime.PompHeadToClass(pomp, Appt);
        }
    }

    sealed class TogetherPompWaiting : TogetherPompState
    {
        bool wandering;
        public TogetherPompWaiting(NoLateTeacher pomp, PompAppointment appt) : base(pomp, appt) { }
        public override void Enter()
        {
            base.Enter();
            ChangeNavigationState(new NavigationState_TargetPosition(pomp, 63, pomp.GetClassPosition(), true));
        }
        public override void Update()
        {
            base.Update();
            var runtime = Runtime;
            if (runtime == null) return;
            if (runtime.PompArrived(Appt))
            {
                currentNavigationState.priority = 0;
                runtime.PompInTime(pomp, Appt, Mathf.CeilToInt(Appt.Time / Appt.Initial * 20f));
                return;
            }
            Appt.Time -= Time.deltaTime * npc.TimeScale;
            if (Appt.Time <= 0f)
            {
                Appt.Time = 0; currentNavigationState.priority = 0;
                runtime.PompTimeOut(pomp, Appt);
                return;
            }
            if (wandering && npc.ec.CheckPath(npc.transform.position, pomp.GetClassPosition(), PathType.Nav)) runtime.PompHeadToClass(pomp, Appt);
        }
        public override void DestinationEmpty()
        {
            base.DestinationEmpty();
            if (!wandering)
            {
                var cell = npc.ec.CellFromPosition(npc.transform.position);
                if (cell != null && cell.room == pomp.TargetClassRoom) pomp.WanderSpeed();
                currentNavigationState.UpdatePosition(pomp.GetClassPosition());
                if (!npc.Navigator.HasDestination)
                {
                    wandering = true;
                    currentNavigationState.priority = 0;
                    ChangeNavigationState(new NavigationState_WanderRandom(npc, 0));
                }
            }
            else
            {
                var runtime = Runtime;
                if (runtime != null) runtime.PompHeadToClass(pomp, Appt);
            }
        }
    }

    sealed class TogetherPompTimeOut : TogetherPompState
    {
        RoomController targetedRoom;
        readonly List<Door> potentialDoors = new List<Door>();
        public Door TargetedDoor;
        public TogetherPompTimeOut(NoLateTeacher pomp, PompAppointment appt) : base(pomp, appt) { }
        public override void Enter() { base.Enter(); pomp.AngrySpeed(); }
        public override void Update()
        {
            base.Update();
            var cell = npc.ec.CellFromPosition(Appt.Position);
            var room = cell == null ? null : cell.room;
            if (room != null && room.type != RoomType.Hall && room.doors.Count > 0)
            {
                if (room == targetedRoom) return;
                potentialDoors.Clear();
                foreach (Door door in room.doors)
                    if (door.bTile.room.type == RoomType.Hall && npc.ec.CheckPath(npc.transform.position, door.bTile.CenterWorldPosition, PathType.Nav)
                        && npc.ec.CheckPath(npc.transform.position, door.aTile.CenterWorldPosition, PathType.Nav))
                        potentialDoors.Add(door);
                if (potentialDoors.Count > 0)
                {
                    targetedRoom = room;
                    TargetedDoor = potentialDoors[UnityEngine.Random.Range(0, potentialDoors.Count)];
                    ChangeNavigationState(new NavigationState_TargetPosition(pomp, 127, TargetedDoor.bTile.CenterWorldPosition, true));
                }
                else { targetedRoom = null; TargetedDoor = null; }
            }
            else { targetedRoom = null; TargetedDoor = null; }
        }
    }

    sealed class TogetherPompAttackDelay : TogetherPompState
    {
        float delay;
        public TogetherPompAttackDelay(NoLateTeacher pomp, PompAppointment appt, float delay) : base(pomp, appt) { this.delay = delay; }
        public override void Update()
        {
            base.Update();
            delay -= Time.deltaTime * npc.TimeScale;
            if (delay <= 0f) npc.behaviorStateMachine.ChangeState(new TogetherPompReturning(pomp, Appt));
        }
    }

    sealed class TogetherPompReturning : TogetherPompState
    {
        public TogetherPompReturning(NoLateTeacher pomp, PompAppointment appt) : base(pomp, appt) { }
        public override void Enter()
        {
            base.Enter();
            Appt.Dragging = false;
            ChangeNavigationState(new NavigationState_TargetPosition(npc, 127, Appt.Position, true));
        }
        public override void Update()
        {
            base.Update();
            currentNavigationState.UpdatePosition(Appt.Position);
            var runtime = Runtime;
            if (runtime != null && runtime.PompCanDrag(pomp, Appt)) npc.behaviorStateMachine.ChangeState(new TogetherPompTriggered(pomp, Appt));
        }
    }

    sealed class TogetherPompTriggered : TogetherPompState
    {
        bool draggable = true;
        public TogetherPompTriggered(NoLateTeacher pomp, PompAppointment appt) : base(pomp, appt) { }
        public override void Enter()
        {
            base.Enter();
            ChangeNavigationState(new NavigationState_TargetPosition(npc, 127, pomp.TargetClassRoom.RandomEntitySafeCellNoGarbage().FloorWorldPosition));
        }
        public override void Update()
        {
            base.Update();
            var runtime = Runtime;
            if (runtime == null) return;
            if (draggable) draggable = runtime.PompDrag(pomp, Appt);
            else if (runtime.PompTooFar(pomp, Appt)) { npc.behaviorStateMachine.ChangeState(new TogetherPompReturning(pomp, Appt)); return; }
            if (runtime.PompInClass(Appt)) runtime.PompTeach(pomp, Appt);
        }
        public override void DestinationEmpty()
        {
            base.DestinationEmpty();
            npc.behaviorStateMachine.ChangeState(new TogetherPompReturning(pomp, Appt));
        }
    }

    [HarmonyPatch(typeof(NoLateTeacher), "Dismiss")]
    static class PompRemoteDismissPatch
    { static bool Prefix(NoLateTeacher __instance) { var r = ModHooks.Current; return r == null || r.PompDismissNative(__instance); } }

    [HarmonyPatch(typeof(PlayerClick), "Update")]
    static class PompDoorClickPatch
    { static void Postfix(PlayerClick __instance) { var r = ModHooks.Current; if (r != null && __instance.clickedThisFrame != null) r.ReportPompClick(__instance); } }
}
