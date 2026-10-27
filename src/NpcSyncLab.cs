using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        bool npcSyncLab;
        // Lab scenarios that stage NPC encounters suppress Baldi catches; they eliminate players explicitly when they need to.
        static readonly bool encounterLab = Lab.HasArg("-bt-encounter") || Lab.HasArg("-bt-soak");
        public bool NpcTestRunning { get { return Lab.Enabled && (npcSyncLab || encounterLab); } }
        int npcLabStage, npcLabClientPhase, npcLabJumps;
        float npcLabAt, npcLabJumpAt;
        bool npcLabFirstPrizeTarget;
        Vector3 npcLabAnchor;
        Vector3 npcLabHostPosition;
        string npcLabPending;
        [Serializable] public sealed class NpcLabCommand { public int Phase; public Vector3 Position; }
        string NpcLabPath { get { return Path.Combine(Hosting ? Lab.Workspace : Lab.Shared, "private/npc-command.json"); } }

        void NpcLabMoveClient(int phase)
        {
            npcLabPending = JsonUtility.ToJson(new NpcLabCommand { Phase = phase, Position = npcLabAnchor });
            FlushNpcLabCommand();
        }

        void FlushNpcLabCommand()
        {
            if (npcLabPending == null) return;
            try
            {
                using (var file = new FileStream(NpcLabPath,FileMode.Create,FileAccess.Write,FileShare.None))
                using (var writer = new StreamWriter(file)) writer.Write(npcLabPending);
                npcLabPending = null;
            }
            catch (IOException) { /* A reader owns the file briefly; retry the pending command next frame. */ }
        }

        void UpdateNpcSyncLab()
        {
            if (!npcSyncLab || !worldReady || !hidingFinished) return;
            var manager = Singleton<BaseGameManager>.Instance; var core = Singleton<CoreGameManager>.Instance;
            if (manager == null || core == null || !manager.Ec.Active) return;
            if (Hosting) FlushNpcLabCommand();
            float now = Time.realtimeSinceStartup;
            if (!Hosting)
            {
                if (File.Exists(NpcLabPath))
                {
                    string payload;
                    try
                    {
                        using (var file = new FileStream(NpcLabPath,FileMode.Open,FileAccess.Read,FileShare.None))
                        using (var reader = new StreamReader(file)) payload = reader.ReadToEnd();
                    }
                    catch (IOException) { return; }
                    var command = JsonUtility.FromJson<NpcLabCommand>(payload);
                    if (command.Phase != npcLabClientPhase)
                    {
                        npcLabClientPhase = command.Phase; if (command.Phase!=8) core.GetPlayer(0).Teleport(command.Position);
                        if (command.Phase == 1 || command.Phase == 3)
                        {
                            var inventory = core.GetPlayer(0).itm; inventory.ClearItems();
                            inventory.AddItem(Resources.FindObjectsOfTypeAll<ItemObject>().First(o => o.itemType == Items.Bsoda && o.addToInventory));
                        }
                    }
                }
                if (npcLabClientPhase == 1 && localRopes.Count > 0)
                {
                    Playtime playtime = localRopes.Keys.First(); var rope = NpcField<Jumprope>(playtime,"currentJumprope");
                    if (npcLabJumpAt == 0) { npcLabJumpAt = now + 1; return; }
                    if (now < npcLabJumpAt) return;
                    if (rope != null) rope.StopAllCoroutines();
                    npcLabJumpAt = now + 1.1f;
                    if (npcLabJumps < 5) playtime.Count(++npcLabJumps); else playtime.EndJumprope(true);
                }
                if (npcLabClientPhase == 7) core.GetPlayer(0).RuleBreak("Faculty",4,.1f);
                if (npcLabClientPhase == 9) core.GetPlayer(0).RuleBreak("Running",1,1f); // the Hall Monitor stage: the client runs in his sight
                return;
            }
            if (!peers.Values.Any(p => p.Id != self && p.Ready && p.HasPose)) return;
            var ec = manager.Ec;
            if (npcLabStage == 0)
            {
                npcLabAnchor = ec.spawnPoint + ec.spawnRotation * new Vector3(3,0,12);
                foreach (Type type in new[] { typeof(Playtime),typeof(Bully),typeof(GottaSweep),typeof(FirstPrize),typeof(Principal) })
                    if (!ec.Npcs.Any(n => n.GetType() == type))
                    {
                        NPC template = Resources.FindObjectsOfTypeAll<NPC>().First(n => n.GetType() == type && !n.gameObject.scene.IsValid());
                        ec.SpawnNPC(template,ec.CellFromPosition(npcLabAnchor).position);
                    }
                foreach (NPC npc in ec.Npcs)
                {
                    npc.Navigator.enabled = false;
                    if (!disabledNavigators.Contains(npc.Navigator)) disabledNavigators.Add(npc.Navigator);
                    npc.Entity.Teleport(ec.RealRoomMid(ec.rooms.First(r => r.category == RoomCategory.Class)));
                }
                core.GetPlayer(0).Teleport(ec.RealRoomMid(ec.rooms.First(r => r.category == RoomCategory.Class)));
                npcLabHostPosition = core.GetPlayer(0).transform.position;
                var playtime = ec.Npcs.OfType<Playtime>().First(); playtime.Entity.Teleport(npcLabAnchor);
                playtime.behaviorStateMachine.ChangeState(new Playtime_Wandering(playtime,playtime));
                ec.Npcs.OfType<FirstPrize>().First().Entity.Teleport(npcLabAnchor + ec.spawnRotation * Vector3.forward * 7);
                NpcLabMoveClient(1); npcLabStage = 1; npcLabAt = now + 2;
                Log("NPC LAB: configured native prefabs and remote Playtime contact.");
            }
            var sweep = ec.Npcs.OfType<GottaSweep>().First();
            if (npcLabStage == 4)
            {
                var mod = NpcField<MovementModifier>(sweep,"moveMod");
                mod.forceTrigger = true; mod.movementAddend = Vector3.right*12; mod.movementMultiplier = .9f;
            }
            if (now < npcLabAt) return;
            if (npcLabStage == 1)
            {
                var state = ec.Npcs.OfType<FirstPrize>().First().behaviorStateMachine.CurrentState as FirstPrize_Active;
                npcLabFirstPrizeTarget |= state != null && NpcField<float>(state,"unseenDelay") > .5f;
                if (ropeEnds == 0) return;
                ec.Npcs.OfType<Playtime>().First().Entity.Teleport(core.GetPlayer(0).transform.position);
                var bully = ec.Npcs.OfType<Bully>().First(); bully.Entity.Teleport(npcLabAnchor); bully.SetComponents(true);
                AccessTools.Field(typeof(Bully),"hidden").SetValue(bully,false); bully.behaviorStateMachine.ChangeState(new Bully_Active(bully,bully,60));
                NpcLabMoveClient(3); npcLabStage = 3; npcLabAt = now + 2;
            }
            else if (npcLabStage == 3 && bullyThefts > 0)
            { sweep.Entity.Teleport(npcLabAnchor); sweep.StartSweeping(); NpcLabMoveClient(4); npcLabStage = 4; npcLabAt = now + 3; }
            else if (npcLabStage == 4)
            {
                sweep.StopSweeping(); sweep.Entity.Teleport(core.GetPlayer(0).transform.position);
                ec.Npcs.OfType<Bully>().First().Entity.Teleport(npcLabAnchor); ec.Npcs.OfType<Bully>().First().SetComponents(true);
                NpcLabMoveClient(5); npcLabStage = 5; npcLabAt = now + 3;
            }
            else if (npcLabStage == 5)
            { ec.Npcs.OfType<Bully>().First().SetComponents(false); npcLabStage = 6; npcLabAt = now + 3; }
            else if (npcLabStage == 6)
            {
                var principal = ec.Npcs.OfType<Principal>().First(); principal.Entity.Teleport(npcLabAnchor + Vector3.forward*2);
                principal.behaviorStateMachine.ChangeState(new Principal_Wandering(principal));
                NpcLabMoveClient(7); npcLabStage = 7; npcLabAt = now + 4;
            }
            else if (npcLabStage == 7 && principalRemoteDetentions > 0)
            { ec.Npcs.OfType<FirstPrize>().First().Despawn(); NpcLabMoveClient(8); npcLabStage = 8; npcLabAt = now + 3; }
            else if (npcLabStage == 8 && principalDetentions.Count == 0)
            {
                // Hall Monitor: the Principal leaves; the monitor stands six units from the client's spot, facing it.
                var room = ec.RealRoomMid(ec.rooms.First(r => r.category == RoomCategory.Class));
                ec.Npcs.OfType<Principal>().First().Entity.Teleport(room);
                var monitor = ec.Npcs.OfType<HallMonitor>().FirstOrDefault();
                if (monitor != null)
                {
                    Vector3 dir = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left }.FirstOrDefault(d => ClearWorldLine(npcLabAnchor + Vector3.up, npcLabAnchor + d * 7));
                    if (dir == Vector3.zero) dir = Vector3.forward;
                    monitor.Entity.Teleport(npcLabAnchor + dir * 6); monitor.transform.rotation = Quaternion.LookRotation(-dir);
                    monitor.behaviorStateMachine.ChangeState(new HallMonitor_Busy(monitor, HallMonitor.Look.Patrol, 30));
                }
                NpcLabMoveClient(9); npcLabStage = 9;
                Log("NPC LAB: Hall Monitor stage.");
            }
        }

        string NpcLabDiagnostic()
        {
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            string catalog = string.Join("|",npcs.Where(n=>n!=null).Select(n=>(int)n.Character+":"+NpcPrefabName(n)).ToArray());
            using (var sha = System.Security.Cryptography.SHA256.Create()) catalog = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(catalog))).Replace("-","");
            return ",\"npcLabStage\":"+npcLabStage+",\"npcLabFirstPrizeTarget\":"+npcLabFirstPrizeTarget.ToString().ToLowerInvariant()
                +",\"npcCatalog\":\""+catalog+"\",\"visibleBully\":"+npcs.Count(n=>n is Bully && n.Entity.Visible)
                +",\"attachedNpcPushes\":"+npcPushes.Values.Count(p=>p.Attached!=null);
        }

        string NpcPlayerDiagnostic()
        {
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            bool detained = player != null && principalDetentions.Any(d=>d.Peer==self && d.Time>0 && player.ec.CellFromPosition(player.transform.position).room==d.Room);
            return ",\"localJumpropes\":"+(player == null ? 0 : player.jumpropes.Count)
                +",\"localDetained\":"+detained.ToString().ToLowerInvariant()
                +",\"npcLabHostStayed\":"+(player != null && Hosting && npcLabStage>0 && Vector3.Distance(player.transform.position,npcLabHostPosition)<.5f).ToString().ToLowerInvariant();
        }
    }
}
