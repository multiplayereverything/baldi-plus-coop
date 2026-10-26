using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // A remote rope owns no host PlayerManager or UI. Only its owner runs the native minigame.
    sealed class RemoteRopeState : NpcState
    {
        public RemoteRopeState(Playtime playtime) : base(playtime) { }
        public override void Enter() { base.Enter(); ChangeNavigationState(new NavigationState_DoNothing(npc, 127)); }
    }

    public sealed partial class TogetherRuntime
    {
        sealed class NpcRound { public int Owner, Token, Count, Points = 25; public float At, Deadline; }
        sealed class PushReplica { public NPC Npc; public Vector3 Velocity; public float Multiplier, At; public bool Enabled; public MovementModifier Modifier = new MovementModifier(Vector3.zero, 1); public ActivityModifier Attached; }
        readonly Dictionary<NPC, NpcRound> npcRounds = new Dictionary<NPC, NpcRound>();
        readonly Dictionary<NPC, float> npcCooldown = new Dictionary<NPC, float>();
        readonly Dictionary<NPC, PushReplica> npcPushes = new Dictionary<NPC, PushReplica>();
        readonly HashSet<int> npcCommands = new HashSet<int>();
        readonly Dictionary<Playtime, int> localRopes = new Dictionary<Playtime, int>();
        readonly HashSet<Bully> remoteBullyTaunts = new HashSet<Bully>();
        readonly HashSet<Playtime> remotePlaytimeChases = new HashSet<Playtime>();
        int nextNpcToken, ropeStarts, ropeEnds, bullyThefts, npcPushFrames, remoteNpcTargets;
        float nextPlayerStatus, nextNpcContacts;

        static T NpcField<T>(object npc, string name) { return (T)AccessTools.Field(npc.GetType(), name).GetValue(npc); }

        static string NpcPrefabName(NPC npc) { return npc.name.EndsWith("(Clone)",StringComparison.Ordinal) ? npc.name.Substring(0,npc.name.Length-7) : npc.name; }

        void TrimReplicaNpcs(EnvironmentController ec, int count)
        {
            while (ec.Npcs.Count > count)
            {
                NPC old = ec.Npcs[ec.Npcs.Count-1];
                if (old != null)
                {
                    var playtime = old as Playtime;
                    if (playtime != null && localRopes.ContainsKey(playtime))
                    { var rope = NpcField<Jumprope>(playtime,"currentJumprope"); if (rope != null) rope.Destroy(); localRopes.Remove(playtime); }
                    PushReplica push; if (npcPushes.TryGetValue(old,out push) && push.Attached != null) push.Attached.moveMods.Remove(push.Modifier);
                    var cloud = old as Cumulo;
                    if (cloud != null)
                    {
                        var wind = NpcField<BeltManager>(cloud,"windManager");
                        if (wind != null) { wind.gameObject.SetActive(false); UnityEngine.Object.Destroy(wind.gameObject); }
                    }
                    npcPushes.Remove(old); npcSamples.Remove(old); npcPresentation.Remove(old); disabledNavigators.Remove(old.Navigator);
                    old.Despawn();
                }
                else ec.Npcs.RemoveAt(ec.Npcs.Count-1);
            }
            npcs = ec.Npcs.ToArray();
        }

        void EnsureReplicaNpc(EnvironmentController ec, int index, int character, string prefab, Vector3 position)
        {
            if (index < ec.Npcs.Count && ec.Npcs[index] != null && (int)ec.Npcs[index].Character == character && NpcPrefabName(ec.Npcs[index]) == prefab) return;
            TrimReplicaNpcs(ec,index);
            NPC template = ReplicaTemplate(character, prefab);
            if (template == null) throw new IOException("Host NPC prefab unavailable: " + prefab);
            NPC replica = ec.SpawnNPC(template,ec.CellFromPosition(position).position);
            if (replica.Navigator != null) { replica.Navigator.enabled = false; disabledNavigators.Add(replica.Navigator); }
            npcs = ec.Npcs.ToArray();
        }

        // Game prefabs are scene-less assets; mod-built prefabs (the Hall Monitor) live in a persistent holder.
        static NPC ReplicaTemplate(int character, string prefab)
        {
            if (character == (int)HallMonitor.Id && prefab == HallMonitor.PrefabName) return HallMonitorPrefab.Get();
            return Resources.FindObjectsOfTypeAll<NPC>().FirstOrDefault(n => !n.gameObject.scene.IsValid() && (int)n.Character == character && n.name == prefab);
        }

        void ResetNpcInteractions()
        {
            foreach (var push in npcPushes.Values) if (push.Attached != null) push.Attached.moveMods.Remove(push.Modifier);
            foreach (var rope in localRopes.Keys.ToArray()) if (rope != null)
            { var current = NpcField<Jumprope>(rope, "currentJumprope"); if (current != null) current.Destroy(); }
            npcRounds.Clear(); npcCooldown.Clear(); npcPushes.Clear(); npcCommands.Clear(); localRopes.Clear();
            remoteBullyTaunts.Clear(); remotePlaytimeChases.Clear();
            nextPlayerStatus = nextNpcContacts = 0;
        }

        Peer NearestVisiblePeer(NPC npc)
        {
            if (npc.Blinded) return null;
            float now = Time.realtimeSinceStartup;
            return peers.Values.Where(p => p.Accepted && p.HasPose && (p.Id == self || p.Ready) && !p.Eliminated && !p.InLocker && !p.Tagged && !p.Invisible && now-p.PoseAt < 2
                && Vector3.Distance(p.Position,npc.transform.position) < Mathf.Min(80,npc.looker.distance) && ClearWorldLine(npc.transform.position,p.Position))
                .OrderBy(p => (p.Position-npc.transform.position).sqrMagnitude).ThenBy(p => p.Id).FirstOrDefault();
        }

        void ApplyBullyReplicaComponents(Bully bully, bool visible)
        {
            // SetComponents calls reference-counted Entity.SetVisible. Presentation already
            // applies that semantic state, so mirror only its physical components here.
            foreach (var collider in NpcField<Collider[]>(bully,"collidersToHide"))
                if (collider != null) collider.enabled = visible;
            var obstacle = NpcField<UnityEngine.AI.NavMeshObstacle>(bully,"obstacleToHide");
            if (obstacle != null) obstacle.enabled = visible;
            AccessTools.Field(typeof(Bully),"hidden").SetValue(bully,!visible);
            var blocker = NpcField<GameObject>(bully,"raycastBlocker");
            if (blocker != null) blocker.transform.position = bully.transform.position;
        }

        public void CheckBullySpawn(Bully bully)
        {
            if (!Hosting || !worldReady || !(bully.behaviorStateMachine.CurrentState is Bully_Active)) return;
            float buffer = NpcField<float>(bully,"playerBuffer");
            float now = Time.realtimeSinceStartup;
            if (peers.Values.Any(p => p.Id != self && p.Accepted && p.Ready && p.HasPose && !p.Eliminated && !p.Tagged
                && now-p.PoseAt < 2 && (p.Position-bully.transform.position).sqrMagnitude <= buffer*buffer)) bully.Hide();
        }

        void WriteNpcWind(BinaryWriter writer, NPC npc)
        {
            var cloud = npc as Cumulo;
            writer.Write(cloud != null);
            if (cloud == null) return;
            var wind = NpcField<BeltManager>(cloud,"windManager");
            writer.Write(wind.gameObject.activeSelf);
            WriteVector(writer,wind.transform.position);
            WriteVector(writer,wind.BoxCollider.size);
            writer.Write((int)NpcField<Direction>(wind,"dir"));
            writer.Write(NpcField<float>(wind,"setSpeed"));
        }

        void ReadNpcWind(BinaryReader reader, NPC npc)
        {
            if (!reader.ReadBoolean()) return;
            bool active = reader.ReadBoolean();
            Vector3 position = ReadVector(reader), size = ReadVector(reader);
            int direction = reader.ReadInt32(); float speed = Wire.Number(reader);
            if (direction < 0 || direction > (int)Direction.Null || size.x < 0 || size.y < 0 || size.z < 0
                || size.x > 10000 || size.y > 100 || size.z > 10000 || speed < 0 || speed > 100)
                throw new IOException("Invalid NPC wind region.");
            var cloud = npc as Cumulo;
            if (cloud == null) return;
            var wind = NpcField<BeltManager>(cloud,"windManager");
            wind.transform.position = position; wind.BoxCollider.size = size;
            // Native BeltManager trigger callbacks attach/remove the local player's force.
            // It owns cleanup on disable; no separate guessed distance contact is needed.
            wind.SetDirection((Direction)direction); wind.SetSpeed(speed);
            if (wind.gameObject.activeSelf != active) wind.gameObject.SetActive(active);
            var graphics = NpcField<Transform>(cloud,"windGraphicsParent");
            if (direction != (int)Direction.Null) graphics.rotation = ((Direction)direction).ToRotation();
            float length = Mathf.Max(size.x,size.z);
            foreach (var renderer in NpcField<MeshRenderer[]>(cloud,"windGraphics"))
                if (renderer != null) renderer.transform.localScale = new Vector3(10,length,1);
            if (wind.newMaterial != null) wind.newMaterial.SetVector("_Tiling",new Vector2(1,Mathf.Max(1,length/10)));
        }

        public void UpdateRemoteNpcTarget(NPC npc)
        {
            if (!Hosting || !worldReady || npc == null || !npc.ec.Active) return;
            if (!(npc is FirstPrize) && !(npc is Playtime)) return;
            Peer closest = NearestVisiblePeer(npc);
            if (closest == null || closest.Id == self) { ReleaseRemotePlaytime(npc as Playtime, closest == null); return; }
            var prize = npc as FirstPrize;
            if (prize != null)
            {
                var active = prize.behaviorStateMachine.CurrentState as FirstPrize_Active;
                if (active == null) return;
                active.ChargeTarget(closest.Position,false);
                AccessTools.Field(typeof(FirstPrize_Active),"unseenDelay").SetValue(active,1f);
                remoteNpcTargets++;
            }
            var playtime = npc as Playtime;
            if (playtime != null && playtime.behaviorStateMachine.CurrentState is Playtime_Wandering)
            {
                var nav = playtime.behaviorStateMachine.CurrentNavigationState as NavigationState_TargetPlayer;
                if (nav == null) playtime.behaviorStateMachine.ChangeNavigationState(new NavigationState_TargetPlayer(playtime,63,closest.Position));
                else nav.UpdatePosition(closest.Position);
                playtime.Navigator.maxSpeed = NpcField<float>(playtime,"runSpeed"); playtime.Navigator.SetSpeed(playtime.Navigator.maxSpeed);
                remotePlaytimeChases.Add(playtime);
                remoteNpcTargets++;
            }
        }

        // Native PlayerLost only fires for the host's own Looker, so a lost remote target needs the same turn-around.
        void ReleaseRemotePlaytime(Playtime playtime, bool lost)
        {
            if (playtime == null || !remotePlaytimeChases.Remove(playtime)) return;
            if (lost && playtime.behaviorStateMachine.CurrentState is Playtime_Wandering) playtime.PlayerTurnAround(null);
        }

        void SendNpcCommand(int owner, NPC npc, NpcRound round, byte action)
        {
            int index = Array.IndexOf(npcs,npc);
            if (index < 0) return;
            Network.Send(owner,Wire.Packet(Op.NpcCommand,w => { w.Write(epoch); w.Write(index); w.Write((int)npc.Character); w.Write(round.Token); w.Write(action); }));
        }

        void FinishRemoteRope(Playtime playtime, NpcRound round, bool won)
        {
            npcRounds.Remove(playtime);
            if (peers.ContainsKey(round.Owner)) SendNpcCommand(round.Owner,playtime,round,3);
            playtime.Navigator.maxSpeed = NpcField<float>(playtime,"normSpeed"); playtime.Navigator.SetSpeed(playtime.Navigator.maxSpeed);
            if (!won) playtime.BecomeSad();
            else
            {
                NpcField<AudioManager>(playtime,"audMan").PlaySingle(NpcField<SoundObject>(playtime,"audCongrats"));
                Singleton<CoreGameManager>.Instance.AddPoints(round.Points,0,true);
            }
            playtime.behaviorStateMachine.ChangeState(new Playtime_Cooldown(playtime,playtime,playtime.InitialCooldown));
            ropeEnds++;
        }

        void UpdateNpcInteractions()
        {
            if (!worldReady || !Active) return;
            float now = Time.realtimeSinceStartup;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player == null) return;
            if (!Hosting)
            {
                if (readyAcknowledged && now >= nextPlayerStatus)
                { nextPlayerStatus = now + .25f; Network.Send(0,Wire.Packet(Op.PlayerStatus,w => { w.Write(epoch); w.Write(player.Tagged); w.Write(player.Invisible); })); }
                foreach (var push in npcPushes.Values)
                {
                    bool touching = !LocalEliminated && push.Npc != null && push.Enabled && now-push.At < .5f && !player.Tagged
                        && Vector3.Distance(player.transform.position,push.Npc.transform.position) < 6
                        && ClearWorldLine(player.transform.position,push.Npc.transform.position);
                    if (!touching) { if (push.Attached != null) push.Attached.moveMods.Remove(push.Modifier); push.Attached = null; continue; }
                    if (push.Attached != player.plm.am) { if (push.Attached != null) push.Attached.moveMods.Remove(push.Modifier); push.Attached = player.plm.am; push.Attached.moveMods.Add(push.Modifier); }
                    push.Modifier.movementAddend = push.Velocity; push.Modifier.movementMultiplier = push.Multiplier; push.Modifier.forceTrigger = true;
                    npcPushFrames++;
                }
                return;
            }
            foreach (var pair in npcRounds.ToArray())
                if (pair.Key == null) npcRounds.Remove(pair.Key);
                else if (now > pair.Value.Deadline || !peers.ContainsKey(pair.Value.Owner) || !peers[pair.Value.Owner].Ready || peers[pair.Value.Owner].Eliminated
                    || (pair.Key is Playtime && (peers[pair.Value.Owner].Tagged || Vector3.Distance(peers[pair.Value.Owner].Position,pair.Key.transform.position)>60)))
                { if (pair.Key is Playtime) FinishRemoteRope((Playtime)pair.Key,pair.Value,false); else npcRounds.Remove(pair.Key); }
            if (now < nextNpcContacts) return; nextNpcContacts = now + .1f;
            foreach (NPC npc in npcs)
            {
                var bully = npc as Bully;
                if (bully != null)
                {
                    if (!(bully.behaviorStateMachine.CurrentState is Bully_Active)) remoteBullyTaunts.Remove(bully);
                    else
                    {
                        var sighted = NearestVisiblePeer(bully);
                        if (sighted != null && sighted.Id != self)
                        {
                            bully.SetGuilty();
                            if (remoteBullyTaunts.Add(bully)) bully.Taunt();
                        }
                    }
                }
                if (npc == null || npcRounds.ContainsKey(npc)) continue;
                float cooldown; if (npcCooldown.TryGetValue(npc,out cooldown) && now < cooldown) continue;
                if (!(npc is Playtime && npc.behaviorStateMachine.CurrentState is Playtime_Wandering) && !(npc is Bully && npc.behaviorStateMachine.CurrentState is Bully_Active)) continue;
                Peer closest = NearestVisiblePeer(npc);
                if (closest == null || closest.Id == self || Vector3.Distance(closest.Position,npc.transform.position) > 5) continue;
                var round = new NpcRound { Owner = closest.Id, Token = ++nextNpcToken, At = now, Deadline = now + (npc is Playtime ? 90 : 8) };
                npcRounds.Add(npc,round); npcCooldown[npc] = now + 2;
                if (npc is Playtime)
                {
                    npc.behaviorStateMachine.ChangeState(new RemoteRopeState((Playtime)npc)); npc.Navigator.SetSpeed(0); npc.Navigator.maxSpeed = 0;
                    NpcField<AudioManager>(npc,"audMan").PlaySingle(NpcField<SoundObject>(npc,"audGo")); ropeStarts++;
                }
                SendNpcCommand(closest.Id,npc,round,npc is Playtime ? (byte)1 : (byte)2);
                if (npcSyncLab && npc is Bully) SendNpcCommand(closest.Id,npc,round,2);
            }
        }

        void WriteNpcPushes(BinaryWriter w)
        {
            NPC[] sources = npcs.Where(n => n is GottaSweep || n is FirstPrize).ToArray();
            w.Write((byte)sources.Length);
            foreach (NPC npc in sources)
            {
                bool enabled = false; Vector3 velocity = Vector3.zero; float multiplier = 1;
                if (npc is GottaSweep)
                { var mod = NpcField<MovementModifier>(npc,"moveMod"); enabled = mod.forceTrigger; velocity = mod.movementAddend; multiplier = mod.movementMultiplier; }
                else
                {
                    var state = npc.behaviorStateMachine.CurrentState as FirstPrize_Active;
                    enabled = state != null && NpcField<bool>(state,"hugEnabled");
                    if (enabled) { velocity = npc.transform.forward * ((npc.Navigator.Speed*npc.ec.NpcTimeScale + npc.Navigator.Acceleration)*npc.Entity.ExternalActivity.Multiplier) + npc.Entity.ExternalActivity.Addend; multiplier = .1f; }
                }
                w.Write(Array.IndexOf(npcs,npc)); w.Write((int)npc.Character); w.Write(enabled); WriteVector(w,velocity); w.Write(multiplier);
            }
        }

        void ReadNpcPushes(BinaryReader r, bool apply)
        {
            int count = r.ReadByte(); if (count > 128) throw new IOException("NPC force limit exceeded.");
            for (int i = 0; i < count; i++)
            {
                int index = r.ReadInt32(), character = r.ReadInt32(); bool enabled = r.ReadBoolean(); Vector3 velocity = ReadVector(r); float multiplier = Wire.Number(r);
                if (velocity.magnitude > 2000 || multiplier < 0 || multiplier > 10) throw new IOException("Invalid NPC force.");
                if (!apply || index < 0 || index >= npcs.Length || (int)npcs[index].Character != character) continue;
                NPC npc = npcs[index]; if (!(npc is GottaSweep) && !(npc is FirstPrize)) throw new IOException("Invalid NPC force source.");
                PushReplica push; if (!npcPushes.TryGetValue(npc,out push)) npcPushes[npc] = push = new PushReplica { Npc = npc };
                push.Enabled = enabled; push.Velocity = velocity; push.Multiplier = multiplier; push.At = Time.realtimeSinceStartup;
            }
        }

        bool HandleNpcClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op == Op.PlayerStatus)
            { int generation = r.ReadInt32(); bool tagged = r.ReadBoolean(), invisible = r.ReadBoolean(); if (generation == epoch && sender.Ready) { sender.Tagged = tagged; sender.Invisible = invisible; } return true; }
            if (op != Op.NpcReply) return false;
            int level = r.ReadInt32(), index = r.ReadInt32(), token = r.ReadInt32(); byte action = r.ReadByte(); int value = r.ReadInt32();
            if (level != epoch || !worldReady || !sender.Ready || sender.Eliminated || index < 0 || index >= npcs.Length) return true;
            NPC npc = npcs[index]; NpcRound round;
            if (!npcRounds.TryGetValue(npc,out round) || round.Owner != id || round.Token != token || Time.realtimeSinceStartup > round.Deadline) return true;
            var playtime = npc as Playtime;
            if (playtime != null)
            {
                if (action == 1 && value == round.Count+1 && value <= 5)
                { round.Count = value; playtime.Count(value); }
                else if (action == 2) { round.Count = 0; round.Points = Math.Max(0,round.Points-5); playtime.JumpropeHit(); }
                else if (action == 3 && (value == 0 || (value == 1 && round.Count >= 5 && Time.realtimeSinceStartup-round.At >= 5))) FinishRemoteRope(playtime,round,value == 1);
            }
            else if (npc is Bully && action == 4 && (value == 0 || value == 1))
            { if (value == 1) { if (npc.behaviorStateMachine.CurrentState is Bully_Active) ((Bully)npc).Hide(); bullyThefts++; } npcRounds.Remove(npc); npcCooldown[npc] = Time.realtimeSinceStartup+2; }
            return true;
        }

        void SendNpcReply(NPC npc, int token, byte action, int value)
        { int index = Array.IndexOf(npcs,npc); Network.Send(0,Wire.Packet(Op.NpcReply,w => { w.Write(epoch); w.Write(index); w.Write(token); w.Write(action); w.Write(value); })); }

        bool HandleNpcHost(Op op, BinaryReader r)
        {
            if (op != Op.NpcCommand) return false;
            int generation = r.ReadInt32(), index = r.ReadInt32(), character = r.ReadInt32(), token = r.ReadInt32(); byte action = r.ReadByte();
            if (action < 1 || action > 3 || token < 1) throw new IOException("Invalid NPC command.");
            if (generation != epoch || !worldReady || !readyAcknowledged || index < 0 || index >= npcs.Length || (int)npcs[index].Character != character) return true;
            NPC npc = npcs[index]; var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            if (action == 3)
            {
                var playtime = npc as Playtime; int active;
                if (playtime != null && localRopes.TryGetValue(playtime,out active) && active == token)
                { var rope = NpcField<Jumprope>(playtime,"currentJumprope"); if (rope != null) rope.Destroy(); localRopes.Remove(playtime); ropeEnds++; }
                return true;
            }
            if (npcCommands.Count >= 4096) npcCommands.Clear();
            if (!npcCommands.Add(token)) return true;
            if (LocalEliminated) return true;
            Applying = true;
            try
            {
                if (action == 1 && npc is Playtime)
                { localRopes[(Playtime)npc] = token; ((Playtime)npc).StartJumprope(player); ropeStarts++; }
                else if (action == 2 && npc is Bully)
                {
                    int before = player.itm.items.Count(item => item != null && item.itemType != Items.None);
                    ((Bully)npc).StealItem(player);
                    bool stolen = player.itm.items.Count(item => item != null && item.itemType != Items.None) < before;
                    if (stolen) bullyThefts++;
                    SendNpcReply(npc,token,4,stolen ? 1 : 0);
                }
                else throw new IOException("NPC command type mismatch.");
            }
            finally { Applying = false; }
            return true;
        }

        public void ReportRope(Playtime playtime, byte action, int value)
        {
            int token;
            if (Active && !Hosting && !Applying && localRopes.TryGetValue(playtime,out token))
            { SendNpcReply(playtime,token,action,value); if (action == 3) { localRopes.Remove(playtime); ropeEnds++; } }
        }
        public bool CanStartLocalRope() { return !Active || (!LocalEliminated && (Hosting || Applying)); }
        public bool CanStealLocal(Bully bully) { return !Active || (!LocalEliminated && (Applying || (Hosting && !npcRounds.ContainsKey(bully)))); }
        string NpcInteractionDiagnostic()
        { return ",\"ropeStarts\":" + ropeStarts + ",\"ropeEnds\":" + ropeEnds + ",\"bullyThefts\":" + bullyThefts + ",\"npcPushFrames\":" + npcPushFrames + ",\"remoteNpcTargets\":" + remoteNpcTargets + ",\"npcRounds\":" + npcRounds.Count
            + ",\"bullyPhysicalMatches\":" + npcs.OfType<Bully>().Count(b => NpcField<Collider[]>(b,"collidersToHide").All(c=>c==null || c.enabled==b.Entity.Visible) && NpcField<UnityEngine.AI.NavMeshObstacle>(b,"obstacleToHide").enabled==b.Entity.Visible)
            + ",\"cloudWindActive\":" + npcs.OfType<Cumulo>().Count(c=>NpcField<BeltManager>(c,"windManager").gameObject.activeSelf); }
    }

    [HarmonyPatch(typeof(Looker),"Update")]
    static class ReplicaLookerPatch { static bool Prefix() { return !ModHooks.ClientWorld; } }
    [HarmonyPatch(typeof(EnvironmentController),"SpawnNPCs")]
    static class ReplicaNpcSpawnerPatch { static bool Prefix() { return !ModHooks.ClientWorld; } }
    [HarmonyPatch(typeof(Playtime),"StartJumprope")]
    static class RopeStartPatch { static bool Prefix() { var r = ModHooks.Current; return r == null || r.CanStartLocalRope(); } }
    [HarmonyPatch(typeof(Playtime),"Count")]
    static class RopeCountPatch { static void Postfix(Playtime __instance,int jumps) { var r = ModHooks.Current; if (r != null) r.ReportRope(__instance,1,jumps); } }
    [HarmonyPatch(typeof(Playtime),"JumpropeHit")]
    static class RopeHitPatch { static void Postfix(Playtime __instance) { var r = ModHooks.Current; if (r != null) r.ReportRope(__instance,2,0); } }
    [HarmonyPatch(typeof(Playtime),"EndJumprope")]
    static class RopeEndPatch { static void Postfix(Playtime __instance,bool won) { var r = ModHooks.Current; if (r != null) r.ReportRope(__instance,3,won ? 1 : 0); } }
    [HarmonyPatch(typeof(Bully),"StealItem")]
    static class BullyStealPatch { static bool Prefix(Bully __instance) { var r = ModHooks.Current; return r == null || r.CanStealLocal(__instance); } }
    [HarmonyPatch(typeof(Bully),"Spawn")]
    static class BullySpawnBufferPatch { static void Postfix(Bully __instance) { var r = ModHooks.Current; if (r != null) r.CheckBullySpawn(__instance); } }
}
