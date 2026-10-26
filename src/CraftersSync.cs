using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // ArtsAndCrafters multiplayer sync. Owned by one feature; called from NpcFeatures.cs.
    // Ops: CraftersToClient = 72 (host to clients), CraftersToHost = 73 (client to host).
    //
    // Native flow (ArtsAndCrafters*.cs, CraftersTrigger.cs):
    //   Waiting --CraftersTrigger enter (spawnChance)--> Ready --nobody looking--> Stalking
    //   Stalking --crafters sees a player for RunTime--> Fleeing(player) --nobody looking--> Waiting
    //   Stalking --jealous player looks at it for AngryTime--> GetAngry -> Chasing(player)
    //   Chasing --touch--> Teleporting(player): Attack, spin + echoes for AttackTime, TeleportPlayer
    //   (player and Baldi moved along one elevator-to-main-hall path), DisappearForever.
    //
    // Multiplayer: the host runs the AI. Remote players' trigger entries are detected from their poses,
    // their gaze ("looking at the crafters", Looker.IsVisible) is reported by their client and checked
    // against host line of sight, and remote fleeing/chasing/attacking use the Together* states below.
    // The attack owner runs the native Attack/UpdateTeleportAnimation on its own player and applies the
    // teleport the host computed; the host moves Baldi. Everyone else draws the spin from the world section.
    //
    // CraftersToHost sub 1 gaze:   epoch, seq, npc index, looking
    // CraftersToClient sub 1 start: epoch, npc index, token
    // CraftersToClient sub 2 finish: epoch, npc index, token, teleport, destination
    // World section: count, per crafters: index, flags (1 gone, 2 attacking) [owner, token, angle, speed, echo, centre]
    static class CraftersNative
    {
        public static readonly FieldInfo VisibleRenderer = AccessTools.Field(typeof(ArtsAndCrafters), "visibleRenderer");
        public static readonly FieldInfo AttackAudio = AccessTools.Field(typeof(ArtsAndCrafters), "attackAudMan");
        public static readonly FieldInfo AudMan = AccessTools.Field(typeof(ArtsAndCrafters), "audMan");
        public static readonly FieldInfo Echo = AccessTools.Field(typeof(ArtsAndCrafters), "echo");
        public static readonly FieldInfo SpinDistance = AccessTools.Field(typeof(ArtsAndCrafters), "spinDistance");
        public static readonly FieldInfo Attacking = AccessTools.Field(typeof(ArtsAndCrafters), "attacking");
        public static readonly FieldInfo PlayerSpawnDistance = AccessTools.Field(typeof(ArtsAndCrafters), "playerSpawnDistance");
        public static readonly FieldInfo BaldiSpawnDistance = AccessTools.Field(typeof(ArtsAndCrafters), "baldiSpawnDistance");
        public static readonly FieldInfo NoiseValue = AccessTools.Field(typeof(ArtsAndCrafters), "noiseValue");
        public static readonly FieldInfo RunTime = AccessTools.Field(typeof(ArtsAndCrafters), "runTime");
        public static readonly FieldInfo AttackTime = AccessTools.Field(typeof(ArtsAndCrafters), "attackTime");
        public static readonly MethodInfo CreateTrigger = AccessTools.Method(typeof(ArtsAndCrafters), "CreateTrigger");
        public static readonly FieldInfo ReadyTime = AccessTools.Field(typeof(ArtsAndCrafters_Ready), "time");
        public static readonly FieldInfo StalkSight = AccessTools.Field(typeof(ArtsAndCrafters_Stalking), "timePlayerInSight");
        public static readonly FieldInfo StalkRespawn = AccessTools.Field(typeof(ArtsAndCrafters_Stalking), "timeLeftToRespawn");
        public static readonly FieldInfo TeleportPlayer = AccessTools.Field(typeof(ArtsAndCrafters_Teleporting), "player");
        public static readonly FieldInfo TeleportAngle = AccessTools.Field(typeof(ArtsAndCrafters_Teleporting), "currentAngle");
        public static readonly FieldInfo TeleportSpeed = AccessTools.Field(typeof(ArtsAndCrafters_Teleporting), "speed");
        public static readonly FieldInfo TeleportEcho = AccessTools.Field(typeof(ArtsAndCrafters_Teleporting), "echoDistance");
        public static readonly FieldInfo LookerMask = AccessTools.Field(typeof(Looker), "layerMask");
        public static readonly FieldInfo LookerHasFov = AccessTools.Field(typeof(Looker), "hasFov");
        public static readonly FieldInfo LookerFov = AccessTools.Field(typeof(Looker), "fieldOfView");
        public static readonly FieldInfo LookerBuffer = AccessTools.Field(typeof(Looker), "visibilityBuffer");
        public static readonly FieldInfo EntityActive = AccessTools.Field(typeof(Entity), "active");
        public static readonly FieldInfo EntityTargetLayer = AccessTools.Field(typeof(Entity), "targetLayer");
        public static readonly FieldInfo DijkstraMaps = AccessTools.Field(typeof(EnvironmentController), "activeDijkstraMaps");
        public static readonly FieldInfo FoundNotebooks = AccessTools.Field(typeof(BaseGameManager), "foundNotebooks");

        public static readonly FieldInfo DijkstraQueue = AccessTools.Field(typeof(EnvironmentController), "dijkstraMapsToUpdate");
        public static readonly FieldInfo DijkstraTargets = AccessTools.Field(typeof(DijkstraMap), "targets");

        // DijkstraMap.Deactivate only removes the map from activeDijkstraMaps. EnvironmentController.Update
        // dequeues one queued map per frame, so a map queued by QueueUpdate/UpdateIsNeeded would still be
        // calculated after deactivation and read its destroyed target. Remove it from the queue as well.
        public static void Unregister(EnvironmentController ec, DijkstraMap map)
        {
            map.Deactivate();
            var queue = DijkstraQueue.GetValue(ec) as Queue<DijkstraMap>;
            if (queue == null || !queue.Contains(map)) return;
            var keep = queue.Where(m => m != map).ToArray();
            queue.Clear(); foreach (var m in keep) queue.Enqueue(m);
        }

        // True if no active or queued map targets a destroyed transform.
        public static bool MapsClean(EnvironmentController ec)
        {
            var maps = ((List<DijkstraMap>)DijkstraMaps.GetValue(ec)).Concat((Queue<DijkstraMap>)DijkstraQueue.GetValue(ec));
            return maps.All(m => m == null || ((List<Transform>)DijkstraTargets.GetValue(m)).All(t => t != null));
        }

        public static T Get<T>(FieldInfo field, object target) { return (T)field.GetValue(target); }
        public static bool Active(NPC npc) { return npc != null && npc.Entity != null && (bool)EntityActive.GetValue(npc.Entity); }
        public static Vector3 Direction(float degrees) { return Quaternion.Euler(0, degrees, 0) * Vector3.forward; }
        public static float Degrees(Vector3 direction) { return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg; }
    }

    public sealed partial class TogetherRuntime
    {
        sealed class CraftersGaze { public bool Looking; public float At; }
        sealed class CraftersSpin { public int Owner, Token; public float Angle, Speed, Echo; public Vector3 Centre; public bool EchoesShown; }
        sealed class CraftersOwnerAttack { public ArtsAndCrafters Npc; public int Token, Layer; public Vector3 Angle; public float Speed, Echo, Deadline; }

        readonly Dictionary<long, CraftersGaze> craftersGaze = new Dictionary<long, CraftersGaze>();
        readonly Dictionary<int, int> craftersGazeSeq = new Dictionary<int, int>();
        readonly Dictionary<ArtsAndCrafters_Stalking, Dictionary<int, float>> craftersJealousy = new Dictionary<ArtsAndCrafters_Stalking, Dictionary<int, float>>();
        readonly HashSet<long> craftersInTrigger = new HashSet<long>();
        readonly List<TogetherCraftersFlee> craftersFlees = new List<TogetherCraftersFlee>();
        readonly Dictionary<int, CraftersSpin> craftersSpins = new Dictionary<int, CraftersSpin>();
        readonly HashSet<ArtsAndCrafters> craftersHiddenReplicas = new HashSet<ArtsAndCrafters>();
        readonly HashSet<int> craftersStartTokens = new HashSet<int>(), craftersFinishTokens = new HashSet<int>();
        readonly Dictionary<int, bool> craftersLastReport = new Dictionary<int, bool>();
        CraftersTrigger[] craftersTriggers = new CraftersTrigger[0];
        Bounds[] craftersTriggerBounds = new Bounds[0];
        int craftersTriggerCrafters;
        readonly HashSet<int> craftersRecordPeers = new HashSet<int>();
        CraftersOwnerAttack craftersOwner;
        Transform craftersSightTarget;
        int craftersNextToken, craftersSendSeq;
        float craftersLastRoll = -1;
        float craftersNextReport, craftersNextHeartbeat;
        int craftersRemoteSpawns, craftersGazeAccepted, craftersGazeRejected, craftersRemoteFlees, craftersRemoteAngers,
            craftersRemoteAttacks, craftersRemoteTeleports, craftersAborts, craftersOwnerAttacks, craftersOwnerTeleports, craftersSpinFrames;

        internal static bool CraftersPatchGate(TogetherRuntime r) { return r != null && r.Active && r.Hosting && r.worldReady; }

        void ResetCrafters()
        {
            EndCraftersOwnerAttack();
            foreach (var flee in craftersFlees.ToArray()) flee.Release();
            craftersFlees.Clear();
            foreach (var pair in craftersSpins) HideCraftersEchoes(pair.Key, pair.Value);
            foreach (var replica in craftersHiddenReplicas) if (replica != null && replica.Entity != null) replica.Entity.SetActive(true);
            craftersGaze.Clear(); craftersGazeSeq.Clear(); craftersJealousy.Clear(); craftersInTrigger.Clear(); craftersSpins.Clear();
            craftersHiddenReplicas.Clear(); craftersStartTokens.Clear(); craftersFinishTokens.Clear(); craftersLastReport.Clear();
            craftersTriggers = new CraftersTrigger[0]; craftersTriggerBounds = new Bounds[0]; craftersTriggerCrafters = 0; craftersRecordPeers.Clear(); craftersSendSeq = 0;
            craftersNextReport = craftersNextHeartbeat = 0;
        }

        // ---------------------------------------------------------------- shared helpers

        int CraftersIndex(ArtsAndCrafters crafters) { return Array.IndexOf(npcs, crafters); }

        ArtsAndCrafters CraftersAt(int index)
        {
            if (index < 0 || index >= npcs.Length) return null;
            var crafters = npcs[index] as ArtsAndCrafters;
            return crafters == null ? null : crafters;
        }

        bool CraftersPeerValid(Peer peer, float now)
        {
            return peer != null && peer.Id != self && peer.Accepted && peer.Ready && peer.HasPose && !peer.Eliminated && !peer.InLocker && !peer.Invisible && now - peer.PoseAt < 2;
        }

        // Host line of sight from the crafters' native Looker to a remote pose (BaldiContact pattern).
        bool CraftersSees(ArtsAndCrafters crafters, Vector3 position, bool useFov)
        {
            var core = Singleton<CoreGameManager>.Instance;
            if (crafters == null || crafters.looker == null || crafters.Blinded || core == null || core.GetPlayer(0) == null) return false;
            float distance = Vector3.Distance(crafters.looker.transform.position, position);
            if (distance > Mathf.Min(crafters.looker.distance, crafters.ec.MaxRaycast)) return false;
            if (useFov && CraftersNative.Get<bool>(CraftersNative.LookerHasFov, crafters.looker)
                && Vector3.Angle(crafters.transform.forward, position - crafters.transform.position) > CraftersNative.Get<float>(CraftersNative.LookerFov, crafters.looker)) return false;
            if (craftersSightTarget == null) { craftersSightTarget = new GameObject("Crafters remote sight target").transform; craftersSightTarget.SetParent(transform, false); }
            craftersSightTarget.gameObject.layer = CraftersNative.Get<int>(CraftersNative.EntityTargetLayer, core.GetPlayer(0).plm.Entity);
            craftersSightTarget.position = position;
            bool seen; crafters.looker.Raycast(craftersSightTarget, distance + .01f, out seen); return seen;
        }

        // Nearest valid remote player the crafters can see (ties by lowest id). Plain loop: runs every host frame.
        internal Peer RemoteCraftersSighted(ArtsAndCrafters crafters)
        {
            float now = Time.realtimeSinceStartup; Vector3 from = crafters.transform.position;
            Peer best = null; float bestDistance = 0;
            foreach (var peer in peers.Values)
            {
                if (!CraftersPeerValid(peer, now)) continue;
                float distance = (peer.Position - from).sqrMagnitude;
                if (best != null && (distance > bestDistance || (distance == bestDistance && peer.Id > best.Id))) continue;
                if (!CraftersSees(crafters, peer.Position, true)) continue;
                best = peer; bestDistance = distance;
            }
            return best;
        }

        bool CraftersGazeFresh(Peer peer, int index, float now)
        {
            CraftersGaze gaze;
            return craftersGaze.TryGetValue(((long)peer.Id << 16) | (uint)index, out gaze) && gaze.Looking && now - gaze.At < .35f;
        }

        // Lowest-id valid remote player with a fresh validated gaze report.
        internal Peer RemoteCraftersLooking(ArtsAndCrafters crafters)
        {
            int index = CraftersIndex(crafters); if (index < 0) return null;
            float now = Time.realtimeSinceStartup; Peer best = null;
            foreach (var peer in peers.Values)
                if ((best == null || peer.Id < best.Id) && CraftersPeerValid(peer, now) && CraftersGazeFresh(peer, index, now)) best = peer;
            return best;
        }

        readonly List<Peer> craftersPeerScratch = new List<Peer>();
        static readonly Comparison<Peer> CraftersById = (x, y) => x.Id.CompareTo(y.Id);

        Peer CraftersPeer(int id) { Peer peer; return peers.TryGetValue(id, out peer) ? peer : null; }

        bool CraftersTeleportPath(ArtsAndCrafters crafters, out Vector3 player, out Vector3 baldi)
        {
            // Mirrors ArtsAndCrafters.TeleportPlayer so the remote owner lands where a local one would.
            player = baldi = Vector3.zero;
            var ec = crafters.ec; var doors = new List<IntVector2>();
            foreach (Elevator elevator in ec.Elevators) if (elevator.GateIsOpen) doors.Add(elevator.Door.position);
            if (doors.Count <= 0) foreach (Elevator elevator in ec.Elevators) doors.Add(elevator.Door.position);
            if (doors.Count <= 0 || ec.mainHall == null || ec.mainHall.TileCount <= 0) return false;
            int playerDistance = CraftersNative.Get<int>(CraftersNative.PlayerSpawnDistance, crafters), baldiDistance = CraftersNative.Get<int>(CraftersNative.BaldiSpawnDistance, crafters);
            IntVector2 start = doors[UnityEngine.Random.Range(0, doors.Count)];
            for (int attempt = 0; attempt < 32; attempt++)
            {
                List<Cell> path; bool success;
                ec.FindPath(ec.CellFromPosition(start), ec.mainHall.TileAtIndex(UnityEngine.Random.Range(0, ec.mainHall.TileCount)), PathType.Nav, out path, out success);
                if (success && path.Count > baldiDistance && path.Count > playerDistance)
                { player = path[playerDistance].CenterWorldPosition; baldi = path[baldiDistance].FloorWorldPosition + Vector3.up * 5f; return true; }
            }
            return false;
        }

        void SetCraftersEchoes(ArtsAndCrafters crafters, bool active)
        {
            foreach (var echo in CraftersNative.Get<Transform[]>(CraftersNative.Echo, crafters))
                if (echo != null && echo.gameObject.activeSelf != active) echo.gameObject.SetActive(active);
        }

        // Same placement as ArtsAndCrafters.UpdateTeleportAnimation, around any centre.
        void PlaceCraftersSpin(ArtsAndCrafters crafters, Vector3 centre, Vector3 angle, float echoDistance)
        {
            float spin = CraftersNative.Get<float>(CraftersNative.SpinDistance, crafters);
            crafters.transform.position = centre + angle * spin;
            var echoes = CraftersNative.Get<Transform[]>(CraftersNative.Echo, crafters);
            for (int i = 0; i < echoes.Length; i++)
                if (echoes[i] != null) echoes[i].position = centre + Quaternion.AngleAxis(echoDistance * i * -1f, Vector3.up) * angle * (spin + i + 1f);
        }

        // ---------------------------------------------------------------- host: native state overrides

        internal bool CraftersReadyUpdate(ArtsAndCrafters_Ready state)
        {
            var crafters = state.Npc as ArtsAndCrafters; if (crafters == null) return false;
            bool sighted = crafters.looker.PlayerInSight() || RemoteCraftersSighted(crafters) != null;
            if (!sighted)
            {
                float time = (float)CraftersNative.ReadyTime.GetValue(state) - Time.deltaTime * crafters.TimeScale;
                CraftersNative.ReadyTime.SetValue(state, time);
                if (time <= 0f) { CraftersChange(crafters, new ArtsAndCrafters_Waiting(crafters)); return true; }
            }
            else CraftersNative.ReadyTime.SetValue(state, 1f);
            if (!crafters.looker.IsVisible && RemoteCraftersLooking(crafters) == null) CraftersChange(crafters, new ArtsAndCrafters_Stalking(crafters));
            return true;
        }

        internal bool CraftersStalkingUpdate(ArtsAndCrafters_Stalking state)
        {
            var crafters = state.Npc as ArtsAndCrafters; if (crafters == null) return false;
            float dt = Time.deltaTime * crafters.TimeScale;
            Peer sighted = RemoteCraftersSighted(crafters);
            if (!crafters.looker.PlayerInSight() && sighted == null)
            {
                float left = (float)CraftersNative.StalkRespawn.GetValue(state) - dt;
                CraftersNative.StalkRespawn.SetValue(state, left);
                if (left <= 0f) { CraftersChange(crafters, new ArtsAndCrafters_Waiting(crafters)); return true; }
            }
            else CraftersNative.StalkRespawn.SetValue(state, 10f);
            // Native PlayerInSight: shared sight time; the player who completes it is fled from.
            if (sighted != null)
            {
                float seen = (float)CraftersNative.StalkSight.GetValue(state) + dt;
                CraftersNative.StalkSight.SetValue(state, seen);
                if (seen >= crafters.RunTime) { StartRemoteCraftersFlee(crafters, sighted); return true; }
            }
            // Native InPlayerSight: a jealous, untagged player looking at the crafters for AngryTime.
            Dictionary<int, float> jealousy;
            if (!craftersJealousy.TryGetValue(state, out jealousy))
            {
                foreach (var old in craftersJealousy.Keys.Where(k => k.Npc == crafters).ToArray()) craftersJealousy.Remove(old);
                craftersJealousy[state] = jealousy = new Dictionary<int, float>();
            }
            if (!crafters.Jealous) return true;
            float now = Time.realtimeSinceStartup; int index = CraftersIndex(crafters);
            craftersPeerScratch.Clear();
            foreach (var peer in peers.Values) craftersPeerScratch.Add(peer);
            craftersPeerScratch.Sort(CraftersById);
            for (int i = 0; i < craftersPeerScratch.Count; i++)
            {
                var peer = craftersPeerScratch[i];
                if (!CraftersPeerValid(peer, now) || peer.Tagged || !CraftersGazeFresh(peer, index, now)) continue;
                float time; jealousy.TryGetValue(peer.Id, out time); time += dt; jealousy[peer.Id] = time;
                if (time >= crafters.AngryTime) { craftersPeerScratch.Clear(); StartRemoteCraftersChase(crafters, peer); return true; }
            }
            craftersPeerScratch.Clear();
            return true;
        }

        internal bool CraftersFleeingUpdate(ArtsAndCrafters_Fleeing state)
        {
            var crafters = state.Npc as ArtsAndCrafters; if (crafters == null) return false;
            if (!crafters.looker.IsVisible && RemoteCraftersLooking(crafters) == null) CraftersChange(crafters, new ArtsAndCrafters_Waiting(crafters));
            return true;
        }

        static void CraftersChange(ArtsAndCrafters crafters, ArtsAndCrafters_StateBase state)
        { crafters.state = state; crafters.behaviorStateMachine.ChangeState(state); }

        void StartRemoteCraftersFlee(ArtsAndCrafters crafters, Peer peer)
        { CraftersChange(crafters, new TogetherCraftersFlee(crafters, peer.Id, peer.Position)); craftersRemoteFlees++; }

        void StartRemoteCraftersChase(ArtsAndCrafters crafters, Peer peer)
        {
            // GetAngry applies the native anger presentation (sprite, speed, audio, Hide(false)); its
            // Chasing(host) state is replaced at once by the remote chase.
            crafters.GetAngry(Singleton<CoreGameManager>.Instance.GetPlayer(0));
            CraftersChange(crafters, new TogetherCraftersChase(crafters, peer.Id, peer.Position));
            craftersRemoteAngers++;
        }

        internal void UpdateCraftersFlee(TogetherCraftersFlee flee)
        {
            Peer peer = CraftersPeer(flee.Peer);
            if (peer != null) flee.Proxy.position = peer.Position;
            if (peer == null || peer.Eliminated || (!flee.Crafters.looker.IsVisible && RemoteCraftersLooking(flee.Crafters) == null))
                CraftersChange(flee.Crafters, new ArtsAndCrafters_Waiting(flee.Crafters));
        }

        internal void RegisterCraftersFlee(TogetherCraftersFlee flee, bool add) { if (add) craftersFlees.Add(flee); else craftersFlees.Remove(flee); }

        internal void UpdateCraftersChase(TogetherCraftersChase chase)
        {
            var crafters = chase.Crafters; Peer peer = CraftersPeer(chase.Peer); float now = Time.realtimeSinceStartup;
            // Eliminated or departed targets end the encounter through the native terminal path.
            if (peer == null || !peer.Accepted || !peer.Ready || peer.Eliminated) { AbortCrafters(crafters, null, 0); return; }
            if (!CraftersPeerValid(peer, now)) return;
            if (CraftersSees(crafters, peer.Position, false))
            {
                if (crafters.behaviorStateMachine.CurrentNavigationState != chase.Target) crafters.behaviorStateMachine.ChangeNavigationState(chase.Target);
                chase.Target.UpdatePosition(peer.Position);
            }
            Vector3 delta = peer.Position - crafters.transform.position;
            if (Mathf.Abs(delta.y) < 6 && new Vector2(delta.x, delta.z).magnitude < 5 && ClearWorldLine(crafters.transform.position, peer.Position))
                CraftersChange(crafters, new TogetherCraftersAttack(crafters, peer.Id));
        }

        internal void BeginRemoteCraftersAttack(TogetherCraftersAttack attack)
        {
            var crafters = attack.Crafters; Peer peer = CraftersPeer(attack.Peer);
            attack.Token = ++craftersNextToken;
            Vector3 forward = CraftersNative.Direction(peer == null ? 0 : peer.Yaw), centre = peer == null ? crafters.transform.position : peer.Position;
            // ArtsAndCrafters.Attack without a host PlayerManager. The overlay layer and 2D attack loop
            // belong to the owner's screen only; the host sees the replica like any other spectator.
            crafters.Navigator.SetSpeed(0f); crafters.Navigator.ClearDestination();
            crafters.Navigator.Entity.SetInteractionState(false);
            CraftersNative.Attacking.SetValue(crafters, true);
            crafters.transform.position = centre + forward * 8f;
            SetCraftersEchoes(crafters, true);
            CraftersNative.Get<AudioManager>(CraftersNative.AudMan, crafters).FlushQueue(true);
            attack.Angle = -forward; attack.Speed = crafters.AttackSpinSpeed; attack.Echo = 0; attack.Time = crafters.AttackTime;
            craftersRemoteAttacks++;
            int index = CraftersIndex(crafters), token = attack.Token;
            if (peer != null && index >= 0) Network.Send(peer.Id, CraftersStartPacket(index, token));
        }

        internal void UpdateRemoteCraftersAttack(TogetherCraftersAttack attack)
        {
            if (attack.Done) return;
            var crafters = attack.Crafters; Peer peer = CraftersPeer(attack.Peer);
            if (peer == null || !peer.Accepted || !peer.Ready || peer.Eliminated) { attack.Done = true; AbortCrafters(crafters, peer, attack.Token); return; }
            float dt = Time.deltaTime * crafters.TimeScale;
            attack.Angle = Quaternion.AngleAxis(attack.Speed * dt, Vector3.up) * attack.Angle;
            PlaceCraftersSpin(crafters, peer.Position, attack.Angle, attack.Echo);
            attack.Speed += crafters.AttackSpinAccel * dt;
            attack.Echo = Mathf.Min(attack.Echo + crafters.EchoIncrease * dt, crafters.MaxEchoDistance);
            attack.Time -= dt;
            if (attack.Time > 0f) return;
            attack.Done = true;
            Vector3 destination, baldiPosition; bool teleport = CraftersTeleportPath(crafters, out destination, out baldiPosition);
            if (teleport)
            {
                var baldi = crafters.ec.GetBaldi();
                if (baldi != null) { baldi.ClearSoundLocations(true, destination); baldi.transform.position = baldiPosition; }
                crafters.ec.MakeNoise(destination, CraftersNative.Get<int>(CraftersNative.NoiseValue, crafters));
                peer.Position = destination; peer.PoseAt = Time.realtimeSinceStartup;
                craftersRemoteTeleports++;
            }
            int index = CraftersIndex(crafters);
            if (index >= 0) Network.Send(peer.Id, CraftersFinishPacket(index, attack.Token, teleport, destination));
            crafters.DisappearForever();
        }

        void AbortCrafters(ArtsAndCrafters crafters, Peer owner, int token)
        {
            int index = CraftersIndex(crafters);
            if (owner != null && token > 0 && index >= 0) Network.Send(owner.Id, CraftersFinishPacket(index, token, false, Vector3.zero));
            crafters.DisappearForever(); craftersAborts++;
        }

        // ---------------------------------------------------------------- host: per-frame remote triggers

        // Triggers are created in ArtsAndCrafters.Initialize and never move, so their boxes are cached once and
        // rescanned only when the number of crafters changes. Inside-keys survive a rescan by trigger identity.
        void RefreshCraftersTriggers()
        {
            var old = craftersTriggers;
            craftersTriggers = Object.FindObjectsOfType<CraftersTrigger>().Where(t => t.crafters != null).ToArray();
            craftersTriggerBounds = craftersTriggers.Select(t => CraftersTriggerBounds(t)).ToArray();
            craftersTriggerCrafters = CraftersCount();
            if (craftersInTrigger.Count == 0) return;
            var remapped = new HashSet<long>();
            foreach (long key in craftersInTrigger)
            {
                int oldIndex = (int)(uint)key; if (oldIndex >= old.Length || old[oldIndex] == null) continue;
                int newIndex = Array.IndexOf(craftersTriggers, old[oldIndex]);
                if (newIndex >= 0) remapped.Add((key & ~0xFFFFFFFFL) | (uint)newIndex);
            }
            craftersInTrigger.Clear(); craftersInTrigger.UnionWith(remapped);
        }

        int CraftersCount()
        {
            int count = 0;
            for (int i = 0; i < npcs.Length; i++) if (npcs[i] is ArtsAndCrafters) count++;
            return count;
        }

        // Every peer id that owns a gaze, sequence or trigger record is tracked here. Each frame only this small
        // set is checked against peers (no allocation); records are pruned the first frame their peer is gone,
        // even if it joined and left between two frames (a peer-set signature would miss that).
        bool CraftersRecordsStale()
        {
            foreach (int id in craftersRecordPeers) if (!peers.ContainsKey(id)) return true;
            return false;
        }

        void PruneCraftersPeers()
        {
            if (craftersRecordPeers.Count == 0 || !CraftersRecordsStale()) return;
            foreach (long key in craftersGaze.Keys.Where(k => !peers.ContainsKey((int)(k >> 16))).ToArray()) craftersGaze.Remove(key);
            foreach (int id in craftersGazeSeq.Keys.Where(k => !peers.ContainsKey(k)).ToArray()) craftersGazeSeq.Remove(id);
            craftersInTrigger.RemoveWhere(k => !peers.ContainsKey((int)(k >> 32)));
            craftersRecordPeers.RemoveWhere(id => !peers.ContainsKey(id));
        }

        // The prefab's trigger collider may live on the root or a child; without an enabled one, use the tile.
        static Bounds CraftersTriggerBounds(CraftersTrigger trigger)
        {
            foreach (var collider in trigger.GetComponentsInChildren<Collider>())
                if (collider.enabled) return collider.bounds;
            return new Bounds(trigger.transform.position, new Vector3(10, 10, 10));
        }

        void UpdateCraftersHost(float now)
        {
            PruneCraftersPeers();
            int count = CraftersCount();
            if (count == 0) return;
            if (count != craftersTriggerCrafters) RefreshCraftersTriggers();
            // CraftersTrigger.OnTriggerEnter only sees the host's collider; remote poses enter the same box.
            for (int t = 0; t < craftersTriggers.Length; t++)
            {
                var trigger = craftersTriggers[t];
                if (trigger == null || trigger.crafters == null || !trigger.crafters.enabled || !CraftersNative.Active(trigger.crafters)) continue;
                if (!trigger.gameObject.activeInHierarchy) continue;
                Bounds bounds = craftersTriggerBounds[t];
                foreach (var peer in peers.Values)
                {
                    if (peer.Id == self) continue;
                    long key = ((long)peer.Id << 32) | (uint)t;
                    // Triggers sit at floor height and poses at eye height (+5): compare the floor footprint only.
                    bool inside = CraftersPeerValid(peer, now) && peer.Position.x >= bounds.min.x - 1 && peer.Position.x <= bounds.max.x + 1
                        && peer.Position.z >= bounds.min.z - 1 && peer.Position.z <= bounds.max.z + 1;
                    if (!inside) { craftersInTrigger.Remove(key); continue; }
                    if (!craftersInTrigger.Add(key)) continue;
                    craftersRecordPeers.Add(peer.Id);
                    craftersLastRoll = UnityEngine.Random.value;
                    if (craftersLastRoll <= trigger.spawnChance)
                    { bool waiting = trigger.crafters.state is ArtsAndCrafters_Waiting; trigger.crafters.SpawnAt(trigger.target); if (waiting) craftersRemoteSpawns++; }
                }
            }
        }

        // ---------------------------------------------------------------- client: gaze, owner attack, spectator spin

        bool LocalLooksAtCrafters(ArtsAndCrafters crafters, PlayerManager player)
        {
            // Looker.Update "visible": unobstructed ray from the crafters to the player and the crafters
            // inside the player's horizontal camera field of view.
            var core = Singleton<CoreGameManager>.Instance;
            if (crafters.looker == null || crafters.Blinded || !CraftersNative.Active(crafters) || crafters.ec == null) return false;
            float distance = Mathf.Min((crafters.looker.transform.position - player.transform.position).magnitude, Mathf.Min(crafters.looker.distance, crafters.ec.MaxRaycast));
            bool sighted; crafters.looker.Raycast(player.transform, distance, player, (LayerMask)CraftersNative.LookerMask.GetValue(crafters.looker), out sighted);
            if (!sighted || core.GetCamera(0) == null) return false;
            float angle = Vector3.Angle(crafters.looker.transform.position - player.transform.position, core.GetCamera(0).transform.forward);
            var files = Singleton<PlayerFileManager>.Instance;
            float aspect = files == null || files.resolutionY <= 0 ? 16f / 9f : (float)files.resolutionX / files.resolutionY;
            var global = Singleton<GlobalCam>.Instance;
            if (global == null || global.Cam == null) return false;
            float fov = Camera.VerticalToHorizontalFieldOfView(global.Cam.fieldOfView, aspect);
            return angle <= fov / 2f + CraftersNative.Get<float>(CraftersNative.LookerBuffer, crafters.looker);
        }

        void UpdateCraftersGaze(float now, PlayerManager player)
        {
            if (!readyAcknowledged || now < craftersNextReport) return;
            craftersNextReport = now + .1f;
            bool heartbeat = now >= craftersNextHeartbeat; if (heartbeat) craftersNextHeartbeat = now + 1;
            for (int i = 0; i < npcs.Length; i++)
            {
                var crafters = CraftersAt(i); if (crafters == null) continue;
                bool looking = !LocalEliminated && !player.Tagged && LocalLooksAtCrafters(crafters, player);
                bool last; craftersLastReport.TryGetValue(i, out last);
                if (!looking && !last && !heartbeat) continue;
                craftersLastReport[i] = looking;
                Network.Send(0, CraftersGazePacket(epoch, ++craftersSendSeq, i, looking));
            }
        }

        void BeginCraftersOwnerAttack(ArtsAndCrafters crafters, int token)
        {
            EndCraftersOwnerAttack();
            var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            var renderer = CraftersNative.Get<SpriteRenderer>(CraftersNative.VisibleRenderer, crafters);
            craftersOwner = new CraftersOwnerAttack { Npc = crafters, Token = token, Layer = renderer == null ? 0 : renderer.gameObject.layer,
                Angle = player.transform.forward * -1f, Speed = crafters.AttackSpinSpeed, Echo = 0, Deadline = Time.realtimeSinceStartup + crafters.AttackTime + 5 };
            Applying = true;
            try { crafters.Attack(player); }
            finally { Applying = false; }
            craftersOwnerAttacks++;
        }

        void EndCraftersOwnerAttack()
        {
            var owner = craftersOwner; craftersOwner = null;
            if (owner == null || owner.Npc == null) return;
            var audio = CraftersNative.Get<AudioManager>(CraftersNative.AttackAudio, owner.Npc);
            if (audio != null) { audio.maintainLoop = false; audio.SetLoop(false); audio.FlushQueue(true); }
            var renderer = CraftersNative.Get<SpriteRenderer>(CraftersNative.VisibleRenderer, owner.Npc);
            if (renderer != null) renderer.gameObject.layer = owner.Layer;
            CraftersNative.Attacking.SetValue(owner.Npc, false);
            SetCraftersEchoes(owner.Npc, false);
        }

        void UpdateCraftersOwner(float now)
        {
            var owner = craftersOwner; if (owner == null) return;
            var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            if (owner.Npc == null || player == null || now > owner.Deadline || LocalEliminated) { EndCraftersOwnerAttack(); return; }
            // ArtsAndCrafters_Teleporting.Update on the owner's own player; the host ends it.
            float dt = Time.deltaTime * owner.Npc.TimeScale;
            owner.Angle = Quaternion.AngleAxis(owner.Speed * dt, Vector3.up) * owner.Angle;
            owner.Npc.UpdateTeleportAnimation(player, owner.Angle, owner.Echo);
            owner.Speed += owner.Npc.AttackSpinAccel * dt;
            owner.Echo = Mathf.Min(owner.Echo + owner.Npc.EchoIncrease * dt, owner.Npc.MaxEchoDistance);
        }

        void HideCraftersEchoes(int index, CraftersSpin spin)
        { var crafters = CraftersAt(index); if (crafters != null && spin.EchoesShown) SetCraftersEchoes(crafters, false); }

        void UpdateCraftersSpins()
        {
            foreach (var pair in craftersSpins.ToArray())
            {
                var crafters = CraftersAt(pair.Key); var spin = pair.Value;
                if (crafters == null || !CraftersNative.Active(crafters)) { craftersSpins.Remove(pair.Key); continue; }
                float dt = Time.deltaTime * crafters.TimeScale;
                spin.Angle += spin.Speed * dt; spin.Speed += crafters.AttackSpinAccel * dt;
                spin.Echo = Mathf.Min(spin.Echo + crafters.EchoIncrease * dt, crafters.MaxEchoDistance);
                Peer owner = CraftersPeer(spin.Owner);
                Vector3 centre = owner != null && owner.HasPose && owner.Id != self ? owner.Position : spin.Centre;
                if (!spin.EchoesShown) { SetCraftersEchoes(crafters, true); spin.EchoesShown = true; }
                PlaceCraftersSpin(crafters, centre, CraftersNative.Direction(spin.Angle), spin.Echo);
                craftersSpinFrames++;
            }
        }

        void UpdateCrafters()
        {
            if (!Active || !worldReady || !Running) return;
            float now = Time.realtimeSinceStartup;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player == null) return;
            UpdateCraftersOwner(now);
            UpdateCraftersSpins();
            // A flee that is no longer its crafters' state (or whose crafters vanished) must not keep its map.
            foreach (var flee in craftersFlees.ToArray())
                if (flee.Crafters == null || !ReferenceEquals(flee.Crafters.state, flee) || !CraftersNative.Active(flee.Crafters)) flee.Release();
            if (!Hosting) { UpdateCraftersGaze(now, player); return; }
            var manager = Singleton<BaseGameManager>.Instance;
            if (manager != null && manager.Ec != null && manager.Ec.Active) UpdateCraftersHost(now);
        }

        // ---------------------------------------------------------------- packets

        byte[] CraftersStartPacket(int index, int token)
        { return Wire.Packet(Op.CraftersToClient, w => { w.Write(epoch); w.Write((byte)1); w.Write(index); w.Write(token); }); }

        byte[] CraftersFinishPacket(int index, int token, bool teleport, Vector3 destination)
        { return Wire.Packet(Op.CraftersToClient, w => { w.Write(epoch); w.Write((byte)2); w.Write(index); w.Write(token); w.Write(teleport); WriteVector(w, destination); }); }

        static byte[] CraftersGazePacket(int generation, int seq, int index, bool looking)
        { return Wire.Packet(Op.CraftersToHost, w => { w.Write(generation); w.Write((byte)1); w.Write(seq); w.Write(index); w.Write(looking); }); }

        bool HandleCraftersClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.CraftersToHost) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub != 1) throw new IOException("Invalid crafters report.");
            int seq = r.ReadInt32(), index = r.ReadInt32(); bool looking = r.ReadBoolean();
            if (index < 0 || index > 127 || seq < 1) throw new IOException("Invalid crafters gaze.");
            if (generation != epoch || !worldReady || sender == null || !sender.Accepted || !sender.Ready) return true;
            int last; if (craftersGazeSeq.TryGetValue(id, out last) && seq <= last) return true;
            craftersGazeSeq[id] = seq; craftersRecordPeers.Add(id);
            var crafters = CraftersAt(index); if (crafters == null) return true;
            // The claim stands only if the host also has an unobstructed line between the crafters and that player.
            float now = Time.realtimeSinceStartup;
            bool valid = looking && CraftersPeerValid(sender, now) && CraftersNative.Active(crafters) && CraftersSees(crafters, sender.Position, false);
            if (looking) { if (valid) craftersGazeAccepted++; else craftersGazeRejected++; }
            craftersGaze[((long)id << 16) | (uint)index] = new CraftersGaze { Looking = valid, At = now };
            return true;
        }

        bool HandleCraftersHost(Op op, BinaryReader r)
        {
            if (op != Op.CraftersToClient) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub != 1 && sub != 2) throw new IOException("Invalid crafters command.");
            int index = r.ReadInt32(), token = r.ReadInt32();
            bool teleport = false; Vector3 destination = Vector3.zero;
            if (sub == 2) { teleport = r.ReadBoolean(); destination = ReadVector(r); }
            if (index < 0 || index > 127 || token < 1) throw new IOException("Invalid crafters command.");
            if (generation != epoch || !worldReady) return true;
            var crafters = CraftersAt(index); if (crafters == null) return true;
            if (sub == 1)
            {
                if (craftersFinishTokens.Contains(token) || !craftersStartTokens.Add(token) || LocalEliminated) return true;
                BeginCraftersOwnerAttack(crafters, token);
                return true;
            }
            if (!craftersFinishTokens.Add(token)) return true;
            craftersStartTokens.Add(token);
            if (teleport && !LocalEliminated)
            {
                Applying = true;
                try { Singleton<CoreGameManager>.Instance.GetPlayer(0).Teleport(destination); craftersOwnerTeleports++; }
                finally { Applying = false; }
            }
            if (craftersOwner != null && craftersOwner.Token == token) EndCraftersOwnerAttack();
            return true;
        }

        // ---------------------------------------------------------------- world section

        void WriteCraftersWorld(BinaryWriter w)
        {
            var indices = new List<int>();
            for (int i = 0; i < npcs.Length && indices.Count < 16; i++) if (CraftersAt(i) != null) indices.Add(i);
            w.Write((byte)indices.Count);
            foreach (int index in indices)
            {
                var crafters = CraftersAt(index);
                bool gone = !CraftersNative.Active(crafters);
                int owner = -1, token = 0; float angle = 0, speed = 0, echo = 0; Vector3 centre = Vector3.zero;
                var remote = crafters.behaviorStateMachine.CurrentState as TogetherCraftersAttack;
                var native = crafters.behaviorStateMachine.CurrentState as ArtsAndCrafters_Teleporting;
                if (!gone && remote != null && !remote.Done)
                {
                    Peer peer = CraftersPeer(remote.Peer);
                    owner = remote.Peer; token = remote.Token; angle = CraftersNative.Degrees(remote.Angle); speed = remote.Speed; echo = remote.Echo;
                    centre = peer != null ? peer.Position : crafters.transform.position;
                }
                else if (!gone && native != null)
                {
                    var player = CraftersNative.Get<PlayerManager>(CraftersNative.TeleportPlayer, native);
                    owner = self; angle = CraftersNative.Degrees(CraftersNative.Get<Vector3>(CraftersNative.TeleportAngle, native));
                    speed = CraftersNative.Get<float>(CraftersNative.TeleportSpeed, native); echo = CraftersNative.Get<float>(CraftersNative.TeleportEcho, native);
                    centre = player != null ? player.transform.position : crafters.transform.position;
                }
                w.Write(index); w.Write((byte)((gone ? 1 : 0) | (owner >= 0 ? 2 : 0)));
                if (owner < 0) continue;
                w.Write(owner); w.Write(token); w.Write(angle); w.Write(speed); w.Write(echo); WriteVector(w, centre);
            }
        }

        void ReadCraftersWorld(BinaryReader r, bool apply)
        {
            int count = r.ReadByte(); if (count > 16) throw new IOException("Crafters state limit exceeded.");
            var attacking = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                int index = r.ReadInt32(); byte flags = r.ReadByte();
                if (index < 0 || index > 127 || flags > 3) throw new IOException("Invalid crafters state.");
                bool gone = (flags & 1) != 0, attack = (flags & 2) != 0;
                int owner = -1, token = 0; float angle = 0, speed = 0, echo = 0; Vector3 centre = Vector3.zero;
                if (attack)
                {
                    owner = r.ReadInt32(); token = r.ReadInt32(); angle = Wire.Number(r); speed = Wire.Number(r); echo = Wire.Number(r); centre = ReadVector(r);
                    if (owner < 0 || owner > 255 || token < 0) throw new IOException("Invalid crafters attack.");
                }
                if (!apply) continue;
                var crafters = CraftersAt(index); if (crafters == null) continue;
                // DisappearForever deactivates the host entity; the replica follows once.
                if (gone && CraftersNative.Active(crafters)) { crafters.Entity.SetActive(false); craftersHiddenReplicas.Add(crafters); }
                else if (!gone && craftersHiddenReplicas.Remove(crafters)) crafters.Entity.SetActive(true);
                if (!attack || gone) continue;
                if (owner == self)
                {
                    // Recovery for a start command that arrived before the world was ready.
                    if (token > 0 && !Hosting && !craftersStartTokens.Contains(token) && !craftersFinishTokens.Contains(token) && !LocalEliminated)
                    { craftersStartTokens.Add(token); BeginCraftersOwnerAttack(crafters, token); }
                    continue;
                }
                attacking.Add(index);
                CraftersSpin spin;
                if (!craftersSpins.TryGetValue(index, out spin) || spin.Owner != owner || spin.Token != token) craftersSpins[index] = spin = new CraftersSpin { Owner = owner, Token = token };
                spin.Angle = angle; spin.Speed = speed; spin.Echo = echo; spin.Centre = centre;
            }
            if (!apply) return;
            foreach (var pair in craftersSpins.Where(p => !attacking.Contains(p.Key)).ToArray()) { HideCraftersEchoes(pair.Key, pair.Value); craftersSpins.Remove(pair.Key); }
        }

        string CraftersDiagnostic()
        {
            return ",\"craftersRemoteSpawns\":" + craftersRemoteSpawns + ",\"craftersGazeAccepted\":" + craftersGazeAccepted + ",\"craftersGazeRejected\":" + craftersGazeRejected
                + ",\"craftersRemoteFlees\":" + craftersRemoteFlees + ",\"craftersRemoteAngers\":" + craftersRemoteAngers + ",\"craftersRemoteAttacks\":" + craftersRemoteAttacks
                + ",\"craftersRemoteTeleports\":" + craftersRemoteTeleports + ",\"craftersAborts\":" + craftersAborts + ",\"craftersOwnerAttacks\":" + craftersOwnerAttacks
                + ",\"craftersOwnerTeleports\":" + craftersOwnerTeleports + ",\"craftersSpinFrames\":" + craftersSpinFrames + ",\"craftersOwnerActive\":" + (craftersOwner != null ? "true" : "false");
        }

        // ---------------------------------------------------------------- lab tour

        bool craftersLabStarted;
        void UpdateCraftersLab()
        {
            if (craftersLabStarted || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-crafters-tour")) return;
            craftersLabStarted = true; StartCoroutine(TourCrafters());
        }

        bool CraftersLabGaze(Peer peer, int generation, int seq, int index, bool looking)
        {
            using (var reader = new BinaryReader(new MemoryStream(CraftersGazePacket(generation, seq, index, looking))))
            { reader.ReadByte(); bool handled = HandleCraftersClient(peer.Id, peer, Op.CraftersToHost, reader); return handled && reader.BaseStream.Position == reader.BaseStream.Length; }
        }

        void CraftersLabCommand(byte[] packet)
        {
            using (var reader = new BinaryReader(new MemoryStream(packet)))
            { reader.ReadByte(); HandleCraftersHost(Op.CraftersToClient, reader); }
        }

        IEnumerator TourCrafters()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start(); portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); if (!Active) { CheckNativeUi("crafters-host-opened", false); WriteLabResults("crafters-results.json", "Crafters"); yield break; }
            AutoStartRun();
            while (!worldReady) yield return null;
            yield return new WaitForSecondsRealtime(2);
            var core = Singleton<CoreGameManager>.Instance; var manager = Singleton<BaseGameManager>.Instance; var ec = manager.Ec; var player = core.GetPlayer(0);
            var prefab = Resources.FindObjectsOfTypeAll<ArtsAndCrafters>().First(n => !n.gameObject.scene.IsValid());
            var baldi = ec.GetBaldi();
            if (baldi == null) { var baldiPrefab = Resources.FindObjectsOfTypeAll<Baldi>().First(n => !n.gameObject.scene.IsValid()); baldi = (Baldi)ec.SpawnNPC(baldiPrefab, ec.CellFromPosition(ec.spawnPoint).position); }
            bool baldiNav = baldi.Navigator.enabled; baldi.Navigator.enabled = false;
            var cell = ec.CellFromPosition(player.transform.position);
            Vector3 origin = cell.FloorWorldPosition + Vector3.up * 5f;
            var a = ec.Npcs.OfType<ArtsAndCrafters>().FirstOrDefault() ?? (ArtsAndCrafters)ec.SpawnNPC(prefab, cell.position);
            var b = (ArtsAndCrafters)ec.SpawnNPC(prefab, cell.position);
            var c = (ArtsAndCrafters)ec.SpawnNPC(prefab, cell.position);
            npcs = ec.Npcs.ToArray();
            var chances = new Dictionary<CraftersTrigger, float>();
            foreach (var t in Object.FindObjectsOfType<CraftersTrigger>()) { chances[t] = t.spawnChance; t.spawnChance = 0; }
            foreach (var crafters in new[] { a, b, c }) { crafters.looker.enabled = false; crafters.Navigator.enabled = false; crafters.Entity.Teleport(origin); }
            b.enabled = false; // b stands in for a client replica: no host AI runs on it.
            int ia = CraftersIndex(a), ib = CraftersIndex(b);
            var remote = new Peer { Id = 3, Name = "Crafters owner", Accepted = true, Ready = true, HasPose = true, Position = origin + Vector3.right * 200, PoseAt = Time.realtimeSinceStartup };
            peers[3] = remote;
            Action fresh = () => { remote.PoseAt = Time.realtimeSinceStartup; Peer p2; if (peers.TryGetValue(2, out p2)) p2.PoseAt = Time.realtimeSinceStartup; };

            // 1. Without remote sight or gaze the overridden Ready behaves natively: nobody looks, so it stalks.
            CraftersChange(a, new ArtsAndCrafters_Ready(a));
            for (int f = 0; f < 5; f++) { fresh(); yield return null; }
            CheckNativeUi("single-player-gate-and-host-only-ready", !CraftersPatchGate(null) && CraftersPatchGate(this) && a.state is ArtsAndCrafters_Stalking);

            // 2. A remote pose entering a CraftersTrigger box spawns it like the native OnTriggerEnter.
            // Self-contained: a trigger on a reachable tile far from the host player (whose own collider would
            // fire the native OnTriggerEnter), physics synced before reading bounds, crafters back in Waiting.
            var far = ec.AllTilesNoGarbage(false, false).Where(t => t != null && !t.Null && Vector3.Distance(t.FloorWorldPosition, player.transform.position) > 40)
                .OrderBy(t => Vector3.Distance(t.FloorWorldPosition, player.transform.position)).FirstOrDefault();
            if (far == null) far = cell;
            Vector3 farTarget = far.FloorWorldPosition + Vector3.up * 5f;
            // CreateTrigger copies the prefab's spawnChance (0.2): identify the new instance exactly and force the roll.
            var before = new HashSet<CraftersTrigger>(Object.FindObjectsOfType<CraftersTrigger>());
            CraftersNative.CreateTrigger.Invoke(a, new object[] { far.position, far.FloorWorldPosition });
            var labTrigger = Object.FindObjectsOfType<CraftersTrigger>().First(t => !before.Contains(t));
            labTrigger.spawnChance = 1;
            labTrigger.transform.position = far.FloorWorldPosition;
            Physics.SyncTransforms();
            RefreshCraftersTriggers();
            Bounds labBounds = CraftersTriggerBounds(labTrigger);
            int spawns = craftersRemoteSpawns; craftersLastRoll = -1;
            remote.Position = origin + Vector3.right * 2; // outside: the trigger tile is over 40 units away
            for (int f = 0; f < 3; f++) { fresh(); yield return null; }
            CraftersChange(a, new ArtsAndCrafters_Waiting(a));
            bool waitingFirst = a.state is ArtsAndCrafters_Waiting;
            remote.Position = new Vector3(labBounds.center.x, farTarget.y, labBounds.center.z);
            for (int f = 0; f < 30 && craftersRemoteSpawns == spawns; f++) { fresh(); yield return null; }
            Log("LAB crafters trigger: bounds=" + labBounds + " triggerAt=" + labTrigger.transform.position + " active=" + labTrigger.gameObject.activeInHierarchy
                + " colliders=" + labTrigger.GetComponentsInChildren<Collider>(true).Length + " cached=" + craftersTriggers.Contains(labTrigger) + " pose=" + remote.Position
                + " roll=" + craftersLastRoll + " chance=" + labTrigger.spawnChance + " waitingFirst=" + waitingFirst + " state=" + a.state.GetType().Name
                + " crafters=" + a.transform.position + " target=" + farTarget);
            CheckNativeUi("remote-trigger-spawns-crafters", waitingFirst && craftersRemoteSpawns == spawns + 1 && craftersLastRoll >= 0
                && (a.state is ArtsAndCrafters_Ready || a.state is ArtsAndCrafters_Stalking) && Vector3.Distance(a.transform.position, farTarget) < 1);
            labTrigger.spawnChance = 0;
            remote.Position = origin + Vector3.right * 2;
            a.Entity.Teleport(origin);

            // 3. A gaze report holds Ready (nobody else is looking), accepted after host line-of-sight validation.
            CraftersChange(a, new ArtsAndCrafters_Ready(a));
            int seq = 1; bool held = true;
            for (int f = 0; f < 8; f++) { fresh(); CraftersLabGaze(remote, epoch, seq++, ia, true); yield return null; held &= a.state is ArtsAndCrafters_Ready; }
            CheckNativeUi("gaze-report-accepted-holds-ready", held && RemoteCraftersLooking(a) == remote);

            // 4. Replayed and stale-epoch reports change nothing.
            CraftersLabGaze(remote, epoch, seq - 1, ia, false);
            CraftersLabGaze(remote, epoch + 1, seq + 50, ia, false);
            CheckNativeUi("gaze-replay-and-stale-ignored", RemoteCraftersLooking(a) == remote);

            // 5. Locker and eliminated players' claims are rejected.
            remote.InLocker = true; CraftersLabGaze(remote, epoch, seq++, ia, true); bool locker = RemoteCraftersLooking(a) == null;
            remote.InLocker = false; remote.Eliminated = true; CraftersLabGaze(remote, epoch, seq++, ia, true); bool eliminated = RemoteCraftersLooking(a) == null;
            remote.Eliminated = false;
            CheckNativeUi("gaze-locker-and-eliminated-rejected", locker && eliminated);

            // 6. Jealous remote gaze for AngryTime makes it angry at that player.
            int found = CraftersNative.Get<int>(CraftersNative.FoundNotebooks, manager); CraftersNative.FoundNotebooks.SetValue(manager, Math.Max(found, manager.NotebookTotal));
            float runTime = a.RunTime, attackTime = a.AttackTime;
            CraftersNative.RunTime.SetValue(a, 100f); CraftersNative.AttackTime.SetValue(a, .75f);
            remote.Position = a.transform.position + Vector3.right * 2;
            CraftersChange(a, new ArtsAndCrafters_Stalking(a));
            float until = Time.realtimeSinceStartup + 4;
            while (Time.realtimeSinceStartup < until && !(a.state is TogetherCraftersChase)) { fresh(); CraftersLabGaze(remote, epoch, seq++, ia, true); yield return null; }
            var chase = a.state as TogetherCraftersChase;
            CheckNativeUi("remote-jealous-gaze-angers-crafters", chase != null && chase.Peer == 3);

            // 7. Touching the chased remote player starts the remote attack and its owner token.
            int attacks = craftersRemoteAttacks;
            remote.Position = a.transform.position + Vector3.right * 2;
            for (int f = 0; f < 4 && !(a.state is TogetherCraftersAttack); f++) { fresh(); yield return null; }
            var attack = a.state as TogetherCraftersAttack;
            CheckNativeUi("contact-starts-remote-attack", attack != null && attack.Peer == 3 && attack.Token > 0 && craftersRemoteAttacks == attacks + 1);

            // 8. World section round trip: parse-only and apply both consume the record; apply builds the spectator spin.
            byte[] section;
            using (var memory = new MemoryStream()) { using (var writer = new BinaryWriter(memory)) WriteCraftersWorld(writer); section = memory.ToArray(); }
            bool consumed = true;
            foreach (bool apply in new[] { false, true })
                using (var reader = new BinaryReader(new MemoryStream(section))) { ReadCraftersWorld(reader, apply); consumed &= reader.BaseStream.Position == reader.BaseStream.Length; }
            CraftersSpin labSpin;
            CheckNativeUi("world-section-roundtrip", consumed && attack != null && craftersSpins.TryGetValue(ia, out labSpin) && labSpin.Owner == 3 && labSpin.Token == attack.Token);
            craftersSpins.Clear();

            // 9. At the end the host moves Baldi and the owner's pose along the same native path; the crafters disappears.
            Vector3 baldiBefore = baldi.transform.position, ownerBefore = remote.Position; int teleports = craftersRemoteTeleports;
            until = Time.realtimeSinceStartup + 4;
            while (Time.realtimeSinceStartup < until && CraftersNative.Active(a)) { fresh(); yield return null; }
            CheckNativeUi("paired-teleport-baldi-and-owner", craftersRemoteTeleports == teleports + 1 && Vector3.Distance(baldi.transform.position, baldiBefore) > .1f
                && Vector3.Distance(remote.Position, ownerBefore) > .1f && !CraftersNative.Active(a));
            CraftersNative.RunTime.SetValue(a, runTime); CraftersNative.AttackTime.SetValue(a, attackTime);

            // 10. Owner client: the start command runs the native Attack once; duplicate and stale starts are ignored.
            int ownerAttacks = craftersOwnerAttacks;
            byte[] start = CraftersStartPacket(ib, 1001);
            CraftersLabCommand(start); CraftersLabCommand(start);
            epoch--; byte[] stale = CraftersStartPacket(ib, 1002); epoch++; CraftersLabCommand(stale);
            var echoes = CraftersNative.Get<Transform[]>(CraftersNative.Echo, b);
            CheckNativeUi("owner-native-attack-once", craftersOwnerAttacks == ownerAttacks + 1 && craftersOwner != null && craftersOwner.Token == 1001
                && CraftersNative.Get<bool>(CraftersNative.Attacking, b) && echoes.All(e => e == null || e.gameObject.activeSelf));

            // 11. The owner's crafters spins around its own player at the native spin distance.
            for (int f = 0; f < 10; f++) yield return null;
            Vector3 offset = b.transform.position - player.transform.position;
            CheckNativeUi("owner-spin-around-local-player", craftersOwner != null && Mathf.Abs(offset.magnitude - CraftersNative.Get<float>(CraftersNative.SpinDistance, b)) < .5f);

            // 12. The finish command teleports the owner exactly once; a replay does nothing.
            int ownerTeleports = craftersOwnerTeleports; Vector3 home = player.transform.position, destination = ec.spawnPoint;
            byte[] finish = CraftersFinishPacket(ib, 1001, true, destination);
            CraftersLabCommand(finish);
            bool landed = Vector3.Distance(player.transform.position, destination) < 1 && craftersOwner == null;
            player.Teleport(home); CraftersLabCommand(finish);
            CheckNativeUi("owner-teleport-once-replay-ignored", landed && craftersOwnerTeleports == ownerTeleports + 1 && Vector3.Distance(player.transform.position, home) < 1);

            // 13. Fleeing from a remote player releases its Dijkstra map when that player disconnects.
            var leaver = new Peer { Id = 2, Name = "Crafters leaver", Accepted = true, Ready = true, HasPose = true, Position = origin + Vector3.right * 2, PoseAt = Time.realtimeSinceStartup };
            peers[2] = leaver;
            CraftersLabGaze(leaver, epoch, 1, CraftersIndex(c), true);
            StartRemoteCraftersFlee(c, leaver);
            var flee = c.state as TogetherCraftersFlee;
            var maps = (List<DijkstraMap>)CraftersNative.DijkstraMaps.GetValue(ec);
            bool registered = flee != null && maps.Contains(flee.Map);
            peers.Remove(2);
            for (int f = 0; f < 3; f++) { fresh(); yield return null; }
            var queued = CraftersNative.DijkstraQueue.GetValue(ec) as Queue<DijkstraMap>;
            Log("LAB crafters disconnect: peerPresent=" + peers.ContainsKey(2) + " registered=" + registered + " fleeTracked=" + craftersFlees.Contains(flee)
                + " fleeMapActive=" + (flee != null && maps.Contains(flee.Map)) + " fleeMapQueued=" + (flee != null && queued != null && queued.Contains(flee.Map))
                + " activeMaps=" + maps.Count + " queuedMaps=" + (queued == null ? -1 : queued.Count) + " state=" + c.state.GetType().Name
                + " gazeKeys2=" + craftersGaze.Keys.Count(k => (k >> 16) == 2) + " seq2=" + craftersGazeSeq.ContainsKey(2) + " recordPeers=" + string.Join(",", craftersRecordPeers.Select(i => i.ToString()).ToArray()));
            CheckNativeUi("disconnect-releases-flee", registered && c.state is ArtsAndCrafters_Waiting && !maps.Contains(flee.Map) && !craftersFlees.Contains(flee)
                && !craftersGaze.Keys.Any(k => (k >> 16) == 2) && !craftersGazeSeq.ContainsKey(2));

            // 14. An eliminated chase target ends the encounter through the native DisappearForever path.
            int aborts = craftersAborts;
            remote.Position = origin + Vector3.right * 30; StartRemoteCraftersChase(c, remote); remote.Eliminated = true;
            for (int f = 0; f < 3; f++) { fresh(); yield return null; }
            CheckNativeUi("eliminated-target-aborts", craftersAborts == aborts + 1 && !CraftersNative.Active(c));
            remote.Eliminated = false;

            // 15. Reset releases owner attack, flee maps, gaze records and spectator spins.
            var renderer = CraftersNative.Get<SpriteRenderer>(CraftersNative.VisibleRenderer, b); int layer = renderer.gameObject.layer;
            CraftersLabCommand(CraftersStartPacket(ib, 1003));
            StartRemoteCraftersFlee(b, remote); var resetFlee = b.state as TogetherCraftersFlee;
            CraftersLabGaze(remote, epoch, seq++, ib, true);
            ResetCrafters();
            CheckNativeUi("reset-releases-state", craftersOwner == null && renderer.gameObject.layer == layer && echoes.All(e => e == null || !e.gameObject.activeSelf)
                && resetFlee != null && !maps.Contains(resetFlee.Map) && craftersFlees.Count == 0 && craftersGaze.Count == 0 && craftersSpins.Count == 0);
            // The proxy is destroyed at end of frame; afterwards no active or queued map may target a destroyed transform.
            for (int f = 0; f < 3; f++) yield return null;
            CheckNativeUi("reset-no-dangling-dijkstra-maps", CraftersNative.MapsClean(ec));

            CraftersChange(b, new ArtsAndCrafters_Waiting(b));
            foreach (var pair in chances) if (pair.Key != null) pair.Key.spawnChance = pair.Value;
            CraftersNative.FoundNotebooks.SetValue(manager, found);
            baldi.Navigator.enabled = baldiNav;
            peers.Remove(3);
            WriteLabResults("crafters-results.json", "Crafters");
        }
    }

    // Fleeing from a remote player: native Fleeing uses player.DijkstraMap; a proxy transform at the remote
    // pose drives an equivalent map that is registered on Enter and released on Exit/Reset.
    sealed class TogetherCraftersFlee : ArtsAndCrafters_StateBase
    {
        public readonly int Peer;
        public readonly ArtsAndCrafters Crafters;
        public Transform Proxy;
        public DijkstraMap Map;
        readonly Vector3 initial;
        bool released;
        public TogetherCraftersFlee(ArtsAndCrafters crafters, int peer, Vector3 position) : base(crafters) { Crafters = crafters; Peer = peer; initial = position; }
        public override void Enter()
        {
            base.Enter();
            Proxy = new GameObject("Crafters remote flee target").transform; Proxy.position = initial;
            Map = new DijkstraMap(crafters.ec, PathType.Nav, int.MaxValue, Proxy); Map.Activate(); Map.QueueUpdate();
            var runtime = TogetherRuntime.Instance; if (runtime != null) runtime.RegisterCraftersFlee(this, true);
            crafters.RunAway();
            ChangeNavigationState(new NavigationState_WanderFlee(crafters, 63, Map));
        }
        public override void Update()
        {
            base.Update();
            var runtime = TogetherRuntime.Instance;
            if (runtime == null || !runtime.Active) { crafters.state = new ArtsAndCrafters_Waiting(crafters); npc.behaviorStateMachine.ChangeState(crafters.state); return; }
            runtime.UpdateCraftersFlee(this);
        }
        public override void Exit() { base.Exit(); Release(); }
        public void Release()
        {
            if (released) return; released = true;
            // Unregister before the proxy dies: the map must leave both the active list and the update queue.
            if (Map != null && crafters != null && crafters.ec != null) CraftersNative.Unregister(crafters.ec, Map);
            if (Proxy != null) Object.Destroy(Proxy.gameObject);
            var runtime = TogetherRuntime.Instance; if (runtime != null) runtime.RegisterCraftersFlee(this, false);
        }
    }

    sealed class TogetherCraftersChase : ArtsAndCrafters_StateBase
    {
        public readonly int Peer;
        public readonly ArtsAndCrafters Crafters;
        public NavigationState_TargetPlayer Target;
        readonly Vector3 initial;
        public TogetherCraftersChase(ArtsAndCrafters crafters, int peer, Vector3 position) : base(crafters) { Crafters = crafters; Peer = peer; initial = position; }
        public override void Enter() { base.Enter(); Target = new NavigationState_TargetPlayer(npc, 63, initial); ChangeNavigationState(Target); }
        public override void DestinationEmpty() { base.DestinationEmpty(); ChangeNavigationState(new NavigationState_WanderRandom(npc, 0)); }
        public override void Update()
        {
            base.Update();
            var runtime = TogetherRuntime.Instance;
            if (runtime == null || !runtime.Active) { crafters.DisappearForever(); return; }
            runtime.UpdateCraftersChase(this);
        }
    }

    sealed class TogetherCraftersAttack : ArtsAndCrafters_StateBase
    {
        public readonly int Peer;
        public readonly ArtsAndCrafters Crafters;
        public int Token;
        public Vector3 Angle;
        public float Speed, Echo, Time;
        public bool Done;
        public TogetherCraftersAttack(ArtsAndCrafters crafters, int peer) : base(crafters) { Crafters = crafters; Peer = peer; }
        public override void Enter() { base.Enter(); var runtime = TogetherRuntime.Instance; if (runtime != null) runtime.BeginRemoteCraftersAttack(this); }
        public override void Update()
        {
            base.Update();
            var runtime = TogetherRuntime.Instance;
            if (runtime == null || !runtime.Active) { if (!Done) { Done = true; crafters.DisappearForever(); } return; }
            runtime.UpdateRemoteCraftersAttack(this);
        }
    }

    // Host only: the native Update bodies, with remote sight and validated remote gaze added to the host's Looker.
    [HarmonyPatch(typeof(ArtsAndCrafters_Ready), "Update")]
    static class CraftersReadyPatch
    { static bool Prefix(ArtsAndCrafters_Ready __instance) { var r = ModHooks.Current; return !TogetherRuntime.CraftersPatchGate(r) || !r.CraftersReadyUpdate(__instance); } }

    [HarmonyPatch(typeof(ArtsAndCrafters_Stalking), "Update")]
    static class CraftersStalkingPatch
    { static bool Prefix(ArtsAndCrafters_Stalking __instance) { var r = ModHooks.Current; return !TogetherRuntime.CraftersPatchGate(r) || !r.CraftersStalkingUpdate(__instance); } }

    [HarmonyPatch(typeof(ArtsAndCrafters_Fleeing), "Update")]
    static class CraftersFleeingPatch
    { static bool Prefix(ArtsAndCrafters_Fleeing __instance) { var r = ModHooks.Current; return !TogetherRuntime.CraftersPatchGate(r) || !r.CraftersFleeingUpdate(__instance); } }

    // Replicas never spawn locally; the host owns the trigger roll. An eliminated host does not trigger it.
    [HarmonyPatch(typeof(CraftersTrigger), "OnTriggerEnter")]
    static class CraftersTriggerPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.Active || (!ModHooks.ClientWorld && !r.LocalEliminated); } }

    // Client replicas only teleport the local player on the host's finish command.
    [HarmonyPatch(typeof(ArtsAndCrafters), "TeleportPlayer")]
    static class CraftersTeleportPatch
    { static bool Prefix() { return !ModHooks.ClientWorld || TogetherRuntime.Applying; } }
}
