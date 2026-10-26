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
    // The Test (LookAtGuy) multiplayer sync.
    // Ops: TheTestToClient = 76 (host to clients), TheTestToHost = 77 (client to host).
    //
    // Native behaviour (LookAtGuy_Active): while a player is in line of sight and looking at The Test
    // it freezes every NPC through a global TimeScaleModifier and flickering lights, and pressure builds;
    // when that player looks away or loses sight it teleports (Respawning) and reappears once no one looks.
    // At full pressure it may explode: Blind() shows a HUD gauge and speeds up time until Respawn().
    //
    // Multiplayer: every client reports its own camera gaze at each replica (TheTestToHost/Gaze). The host
    // validates the claim against the peer pose (distance, native Looker line of sight, facing, claimed
    // replica position) and combines all gazes with its own player to drive the native state.
    // Global effects (time scale, NPC freeze lights/audio) are mirrored by clients from the world section.
    // The per-player blindness gauge and blind audio are applied by each owner client to its own HUD.
    [HarmonyPatch(typeof(LookAtGuy_Active), "Update")]
    static class TheTestActiveUpdatePatch
    { static bool Prefix(LookAtGuy_Active __instance) { var r = ModHooks.Current; return r == null || !r.TheTestDriving || !r.DriveTheTestActive(__instance); } }

    [HarmonyPatch(typeof(LookAtGuy_Active), "PlayerInSight")]
    static class TheTestActiveInSightPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.TheTestDriving; } }

    [HarmonyPatch(typeof(LookAtGuy_Active), "PlayerLost")]
    static class TheTestActiveLostPatch
    { static bool Prefix() { var r = ModHooks.Current; return r == null || !r.TheTestDriving; } }

    [HarmonyPatch(typeof(LookAtGuy_Respawning), "Update")]
    static class TheTestRespawnHoldPatch
    { static bool Prefix(LookAtGuy_Respawning __instance) { var r = ModHooks.Current; return r == null || !r.TheTestDriving || !r.TheTestRespawnHeld(__instance); } }

    [HarmonyPatch(typeof(LookAtGuy), "Blind")]
    static class TheTestBlindPatch
    { static void Postfix(LookAtGuy __instance) { var r = ModHooks.Current; if (r != null && r.TheTestDriving) r.OnTheTestBlind(__instance); } }

    [HarmonyPatch(typeof(LookAtGuy), "Respawn")]
    static class TheTestRespawnPatch
    { static void Postfix(LookAtGuy __instance) { var r = ModHooks.Current; if (r != null && r.TheTestDriving) r.OnTheTestRespawn(__instance); } }

    public sealed partial class TogetherRuntime
    {
        const int TheTestMaxReplicas = 8;
        const byte TheTestGaze = 1, TheTestBlindCmd = 1, TheTestUnblindCmd = 2;
        const byte TheTestStateActive = 0, TheTestStateRespawning = 1, TheTestStateBlinding = 2, TheTestStateDead = 3;
        const float TheTestClaimLifetime = .75f, TheTestClaimSlack = 3f, TheTestFacingLimit = 75f;

        static readonly FieldInfo testStateOwner = AccessTools.Field(typeof(LookAtGuy_BaseState), "theTest");
        static readonly FieldInfo testPressure = AccessTools.Field(typeof(LookAtGuy_Active), "pressure");
        static readonly FieldInfo testMaxPressure = AccessTools.Field(typeof(LookAtGuy_Active), "maxPressure");
        static readonly FieldInfo testPlayerSees = AccessTools.Field(typeof(LookAtGuy_Active), "playerSees");
        static readonly FieldInfo testSeesPlayer = AccessTools.Field(typeof(LookAtGuy_Active), "seesPlayer");
        static readonly FieldInfo testPlayerTagged = AccessTools.Field(typeof(LookAtGuy_Active), "playerTagged");
        static readonly FieldInfo testPlayerSaw = AccessTools.Field(typeof(LookAtGuy_Active), "playerSaw");
        static readonly FieldInfo testBlindTime = AccessTools.Field(typeof(LookAtGuy_Blinding), "time");
        static readonly FieldInfo testBlindTotal = AccessTools.Field(typeof(LookAtGuy_Blinding), "totalTime");
        static readonly FieldInfo testEcTimeScales = AccessTools.Field(typeof(EnvironmentController), "timeScaleModifiers");
        static readonly FieldInfo testRespawnDelay = AccessTools.Field(typeof(LookAtGuy_Respawning), "frameDelay");
        static readonly FieldInfo testRespawnPrevious = AccessTools.Field(typeof(LookAtGuy_Respawning), "previousState");
        static readonly FieldInfo testEcFogs = AccessTools.Field(typeof(EnvironmentController), "fogs");

        sealed class TheTestEntry { public int Index; public bool Los, Looking; public Vector3 At; }
        sealed class TheTestClaim { public int Seq; public float At; public List<TheTestEntry> Entries = new List<TheTestEntry>(); }
        sealed class TheTestRecord { public int Index, Token; public byte State, Flags; public float Pressure, Scale, Remaining, Total; }
        sealed class TheTestMirror { public byte State = 255; public bool Seen, Crumbled; public float Pressure; }

        // Host state.
        readonly Dictionary<int, TheTestClaim> testClaims = new Dictionary<int, TheTestClaim>();
        readonly Dictionary<int, int> testLastSeq = new Dictionary<int, int>();
        readonly Dictionary<LookAtGuy, int> testBlindTokens = new Dictionary<LookAtGuy, int>();
        int testNextBlindToken;
        Transform testProbe;

        // Client state.
        readonly TimeScaleModifier testMirrorScale = new TimeScaleModifier(1f, 1f, 1f);
        EnvironmentController testMirrorScaleEc, testFlickerEc;
        readonly HashSet<LookAtGuy> testMirrorFrozen = new HashSet<LookAtGuy>();
        readonly Dictionary<LookAtGuy, TheTestMirror> testMirrors = new Dictionary<LookAtGuy, TheTestMirror>();
        HudGauge testOwnerGauge; LookAtGuy testOwnerReplica; int testOwnerToken, testOwnerIndex = -1, testLastOwnerToken;
        float testOwnerTotal;
        int testSeq, testLastSentCount; float testNextReport, testNextEval;
        readonly int[] testLastKeys = new int[TheTestMaxReplicas];
        readonly List<TheTestEntry> testEntries = new List<TheTestEntry>(TheTestMaxReplicas);
        readonly TheTestEntry[] testEntryPool = Enumerable.Range(0, TheTestMaxReplicas).Select(i => new TheTestEntry()).ToArray();

        // Diagnostics.
        int testReports, testRejected, testRemoteHolds, testBlindsSent, testOwnerBlinds, testMirrorFrames;
        bool theTestLabStarted, theTestLabMirror, theTestLabIgnoreHost;

        public bool TheTestDriving { get { return Active && Hosting && Running; } }

        void ResetTheTest()
        {
            try { ReleaseTheTestHostHolds(); } catch (Exception ex) { Log("The Test host reset: " + ex.Message); }
            ResetTheTestMirror();
            testClaims.Clear(); testLastSeq.Clear(); testBlindTokens.Clear();
            testNextBlindToken = 0; testLastOwnerToken = 0;
            testSeq = 0; testLastSentCount = 0; testNextReport = testNextEval = 0;
        }

        // A freeze held only by remote gazes would never end once those reports stop; treat it exactly like
        // the watchers looking away (the native rule), so single-player continues from a native state.
        void ReleaseTheTestHostHolds()
        {
            if (testClaims.Count == 0 || npcs == null) return; // claims exist only on a host; Network may already be gone
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player == null) return;
            foreach (var test in npcs.OfType<LookAtGuy>())
            {
                if (test == null || !test.gameObject.activeInHierarchy) continue;
                var state = test.behaviorStateMachine.CurrentState as LookAtGuy_Active;
                if (state == null || !(bool)testPlayerSaw.GetValue(state)) continue;
                bool hostGaze = !theTestLabIgnoreHost && !LocalEliminated && test.looker.PlayerInSight(player) && test.looker.IsVisible && !test.Navigator.Entity.Hidden;
                if (hostGaze) continue;
                testPlayerSees.SetValue(state, false); testSeesPlayer.SetValue(state, false);
                test.FleePlayer(player);
            }
        }

        void ResetTheTestMirror()
        {
            if (testMirrorScaleEc != null) RemoveTheTestTimeScale(testMirrorScaleEc, testMirrorScale);
            testMirrorScaleEc = null; testMirrorScale.npcTimeScale = testMirrorScale.environmentTimeScale = 1f;
            foreach (var replica in testMirrorFrozen.ToArray())
            {
                if (replica != null && NpcField<bool>(replica, "freezing")) { bool was = Applying; Applying = true; try { replica.FreezeNPCs(false); } finally { Applying = was; } }
                else if (testFlickerEc != null) testFlickerEc.FlickerLights(false);
            }
            testMirrorFrozen.Clear(); testFlickerEc = null;
            EndTheTestOwnerBlind();
            foreach (var pair in testMirrors)
            {
                var replica = pair.Key; if (replica == null) continue;
                if (pair.Value.Crumbled) SetTheTestCrumbled(replica, false);
                if (replica.ec != null && replica.fog != null)
                {
                    var fogs = testEcFogs.GetValue(replica.ec) as List<Fog>;
                    if (fogs != null && fogs.Contains(replica.fog)) replica.ec.RemoveFog(replica.fog);
                }
            }
            testMirrors.Clear();
        }

        static void RemoveTheTestTimeScale(EnvironmentController ec, TimeScaleModifier modifier) { if (ec != null) ec.RemoveTimeScale(modifier); }

        static bool TheTestScaleListed(EnvironmentController ec, TimeScaleModifier modifier)
        {
            if (ec == null) return false;
            var list = testEcTimeScales.GetValue(ec) as List<TimeScaleModifier>;
            return list != null && list.Contains(modifier);
        }

        void UpdateTheTest()
        {
            if (!Running || !worldReady) return;
            if (Hosting) PruneTheTestClaims();
            else UpdateTheTestClient();
        }

        // ---------------------------------------------------------------- host

        void PruneTheTestClaims()
        {
            foreach (int id in testClaims.Keys.ToArray())
            {
                Peer peer;
                if (!peers.TryGetValue(id, out peer) || !peer.Accepted || !peer.Ready || peer.Eliminated) { testClaims.Remove(id); testLastSeq.Remove(id); }
            }
            foreach (int id in testLastSeq.Keys.ToArray()) if (!peers.ContainsKey(id)) testLastSeq.Remove(id);
        }

        bool TheTestLineOfSight(LookAtGuy test, Vector3 position)
        {
            var core = Singleton<CoreGameManager>.Instance;
            if (core == null || core.GetPlayer(0) == null || test.looker == null) return false;
            if (testProbe == null) { testProbe = new GameObject("The Test remote gaze target").transform; testProbe.SetParent(transform, false); }
            testProbe.gameObject.layer = (int)AccessTools.Field(typeof(Entity), "targetLayer").GetValue(core.GetPlayer(0).plm.Entity);
            testProbe.position = position;
            bool visible; test.looker.Raycast(testProbe, Vector3.Distance(test.transform.position, position), out visible); return visible;
        }

        static float TheTestYawToward(Vector3 from, Vector3 to) { Vector3 d = to - from; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }

        // Host-side validation of one peer's claim for one Test: freshness, eligibility, plausible distance,
        // claimed replica position, native Looker line of sight and a generous facing check on the pose yaw.
        void TheTestRemoteGaze(LookAtGuy test, int index, Peer peer, out bool los, out bool looking)
        {
            los = looking = false;
            TheTestClaim claim;
            if (peer == null || peer.Id == self || !testClaims.TryGetValue(peer.Id, out claim)) return;
            float now = Time.realtimeSinceStartup;
            if (!peer.Accepted || !peer.Ready || !peer.HasPose || peer.Eliminated || peer.InLocker || (peer.Invisible && !NpcField<bool>(test.looker, "ignorePlayerVisibility")) || now - peer.PoseAt > 2 || now - claim.At > TheTestClaimLifetime) return;
            TheTestEntry entry = null;
            foreach (var e in claim.Entries) if (e.Index == index) { entry = e; break; }
            if (entry == null || !entry.Los) return;
            Vector3 position = test.transform.position;
            if (Vector3.Distance(entry.At, position) > TheTestClaimSlack) return;
            float range = Mathf.Min(test.looker.distance, test.ec != null ? test.ec.MaxRaycast : 10000f);
            if (Vector3.Distance(peer.Position, position) > range) return;
            if (!TheTestLineOfSight(test, peer.Position)) return;
            los = true;
            if (!entry.Looking) return;
            Vector3 toward = position - peer.Position; toward.y = 0;
            Vector3 forward = Quaternion.Euler(0, peer.Yaw, 0) * Vector3.forward;
            looking = toward.sqrMagnitude < .01f || Vector3.Angle(forward, toward) <= TheTestFacingLimit;
        }

        bool AnyTheTestRemoteGaze(LookAtGuy test)
        {
            int index = Array.IndexOf(npcs, (NPC)test);
            if (index < 0) return false;
            foreach (var peer in peers.Values.ToArray())
            { bool los, looking; TheTestRemoteGaze(test, index, peer, out los, out looking); if (looking) return true; }
            return false;
        }

        // Replaces LookAtGuy_Active.PlayerInSight/PlayerLost/Update while hosting with the same rules
        // applied to the union of the host player and every validated remote watcher.
        public bool DriveTheTestActive(LookAtGuy_Active state)
        {
            var test = testStateOwner.GetValue(state) as LookAtGuy;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (test == null || player == null) return false;
            bool hidden = test.Navigator.Entity.Hidden;
            bool hostLos = !theTestLabIgnoreHost && !LocalEliminated && test.looker.PlayerInSight(player);
            bool anyLos = hostLos, anyGaze = hostLos && test.looker.IsVisible && !hidden, untagged = hostLos && !player.Tagged, remoteOnly = false;
            int index = Array.IndexOf(npcs, (NPC)test);
            if (index >= 0)
                foreach (var peer in peers.Values.ToArray())
                {
                    bool los, looking; TheTestRemoteGaze(test, index, peer, out los, out looking);
                    if (!los) continue;
                    anyLos = true; if (!peer.Tagged) untagged = true;
                    if (looking && !hidden) { if (!anyGaze) remoteOnly = true; anyGaze = true; }
                }
            if (remoteOnly) testRemoteHolds++;
            bool playerSaw = (bool)testPlayerSaw.GetValue(state), playerSees = (bool)testPlayerSees.GetValue(state);
            if (anyLos)
            {
                testSeesPlayer.SetValue(state, true); testPlayerTagged.SetValue(state, !untagged);
                if (anyGaze)
                {
                    if (!playerSees) test.FreezeNPCs(true);
                    testPlayerSees.SetValue(state, true); testPlayerSaw.SetValue(state, true);
                }
                else if (playerSees && playerSaw) { testPlayerSees.SetValue(state, false); test.FleePlayer(player); return true; }
            }
            else if (playerSaw) { test.FleePlayer(player); testSeesPlayer.SetValue(state, false); return true; }

            float pressure = (float)testPressure.GetValue(state), max = (float)testMaxPressure.GetValue(state);
            if (!(bool)testSeesPlayer.GetValue(state)) pressure = Mathf.Max(0f, pressure - Time.deltaTime * test.TimeScale * .2f);
            else if ((bool)testPlayerSees.GetValue(state))
            {
                pressure += Time.deltaTime;
                test.UpdateHeadPosition(pressure / max);
                if (pressure >= max && !(bool)testPlayerTagged.GetValue(state))
                {
                    if (test.ExplodeChance()) { testPressure.SetValue(state, pressure); test.Blind(); return true; }
                    pressure -= 1f;
                }
            }
            testPressure.SetValue(state, pressure);
            return true;
        }

        // Respawning reappears once the host camera stops seeing the new spot; remote watchers hold it too.
        public bool TheTestRespawnHeld(LookAtGuy_Respawning state)
        {
            var test = testStateOwner.GetValue(state) as LookAtGuy;
            if (test == null) return false;
            if (AnyTheTestRemoteGaze(test)) return true;
            if (!theTestLabIgnoreHost) return false;
            // Lab only: the native rule (one frame delay, then reappear when unseen) without the host camera.
            if (!(bool)testRespawnDelay.GetValue(state)) { testRespawnDelay.SetValue(state, true); return true; }
            test.Reappear(); test.behaviorStateMachine.ChangeState((NpcState)testRespawnPrevious.GetValue(state));
            return true;
        }

        public void OnTheTestBlind(LookAtGuy test)
        {
            int index = Array.IndexOf(npcs, (NPC)test);
            if (index < 0) return;
            int token = ++testNextBlindToken; testBlindTokens[test] = token;
            float total = NpcField<float>(test, "fogTime");
            byte[] packet = Wire.Packet(Op.TheTestToClient, w => { w.Write(epoch); w.Write(TheTestBlindCmd); w.Write(token); w.Write((ushort)index); w.Write(total); });
            foreach (var peer in peers.Values.ToArray())
                if (peer.Id != self && peer.Accepted && peer.Ready && !peer.Eliminated) { Network.Send(peer.Id, packet); testBlindsSent++; }
        }

        public void OnTheTestRespawn(LookAtGuy test)
        {
            int token; if (!testBlindTokens.TryGetValue(test, out token)) return;
            testBlindTokens.Remove(test);
            SendAll(Wire.Packet(Op.TheTestToClient, w => { w.Write(epoch); w.Write(TheTestUnblindCmd); w.Write(token); }));
        }

        bool HandleTheTestClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.TheTestToHost) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub != TheTestGaze) throw new IOException("Unknown The Test request.");
            int seq = r.ReadInt32(); int count = r.ReadByte();
            if (count > TheTestMaxReplicas) throw new IOException("Too many The Test gaze entries.");
            var claim = new TheTestClaim { Seq = seq, At = Time.realtimeSinceStartup };
            for (int i = 0; i < count; i++)
            {
                int index = r.ReadUInt16(); byte flags = r.ReadByte(); Vector3 at = ReadVector(r);
                if (flags > 3 || (flags & 2) != 0 && (flags & 1) == 0) throw new IOException("Invalid The Test gaze flags.");
                if (claim.Entries.Any(e => e.Index == index)) throw new IOException("Duplicate The Test gaze entry.");
                claim.Entries.Add(new TheTestEntry { Index = index, Los = (flags & 1) != 0, Looking = (flags & 2) != 0, At = at });
            }
            int last;
            if (!Hosting || generation != epoch || sender == null || !sender.Ready || sender.Eliminated || (testLastSeq.TryGetValue(id, out last) && seq <= last)) { testRejected++; return true; }
            testLastSeq[id] = seq;
            claim.Entries.RemoveAll(e => npcs == null || e.Index >= npcs.Length || !(npcs[e.Index] is LookAtGuy) || npcs[e.Index] == null);
            testClaims[id] = claim; testReports++;
            return true;
        }

        void WriteTheTestWorld(BinaryWriter w)
        {
            var records = new List<TheTestRecord>();
            if (Hosting && npcs != null)
                for (int i = 0; i < npcs.Length && records.Count < TheTestMaxReplicas; i++)
                {
                    var test = npcs[i] as LookAtGuy; if (test == null) continue;
                    var record = new TheTestRecord { Index = i, Scale = Mathf.Clamp(NpcField<float>(test, "currentTimeScale"), 0f, 10f) };
                    if (NpcField<bool>(test, "freezing")) record.Flags |= 1;
                    var current = test.behaviorStateMachine.CurrentState;
                    var active = current as LookAtGuy_Active; var blinding = current as LookAtGuy_Blinding;
                    if (active != null)
                    {
                        record.State = TheTestStateActive;
                        if ((bool)testPlayerSees.GetValue(active)) record.Flags |= 2;
                        record.Pressure = Mathf.Clamp01((float)testPressure.GetValue(active) / (float)testMaxPressure.GetValue(active));
                    }
                    else if (current is LookAtGuy_Respawning) record.State = TheTestStateRespawning;
                    else if (blinding != null)
                    {
                        record.State = TheTestStateBlinding;
                        record.Remaining = Mathf.Max(0f, (float)testBlindTime.GetValue(blinding)); record.Total = (float)testBlindTotal.GetValue(blinding);
                        testBlindTokens.TryGetValue(test, out record.Token);
                    }
                    else if (current is LookAtGuy_Dead) record.State = TheTestStateDead;
                    records.Add(record);
                }
            w.Write((byte)records.Count);
            foreach (var record in records)
            {
                w.Write((ushort)record.Index); w.Write(record.State); w.Write(record.Flags);
                w.Write(record.Pressure); w.Write(record.Scale); w.Write(record.Remaining); w.Write(record.Total); w.Write(record.Token);
            }
        }

        // ---------------------------------------------------------------- client

        void ReadTheTestWorld(BinaryReader r, bool apply)
        {
            int count = r.ReadByte();
            if (count > TheTestMaxReplicas) throw new IOException("Too many The Test records.");
            var records = new List<TheTestRecord>();
            for (int i = 0; i < count; i++)
            {
                var record = new TheTestRecord { Index = r.ReadUInt16(), State = r.ReadByte(), Flags = r.ReadByte() };
                record.Pressure = Wire.Number(r); record.Scale = Wire.Number(r); record.Remaining = Wire.Number(r); record.Total = Wire.Number(r); record.Token = r.ReadInt32();
                if (record.State > TheTestStateDead || record.Flags > 3 || record.Pressure < 0 || record.Pressure > 1 || record.Scale < 0 || record.Scale > 10 || record.Remaining < 0 || record.Total < 0 || record.Token < 0)
                    throw new IOException("Invalid The Test record.");
                records.Add(record);
            }
            if (apply && (!Hosting || theTestLabMirror)) ApplyTheTestMirror(records);
        }

        void ApplyTheTestMirror(List<TheTestRecord> records)
        {
            var manager = Singleton<BaseGameManager>.Instance; var ec = manager == null ? null : manager.Ec;
            if (ec == null || npcs == null) return;
            testMirrorFrames++;
            float scale = 1f; bool ownerSeen = false;
            Applying = true;
            try
            {
                foreach (var record in records)
                {
                    if (record.Index >= npcs.Length) continue;
                    var replica = npcs[record.Index] as LookAtGuy; if (replica == null) continue;
                    scale *= record.Scale;
                    TheTestMirror mirror;
                    if (!testMirrors.TryGetValue(replica, out mirror)) { mirror = new TheTestMirror(); testMirrors.Add(replica, mirror); }
                    bool freezing = (record.Flags & 1) != 0;
                    if (freezing != NpcField<bool>(replica, "freezing"))
                    {
                        replica.FreezeNPCs(freezing);
                        if (freezing) { testMirrorFrozen.Add(replica); testFlickerEc = ec; } else testMirrorFrozen.Remove(replica);
                    }
                    bool crumbled = record.State == TheTestStateBlinding || record.State == TheTestStateDead;
                    if (record.State == TheTestStateBlinding && mirror.State == TheTestStateActive) TheTestExplodeReplica(replica);
                    if (crumbled != mirror.Crumbled) { SetTheTestCrumbled(replica, crumbled); mirror.Crumbled = crumbled; }
                    bool seen = record.State == TheTestStateActive && (record.Flags & 2) != 0;
                    if (mirror.Seen && !seen) replica.UpdateHeadPosition(0f);
                    mirror.State = record.State; mirror.Seen = seen; mirror.Pressure = record.Pressure;
                    if (record.State == TheTestStateBlinding && record.Token > 0)
                    {
                        if (record.Token > testLastOwnerToken) StartTheTestOwnerBlind(record.Index, record.Token, record.Total);
                        if (testOwnerToken == record.Token) { ownerSeen = true; if (testOwnerGauge != null) testOwnerGauge.SetValue(Mathf.Max(.01f, testOwnerTotal), record.Remaining); }
                    }
                }
                if (testOwnerGauge != null && !ownerSeen) EndTheTestOwnerBlind();
            }
            finally { Applying = false; }
            testMirrorScale.npcTimeScale = testMirrorScale.environmentTimeScale = scale;
            bool needed = Mathf.Abs(scale - 1f) > .0001f;
            if (testMirrorScaleEc != null && (testMirrorScaleEc != ec || !needed)) { RemoveTheTestTimeScale(testMirrorScaleEc, testMirrorScale); testMirrorScaleEc = null; }
            if (needed && testMirrorScaleEc == null) { ec.AddTimeScale(testMirrorScale); testMirrorScaleEc = ec; }
        }

        void TheTestExplodeReplica(LookAtGuy replica)
        {
            var prefab = NpcField<QuickExplosion>(replica, "explosionPrefab"); var billboard = NpcField<Transform>(replica, "billboardedTransform");
            if (prefab != null && billboard != null) Object.Instantiate(prefab, billboard).transform.localPosition += Vector3.forward * .015f;
            var audio = NpcField<AudioManager>(replica, "audMan"); var explode = NpcField<SoundObject>(replica, "audExplode");
            if (audio != null && explode != null) audio.PlaySingle(explode);
        }

        static void SetTheTestCrumbled(LookAtGuy replica, bool crumbled)
        {
            var rotator = NpcField<AnimatedSpriteRotator>(replica, "spriteRotator"); var head = NpcField<Transform>(replica, "headTransform");
            if (rotator != null) rotator.enabled = !crumbled;
            if (head != null) head.gameObject.SetActive(!crumbled);
            if (crumbled) { var sprite = NpcField<SpriteRenderer>(replica, "sprite"); var crumbledSprite = NpcField<Sprite>(replica, "crumbledSprite"); if (sprite != null && crumbledSprite != null) sprite.sprite = crumbledSprite; }
        }

        // Owner effect: the native blindness gauge on this player's own HUD plus the blind audio loop.
        void StartTheTestOwnerBlind(int index, int token, float total)
        {
            if (token <= testLastOwnerToken || npcs == null || index < 0 || index >= npcs.Length) return;
            var replica = npcs[index] as LookAtGuy; var core = Singleton<CoreGameManager>.Instance;
            if (replica == null || core == null || core.GetHud(0) == null) return;
            testLastOwnerToken = token;
            if (LocalEliminated) return;
            EndTheTestOwnerBlind();
            bool was = Applying; Applying = true;
            try
            {
                testOwnerTotal = total > 0 ? total : NpcField<float>(replica, "fogTime");
                testOwnerGauge = core.GetHud(0).gaugeManager.ActivateNewGauge(NpcField<Sprite>(replica, "gaugeSprite"), testOwnerTotal);
                var blind = NpcField<AudioManager>(replica, "blindAudMan");
                if (blind != null)
                {
                    blind.FlushQueue(true);
                    blind.QueueAudio(NpcField<SoundObject>(replica, "audBlindStart")); blind.QueueAudio(NpcField<SoundObject>(replica, "audBlindLoop")); blind.SetLoop(true);
                }
            }
            finally { Applying = was; }
            testOwnerReplica = replica; testOwnerToken = token; testOwnerIndex = index; testOwnerBlinds++;
        }

        void EndTheTestOwnerBlind()
        {
            if (testOwnerGauge != null) { testOwnerGauge.SetValue(Mathf.Max(.01f, testOwnerTotal), 0f); testOwnerGauge.Deactivate(); }
            if (testOwnerReplica != null) { var blind = NpcField<AudioManager>(testOwnerReplica, "blindAudMan"); if (blind != null) blind.FlushQueue(true); }
            testOwnerGauge = null; testOwnerReplica = null; testOwnerToken = 0; testOwnerIndex = -1;
        }

        bool HandleTheTestHost(Op op, BinaryReader r)
        {
            if (op != Op.TheTestToClient) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub == TheTestBlindCmd)
            {
                int token = r.ReadInt32(); int index = r.ReadUInt16(); float total = Wire.Number(r);
                if (token <= 0 || total < 0) throw new IOException("Invalid The Test blind.");
                if (generation == epoch && worldReady) StartTheTestOwnerBlind(index, token, total);
            }
            else if (sub == TheTestUnblindCmd)
            {
                int token = r.ReadInt32();
                if (generation == epoch && token > 0 && token == testOwnerToken) EndTheTestOwnerBlind();
            }
            else throw new IOException("Unknown The Test command.");
            return true;
        }

        // Native Looker.Update visibility rule, evaluated locally against the replica for the client camera.
        bool TheTestLocalGaze(LookAtGuy replica, PlayerManager player, out bool los)
        {
            los = false;
            if (replica == null || replica.looker == null || replica.ec == null || player == null) return false;
            float range = Mathf.Min((replica.transform.position - player.transform.position).magnitude, Mathf.Min(replica.looker.distance, replica.ec.MaxRaycast));
            // Same overload as Looker.Update, so the looker's own ignorePlayerVisibility rule applies to hidden players.
            replica.looker.Raycast(player.transform, range, player, NpcField<LayerMask>(replica.looker, "layerMask"), out los);
            if (!los) return false;
            var camera = Singleton<CoreGameManager>.Instance.GetCamera(0); var global = Singleton<GlobalCam>.Instance; var files = Singleton<PlayerFileManager>.Instance;
            if (camera == null || global == null || global.Cam == null || files == null || files.resolutionY == 0) return false;
            float angle = Vector3.Angle(replica.transform.position - player.transform.position, camera.transform.forward);
            float fov = Camera.VerticalToHorizontalFieldOfView(global.Cam.fieldOfView, (float)files.resolutionX / files.resolutionY);
            return angle <= fov / 2f + NpcField<float>(replica.looker, "visibilityBuffer");
        }

        void UpdateTheTestClient()
        {
            foreach (var pair in testMirrors) if (pair.Key != null && pair.Value.Seen) pair.Key.UpdateHeadPosition(pair.Value.Pressure);
            if (!readyAcknowledged || npcs == null) return;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player == null) return;
            // Gaze is sampled at most every 50 ms (raycasts per replica); a report goes out on change or every 0.2 s.
            float now = Time.realtimeSinceStartup;
            if (now < testNextEval) return;
            testNextEval = now + .05f;
            var entries = testEntries; entries.Clear();
            bool changed = false;
            if (!LocalEliminated)
                for (int i = 0; i < npcs.Length && entries.Count < TheTestMaxReplicas; i++)
                {
                    var replica = npcs[i] as LookAtGuy; if (replica == null) continue;
                    bool los; bool looking = TheTestLocalGaze(replica, player, out los);
                    var entry = testEntryPool[entries.Count];
                    entry.Index = i; entry.Los = los; entry.Looking = looking; entry.At = replica.transform.position;
                    int key = (i << 2) | (los ? 1 : 0) | (looking ? 2 : 0);
                    if (entries.Count >= testLastSentCount || testLastKeys[entries.Count] != key) changed = true;
                    testLastKeys[entries.Count] = key;
                    entries.Add(entry);
                }
            if (entries.Count != testLastSentCount) changed = true;
            if (entries.Count == 0 && testLastSentCount == 0) return;
            if (!changed && now < testNextReport) return;
            testLastSentCount = entries.Count; testNextReport = now + .2f;
            Network.Send(0, TheTestGazePacket(epoch, ++testSeq, entries));
        }

        static byte[] TheTestGazePacket(int generation, int seq, List<TheTestEntry> entries)
        {
            return Wire.Packet(Op.TheTestToHost, w =>
            {
                w.Write(generation); w.Write(TheTestGaze); w.Write(seq); w.Write((byte)entries.Count);
                foreach (var e in entries) { w.Write((ushort)e.Index); w.Write((byte)((e.Los ? 1 : 0) | (e.Looking ? 2 : 0))); WriteVector(w, e.At); }
            });
        }

        string TheTestDiagnostic()
        {
            return ",\"theTestReports\":" + testReports + ",\"theTestRejected\":" + testRejected + ",\"theTestRemoteHolds\":" + testRemoteHolds
                + ",\"theTestBlindsSent\":" + testBlindsSent + ",\"theTestOwnerBlinds\":" + testOwnerBlinds + ",\"theTestMirrorFrames\":" + testMirrorFrames
                + ",\"theTestMirrorScale\":" + testMirrorScale.npcTimeScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- lab

        void UpdateTheTestLab()
        {
            if (theTestLabStarted || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-thetest-tour")) return;
            theTestLabStarted = true; StartCoroutine(TourTheTest());
        }

        bool FeedTheTestHost(int id, byte[] packet)
        {
            using (var memory = new MemoryStream(packet))
            using (var reader = new BinaryReader(memory))
            { reader.ReadByte(); bool handled = HandleTheTestClient(id, peers[id], Op.TheTestToHost, reader); return handled && memory.Position == memory.Length; }
        }

        bool FeedTheTestClient(byte[] packet)
        {
            using (var memory = new MemoryStream(packet))
            using (var reader = new BinaryReader(memory))
            { reader.ReadByte(); bool handled = HandleTheTestHost(Op.TheTestToClient, reader); return handled && memory.Position == memory.Length; }
        }

        byte[] TheTestLabGaze(int generation, int seq, int index, LookAtGuy test, bool los, bool looking)
        {
            return TheTestGazePacket(generation, seq, new List<TheTestEntry> { new TheTestEntry { Index = index, Los = los, Looking = looking, At = test.transform.position } });
        }

        bool TheTestLabMirrorRoundTrip()
        {
            byte[] data;
            using (var memory = new MemoryStream())
            { using (var writer = new BinaryWriter(memory)) WriteTheTestWorld(writer); data = memory.ToArray(); }
            bool parsed, applied;
            using (var memory = new MemoryStream(data)) using (var reader = new BinaryReader(memory)) { ReadTheTestWorld(reader, false); parsed = memory.Position == memory.Length; }
            theTestLabMirror = true;
            try { using (var memory = new MemoryStream(data)) using (var reader = new BinaryReader(memory)) { ReadTheTestWorld(reader, true); applied = memory.Position == memory.Length; } }
            finally { theTestLabMirror = false; }
            return parsed && applied && data.Length > 1;
        }

        string TheTestLabState(LookAtGuy test, Peer remote, EnvironmentController ec)
        {
            var current = test.behaviorStateMachine.CurrentState; var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            string detail = "";
            var active = current as LookAtGuy_Active; var respawning = current as LookAtGuy_Respawning;
            if (active != null) detail = " playerSees=" + testPlayerSees.GetValue(active) + " playerSaw=" + testPlayerSaw.GetValue(active) + " seesPlayer=" + testSeesPlayer.GetValue(active) + " pressure=" + testPressure.GetValue(active);
            if (respawning != null) detail = " frameDelay=" + testRespawnDelay.GetValue(respawning);
            int index = Array.IndexOf(npcs, (NPC)test); bool los = false, looking = false;
            if (remote != null) TheTestRemoteGaze(test, index, remote, out los, out looking);
            var scales = testEcTimeScales.GetValue(ec) as List<TimeScaleModifier>;
            return "state=" + (current == null ? "null" : current.GetType().Name) + detail + " freezing=" + NpcField<bool>(test, "freezing")
                + " visible=" + test.Navigator.Entity.Visible + " hidden=" + test.Navigator.Entity.Hidden + " index=" + index
                + " claims=" + testClaims.Count + " remotePeer=" + (remote != null && peers.ContainsKey(remote.Id)) + " remoteLos=" + los + " remoteLooking=" + looking
                + " hostSighted=" + test.looker.PlayerInSight(player) + " lookerVisible=" + test.looker.IsVisible + " ignoreVisibility=" + NpcField<bool>(test.looker, "ignorePlayerVisibility")
                + " test=" + test.transform.position + " remote=" + (remote == null ? "-" : remote.Position.ToString()) + " host=" + player.transform.position
                + " scaleCount=" + (scales == null ? -1 : scales.Count) + " npcScale=" + ec.NpcTimeScale + " currentScale=" + NpcField<float>(test, "currentTimeScale")
                + " timeScale=" + Time.timeScale + " dt=" + Time.deltaTime;
        }

        // Places the synthetic remote watcher where host validation will accept it: first spots inside the Test's
        // own cell, then spots in neighbouring cells reached through an open side. Each candidate is checked with
        // the same range and TheTestLineOfSight test that TheTestRemoteGaze uses; the yaw faces the Test.
        bool PlaceTheTestLabWatcher(LookAtGuy test, Peer remote, float y, EnvironmentController ec, string step)
        {
            Physics.SyncTransforms();
            Vector3 target = test.transform.position;
            var cell = ec.CellFromPosition(target);
            var candidates = new List<Vector3>();
            var dirs = Directions.All();
            foreach (float d in new[] { 3f, 2f, 3.5f, 1.5f })
            {
                foreach (var dir in dirs) candidates.Add(target + dir.ToVector3() * d);
                candidates.Add(target + new Vector3(d, 0, d) * .7071f); candidates.Add(target + new Vector3(-d, 0, d) * .7071f);
                candidates.Add(target + new Vector3(d, 0, -d) * .7071f); candidates.Add(target + new Vector3(-d, 0, -d) * .7071f);
            }
            int ownCount = candidates.Count;
            if (cell != null && !cell.Null)
                foreach (var dir in dirs)
                {
                    if (!cell.ConstNavigable(dir)) continue;
                    var next = ec.CellFromPosition(cell.position + dir.ToIntVector2());
                    if (next == null || next.Null || next == cell) continue;
                    candidates.Add(target + dir.ToVector3() * 6f); candidates.Add(target + dir.ToVector3() * 10f);
                }
            float range = Mathf.Min(test.looker.distance, test.ec != null ? test.ec.MaxRaycast : 10000f);
            for (int i = 0; i < candidates.Count; i++)
            {
                Vector3 at = candidates[i]; at.y = y;
                if (i < ownCount && cell != null && ec.CellFromPosition(at) != cell) continue;
                if (Vector3.Distance(at, target) > range || !TheTestLineOfSight(test, at)) continue;
                remote.Position = at; remote.Yaw = TheTestYawToward(at, target); remote.PoseAt = Time.realtimeSinceStartup;
                Log("LAB thetest watcher " + step + ": test=" + target + " cell=" + (cell == null ? "-" : cell.position.x + "," + cell.position.z) + " watcher=" + at + " yaw=" + remote.Yaw.ToString("0.#") + " candidate=" + i + "/" + candidates.Count + (i < ownCount ? " own-cell" : " neighbour") + " los=True");
                return true;
            }
            Vector3 fallback = target + Vector3.right * 3; fallback.y = y;
            remote.Position = fallback; remote.Yaw = TheTestYawToward(fallback, target); remote.PoseAt = Time.realtimeSinceStartup;
            Log("LAB thetest watcher " + step + ": test=" + target + " no candidate of " + candidates.Count + " had line of sight; fallback=" + fallback + " los=" + TheTestLineOfSight(test, fallback));
            return false;
        }

        IEnumerator TourTheTest()
        {
            nativeUiChecks.Clear();
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start(); portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); AutoStartRun();
            while (!worldReady) yield return null;
            yield return new WaitForSecondsRealtime(1);
            var core = Singleton<CoreGameManager>.Instance; var ec = Singleton<BaseGameManager>.Instance.Ec; var player = core.GetPlayer(0);
            var prefab = Resources.FindObjectsOfTypeAll<LookAtGuy>().First(n => !n.gameObject.scene.IsValid());
            var cell = ec.CellFromPosition(player.transform.position);
            var test = (LookAtGuy)ec.SpawnNPC(prefab, cell.position);
            Vector3 origin = cell.FloorWorldPosition; origin.y = player.transform.position.y;
            test.Navigator.Entity.Teleport(origin);
            npcs = ec.Npcs.ToArray(); int index = Array.IndexOf(npcs, (NPC)test);
            // The host camera must not count as a watcher. Hiding the host is not enough: the Test's looker may
            // ignore player visibility, and the host stands in the Test's own cell. Exclude the host explicitly.
            theTestLabIgnoreHost = true;
            var remote = new Peer { Id = 3, Name = "Test watcher", Accepted = true, Ready = true, HasPose = true, PoseAt = Time.realtimeSinceStartup };
            PlaceTheTestLabWatcher(test, remote, origin.y, ec, "initial"); peers[3] = remote;
            int seq = 1;
            FeedTheTestHost(3, TheTestLabGaze(epoch, seq, index, test, true, true));
            bool los, looking; TheTestRemoteGaze(test, index, remote, out los, out looking);
            CheckNativeUi("remote-gaze-validated", los && looking);
            // Frames can be well under a millisecond apart: wait on the condition with a real-time timeout.
            LookAtGuy_Active active = null;
            float deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline)
            {
                FeedTheTestHost(3, TheTestLabGaze(epoch, ++seq, index, test, true, true)); remote.PoseAt = Time.realtimeSinceStartup;
                yield return null;
                active = test.behaviorStateMachine.CurrentState as LookAtGuy_Active;
                if (active != null && NpcField<bool>(test, "freezing") && (float)testPressure.GetValue(active) > 0 && testRemoteHolds > 0) break;
            }
            active = test.behaviorStateMachine.CurrentState as LookAtGuy_Active;
            CheckNativeUi("remote-gaze-freezes-npcs", active != null && NpcField<bool>(test, "freezing") && (float)testPressure.GetValue(active) > 0 && testRemoteHolds > 0);

            int rejected = testRejected;
            bool replayHandled = FeedTheTestHost(3, TheTestLabGaze(epoch, seq, index, test, false, false));
            TheTestRemoteGaze(test, index, remote, out los, out looking);
            CheckNativeUi("replayed-report-ignored", replayHandled && testRejected == rejected + 1 && looking);
            FeedTheTestHost(3, TheTestLabGaze(epoch + 1, seq + 5, index, test, false, false));
            TheTestRemoteGaze(test, index, remote, out los, out looking);
            CheckNativeUi("stale-epoch-ignored", testRejected == rejected + 2 && looking);

            var other = new Peer { Id = 4, Name = "Invalid watcher", Accepted = true, Ready = true, HasPose = true, PoseAt = Time.realtimeSinceStartup, Position = remote.Position, Yaw = remote.Yaw };
            peers[4] = other; bool invalid = true;
            other.Eliminated = true; FeedTheTestHost(4, TheTestLabGaze(epoch, 1, index, test, true, true)); invalid &= !testClaims.ContainsKey(4);
            other.Eliminated = false; FeedTheTestHost(4, TheTestLabGaze(epoch, 2, index, test, true, true));
            other.InLocker = true; TheTestRemoteGaze(test, index, other, out los, out looking); invalid &= !looking;
            other.InLocker = false; other.Position = origin + Vector3.right * 400; TheTestRemoteGaze(test, index, other, out los, out looking); invalid &= !los && !looking;
            other.Position = remote.Position; other.Yaw = remote.Yaw + 180; TheTestRemoteGaze(test, index, other, out los, out looking); invalid &= los && !looking;
            peers.Remove(4); PruneTheTestClaims(); invalid &= !testClaims.ContainsKey(4);
            CheckNativeUi("invalid-watchers-rejected", invalid);

            bool round = TheTestLabMirrorRoundTrip();
            float hostScale = NpcField<float>(test, "currentTimeScale");
            bool mirrored = Mathf.Abs(testMirrorScale.npcTimeScale - hostScale) < .001f && (Mathf.Abs(hostScale - 1) < .0001f || TheTestScaleListed(ec, testMirrorScale));
            ResetTheTestMirror();
            CheckNativeUi("world-section-roundtrip", round && mirrored && !TheTestScaleListed(ec, testMirrorScale) && NpcField<bool>(test, "freezing"));

            remote.PoseAt = Time.realtimeSinceStartup;
            Vector3 beforeFlee = test.transform.position;
            // Poll every frame: the flee happens in the next NPC update and Respawning may reappear two frames later.
            deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline && !(test.behaviorStateMachine.CurrentState is LookAtGuy_Respawning))
            { FeedTheTestHost(3, TheTestLabGaze(epoch, ++seq, index, test, true, false)); remote.PoseAt = Time.realtimeSinceStartup; yield return null; }
            Log("LAB thetest look-away-teleports: " + TheTestLabState(test, remote, ec) + " before=" + beforeFlee);
            CheckNativeUi("look-away-teleports", test.behaviorStateMachine.CurrentState is LookAtGuy_Respawning && !test.Navigator.Entity.Visible && !NpcField<bool>(test, "freezing"));

            // Respawning teleported the Test to a random hall cell; place the watcher where it really sees that spot.
            // The Test must stay Respawning for the whole real-time hold while the validated gaze continues.
            PlaceTheTestLabWatcher(test, remote, origin.y, ec, "after-respawn");
            bool held = test.behaviorStateMachine.CurrentState is LookAtGuy_Respawning;
            deadline = Time.realtimeSinceStartup + .5f;
            while (held && Time.realtimeSinceStartup < deadline)
            {
                FeedTheTestHost(3, TheTestLabGaze(epoch, ++seq, index, test, true, true)); remote.PoseAt = Time.realtimeSinceStartup;
                yield return null;
                held = test.behaviorStateMachine.CurrentState is LookAtGuy_Respawning;
            }
            Log("LAB thetest respawn-waits-for-remote-gaze: " + TheTestLabState(test, remote, ec));
            CheckNativeUi("respawn-waits-for-remote-gaze", held);
            FeedTheTestHost(3, TheTestLabGaze(epoch, ++seq, index, test, false, false));
            deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline && !(test.behaviorStateMachine.CurrentState is LookAtGuy_Active)) yield return null;
            CheckNativeUi("reappears-when-unwatched", test.behaviorStateMachine.CurrentState is LookAtGuy_Active && test.Navigator.Entity.Visible);

            var gauges = NpcField<List<HudGauge>>(core.GetHud(0).gaugeManager, "gauges");
            int gaugeCount = gauges.Count, ownerBlinds = testOwnerBlinds;
            byte[] blind = Wire.Packet(Op.TheTestToClient, w => { w.Write(epoch); w.Write(TheTestBlindCmd); w.Write(41); w.Write((ushort)index); w.Write(120f); });
            bool blindHandled = FeedTheTestClient(blind);
            CheckNativeUi("owner-blind-applied-once", blindHandled && gauges.Count == gaugeCount + 1 && testOwnerGauge != null && testOwnerGauge.Active && testOwnerBlinds == ownerBlinds + 1);
            FeedTheTestClient(blind);
            FeedTheTestClient(Wire.Packet(Op.TheTestToClient, w => { w.Write(epoch + 1); w.Write(TheTestBlindCmd); w.Write(42); w.Write((ushort)index); w.Write(120f); }));
            CheckNativeUi("duplicate-blind-ignored", gauges.Count == gaugeCount + 1 && testOwnerBlinds == ownerBlinds + 1 && testOwnerToken == 41);
            var ownerGauge = testOwnerGauge;
            FeedTheTestClient(Wire.Packet(Op.TheTestToClient, w => { w.Write(epoch); w.Write(TheTestUnblindCmd); w.Write(41); }));
            CheckNativeUi("owner-unblind-releases", testOwnerGauge == null && ownerGauge != null && !ownerGauge.Active);

            PlaceTheTestLabWatcher(test, remote, origin.y, ec, "after-reappear");
            deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline && !NpcField<bool>(test, "freezing")) { FeedTheTestHost(3, TheTestLabGaze(epoch, ++seq, index, test, true, true)); remote.PoseAt = Time.realtimeSinceStartup; yield return null; }
            bool frozeAgain = NpcField<bool>(test, "freezing");
            string beforeLeave = TheTestLabState(test, remote, ec);
            peers.Remove(3);
            deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline && NpcField<bool>(test, "freezing")) yield return null;
            Log("LAB thetest disconnect-releases-freeze: before={" + beforeLeave + "} after={" + TheTestLabState(test, remote, ec) + "}");
            CheckNativeUi("disconnect-releases-freeze", frozeAgain && !NpcField<bool>(test, "freezing") && !testClaims.ContainsKey(3) && !(test.behaviorStateMachine.CurrentState is LookAtGuy_Active && (bool)testPlayerSees.GetValue(test.behaviorStateMachine.CurrentState)));

            for (int i = 0; i < 6; i++) yield return null;
            TheTestLabMirrorRoundTrip();
            FeedTheTestClient(Wire.Packet(Op.TheTestToClient, w => { w.Write(epoch); w.Write(TheTestBlindCmd); w.Write(43); w.Write((ushort)index); w.Write(120f); }));
            var resetGauge = testOwnerGauge;
            ResetTheTest();
            CheckNativeUi("reset-restores-native", !TheTestScaleListed(ec, testMirrorScale) && testMirrorFrozen.Count == 0 && testOwnerGauge == null && resetGauge != null && !resetGauge.Active && testClaims.Count == 0 && testMirrors.Count == 0);

            theTestLabIgnoreHost = false;
            Disconnect("The Test tour complete.", false);
            CheckNativeUi("single-player-native", !TheTestDriving && !ModHooks.ClientWorld && testClaims.Count == 0 && testMirrorScaleEc == null);
            WriteLabResults("thetest-results.json", "TheTest");
            Applying = true;
            try { core.ReturnToMenu(); } catch (Exception ex) { Log("The Test tour return: " + ex.Message); } finally { Applying = false; }
        }
    }
}
