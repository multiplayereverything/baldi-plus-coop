using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Host-authoritative Hall Monitor rules (see HallMonitor.cs for the NPC body).
    // Host: scans 10 times a second for running players in his sight (the host's own player from its native
    // PlayerManager guilt, remote players from the Principal rule reports they already send), marks them, and
    // blows the whistle when two marked players are within WhistleRange of each other.
    // Clients: the replica's sprite arrives through NPC presentation; this section adds the marks, the voice and
    // whistle cues on the replica, the local notice and a native HUD gauge counting down the player's own mark.
    public sealed partial class TogetherRuntime
    {
        readonly Dictionary<int, float> hallMarks = new Dictionary<int, float>();   // player id -> seconds left (NPC time)
        readonly Dictionary<int, float> hallSight = new Dictionary<int, float>();   // player id -> seconds seen running
        float hallNextScan, hallCooldown, hallFocusLeft; // hallFocusLeft: Baldi hunts the whistled pair (HidingSync.NearestBaldiPeer)
        int hallMarkSeq, hallLastMarked = -1, hallWhistleSeq, hallWhistleA = -1, hallWhistleB = -1;
        Vector3 hallWhistleAt;
        int hallMarksMade, hallWhistles, hallClientCues;
        // Client view of the host's sequence numbers; primed on the first snapshot so joining never replays old cues.
        bool hallClientPrimed; int hallSeenMarkSeq, hallSeenWhistleSeq; float hallLocalLeft;
        HudGauge hallGauge; Sprite hallGaugeIcon;
        const int MaxHallMarks = 16;

        void ResetHallMonitor()
        {
            hallMarks.Clear(); hallSight.Clear();
            hallNextScan = hallCooldown = hallFocusLeft = 0; hallMarkSeq = hallWhistleSeq = 0; hallLastMarked = hallWhistleA = hallWhistleB = -1;
            hallClientPrimed = false; hallSeenMarkSeq = hallSeenWhistleSeq = 0; hallLocalLeft = 0;
            if (hallGauge != null) { hallGauge.Deactivate(); hallGauge = null; }
        }

        HallMonitor CurrentHallMonitor()
        {
            for (int i = 0; i < npcs.Length; i++) { var monitor = npcs[i] as HallMonitor; if (monitor != null) return monitor; }
            return null;
        }

        void UpdateHallMonitor()
        {
            if (!Active || !worldReady) return;
            var manager = Singleton<BaseGameManager>.Instance;
            if (manager == null || manager.Ec == null || !manager.Ec.Active) return;
            var monitor = CurrentHallMonitor();
            if (Hosting && !hallLabManual) HostHallMonitor(monitor, Time.deltaTime * (monitor != null ? monitor.TimeScale : 1f), Time.realtimeSinceStartup >= hallNextScan);
            UpdateHallGauge();
        }

        // One host step. dt is NPC-scaled time; scan says whether to run the 10 Hz sight check this frame.
        void HostHallMonitor(HallMonitor monitor, float dt, bool scan)
        {
            hallCooldown = Mathf.Max(0, hallCooldown - dt); hallFocusLeft = Mathf.Max(0, hallFocusLeft - dt);
            if (hallMarks.Count > 0)
                foreach (int id in hallMarks.Keys.ToArray())
                {
                    Peer peer;
                    float left = hallMarks[id] - dt;
                    if (left <= 0 || !peers.TryGetValue(id, out peer) || peer.Eliminated) hallMarks.Remove(id); else hallMarks[id] = left;
                }
            float mine; hallLocalLeft = hallMarks.TryGetValue(self, out mine) ? mine : 0;
            if (monitor == null) return;
            if (!scan) return;
            // Sight and the whistle pair check run 10 times a second, not every frame.
            hallNextScan = Time.realtimeSinceStartup + 0.1f;
            ScanHallRunners(monitor, 0.1f * monitor.TimeScale);
            CheckHallWhistle(monitor);
        }

        // Players he can currently judge: alive, not hidden in a locker, not tagged or invisible, with a fresh pose.
        IEnumerable<KeyValuePair<int, Vector3>> HallPlayers(bool requireRunning)
        {
            var core = Singleton<CoreGameManager>.Instance; float now = Time.realtimeSinceStartup;
            var player = core == null ? null : core.GetPlayer(0);
            if (player != null && !LocalEliminated && !(localLocker != null && localLocker.playerInside) && !player.Tagged && !player.Invisible
                && (!requireRunning || (player.Disobeying && player.ruleBreak == "Running")))
                yield return new KeyValuePair<int, Vector3>(self, player.transform.position);
            foreach (var peer in peers.Values)
            {
                if (peer.Id == self) continue;
                RemoteRule rule;
                if (!peer.Accepted || !peer.Ready || !peer.HasPose || peer.Eliminated || peer.InLocker || peer.Tagged || peer.Invisible || now - peer.PoseAt > 1.5f) continue;
                if (requireRunning && (!principalRules.TryGetValue(peer.Id, out rule) || !ValidPrincipalPeer(peer, rule, now) || rule.Rule != "Running")) continue;
                yield return new KeyValuePair<int, Vector3>(peer.Id, peer.Position);
            }
        }

        // The native Looker's range and field of view, against the world geometry.
        bool HallMonitorSees(HallMonitor monitor, Vector3 position)
        {
            if (monitor.Blinded || monitor.looker == null) return false;
            var delta = position - monitor.transform.position;
            if (delta.magnitude > Mathf.Min(monitor.looker.distance, monitor.ec.MaxRaycast)) return false;
            bool fov = (bool)AccessTools.Field(typeof(Looker), "hasFov").GetValue(monitor.looker);
            float angle = (float)AccessTools.Field(typeof(Looker), "fieldOfView").GetValue(monitor.looker);
            return (!fov || Vector3.Angle(monitor.transform.forward, delta) <= angle) && ClearWorldLine(monitor.transform.position + Vector3.up, position);
        }

        void ScanHallRunners(HallMonitor monitor, float step)
        {
            var seen = new HashSet<int>();
            if (monitor.Pose == HallMonitor.Look.Patrol)
                foreach (var entry in HallPlayers(true))
                {
                    if (!HallMonitorSees(monitor, entry.Value)) continue;
                    seen.Add(entry.Key);
                    float time; hallSight.TryGetValue(entry.Key, out time); time += step; hallSight[entry.Key] = time;
                    if (hallMarks.ContainsKey(entry.Key)) { hallMarks[entry.Key] = HallMonitor.MarkSeconds; continue; } // caught again: the mark is refreshed quietly
                    if (time >= HallMonitor.SightSeconds - 0.01f) { MarkHallRunner(monitor, entry.Key); break; }
                }
            foreach (int id in hallSight.Keys.ToArray()) if (!seen.Contains(id)) hallSight.Remove(id);
        }

        void MarkHallRunner(HallMonitor monitor, int id)
        {
            hallMarks[id] = HallMonitor.MarkSeconds; hallSight.Remove(id);
            hallMarkSeq++; hallLastMarked = id; hallMarksMade++;
            monitor.WriteUp();
            if (id == self) HallMarkedNotice();
            Log("Hall Monitor marked player " + id + " for running.");
        }

        void CheckHallWhistle(HallMonitor monitor)
        {
            if (hallCooldown > 0 || hallMarks.Count < 2) return;
            var marked = HallPlayers(false).Where(p => hallMarks.ContainsKey(p.Key)).ToList();
            for (int i = 0; i < marked.Count; i++)
                for (int j = i + 1; j < marked.Count; j++)
                {
                    if (Vector3.Distance(marked[i].Value, marked[j].Value) > HallMonitor.WhistleRange) continue;
                    BlowHallWhistle(monitor, marked[i].Key, marked[j].Key, (marked[i].Value + marked[j].Value) * 0.5f);
                    return;
                }
        }

        void BlowHallWhistle(HallMonitor monitor, int a, int b, Vector3 at)
        {
            hallMarks.Remove(a); hallMarks.Remove(b);
            hallWhistleSeq++; hallWhistleA = a; hallWhistleB = b; hallWhistleAt = at; hallWhistles++;
            hallCooldown = HallMonitor.WhistleCooldown; hallFocusLeft = HallMonitor.FocusSeconds;
            monitor.BlowWhistle();
            // While Baldi hunts the nearest player he ignores noises (HidingSync), so the whistle points his hunt at
            // this pair for FocusSeconds. The native noise still reaches him when he has no target, and other NPCs.
            monitor.ec.MakeNoise(null, at, HallMonitor.WhistleNoise, false);
            foreach (var chase in chaseTargets.Values) chase.Next = 0;
            if (a == self || b == self) HallWhistleNotice();
            Log("Hall Monitor whistled on players " + a + " and " + b + ".");
        }

        bool HallWhistleFocused(int id) { return hallFocusLeft > 0 && (id == hallWhistleA || id == hallWhistleB); }

        void HallMarkedNotice() { Notify("The Hall Monitor wrote you up for running! Stay away from other marked players."); }

        void HallWhistleNotice()
        {
            Notify("WHISTLE! The Hall Monitor told Baldi where you are!");
            var music = Singleton<MusicManager>.Instance;
            if (music != null && HallMonitorAssets.WhistleSound != null) music.PlaySoundEffect(HallMonitorAssets.WhistleSound);
        }

        // Native HUD gauge (the one detention uses) counting down the local player's own mark.
        void UpdateHallGauge()
        {
            var core = Singleton<CoreGameManager>.Instance;
            if (hallLocalLeft <= 0 || core == null || core.GetHud(0) == null)
            { if (hallGauge != null) { hallGauge.Deactivate(); hallGauge = null; } return; }
            if (!Hosting) hallLocalLeft = Mathf.Max(0, hallLocalLeft - Time.deltaTime);
            if (hallGauge == null)
            {
                if (hallGaugeIcon == null && HallMonitorAssets.Load())
                    hallGaugeIcon = Sprite.Create(HallMonitorAssets.Patrol, new Rect(56, 300, 144, 144), new Vector2(0.5f, 0.5f), 100);
                if (hallGaugeIcon == null) return;
                hallGauge = core.GetHud(0).gaugeManager.ActivateNewGauge(hallGaugeIcon, HallMonitor.MarkSeconds);
            }
            hallGauge.SetValue(HallMonitor.MarkSeconds, hallLocalLeft);
        }

        void WriteHallMonitorWorld(BinaryWriter w)
        {
            var monitor = CurrentHallMonitor();
            w.Write(monitor == null ? -1 : Array.IndexOf(npcs, (NPC)monitor));
            w.Write(hallMarkSeq); w.Write(hallLastMarked);
            w.Write(hallWhistleSeq); w.Write(hallWhistleA); w.Write(hallWhistleB); WriteVector(w, hallWhistleAt);
            var marks = hallMarks.Take(MaxHallMarks).ToArray();
            w.Write((byte)marks.Length);
            foreach (var mark in marks) { w.Write(mark.Key); w.Write(mark.Value); }
        }

        void ReadHallMonitorWorld(BinaryReader r, bool apply)
        {
            int index = r.ReadInt32();
            int markSeq = r.ReadInt32(), lastMarked = r.ReadInt32();
            int whistleSeq = r.ReadInt32(), a = r.ReadInt32(), b = r.ReadInt32(); ReadVector(r);
            int count = r.ReadByte();
            if (count > MaxHallMarks || index < -1 || index > 255 || markSeq < 0 || whistleSeq < 0) throw new IOException("Invalid Hall Monitor section.");
            float mine = 0;
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32(); float left = Wire.Number(r);
                if (left < 0 || left > HallMonitor.MarkSeconds + 1) throw new IOException("Invalid Hall Monitor mark.");
                if (id == self) mine = left;
            }
            if (!apply || (Hosting && !hallLabClientView)) return;
            var monitor = index >= 0 && index < npcs.Length ? npcs[index] as HallMonitor : null;
            if (!hallClientPrimed) { hallClientPrimed = true; hallSeenMarkSeq = markSeq; hallSeenWhistleSeq = whistleSeq; }
            if (markSeq > hallSeenMarkSeq)
            {
                hallSeenMarkSeq = markSeq; hallClientCues++;
                if (monitor != null) monitor.PlayWriteUp();
                if (lastMarked == self) HallMarkedNotice();
            }
            if (whistleSeq > hallSeenWhistleSeq)
            {
                hallSeenWhistleSeq = whistleSeq; hallClientCues++;
                if (monitor != null) monitor.PlayWhistle();
                if (a == self || b == self) HallWhistleNotice();
            }
            hallLocalLeft = mine;
        }

        string HallMonitorDiagnostic()
        {
            var monitor = CurrentHallMonitor();
            var sprite = monitor == null || monitor.spriteRenderer.Length == 0 || monitor.spriteRenderer[0] == null ? null : monitor.spriteRenderer[0].sprite;
            bool replicaOk = monitor != null && monitor.GetComponent<Principal>() == null && sprite != null && sprite.texture != null && sprite.texture.name.StartsWith("BaldiTogether_HallMonitor", StringComparison.Ordinal);
            return ",\"hallMonitorPresent\":" + (monitor != null ? "true" : "false") + ",\"hallMonitorBodyOk\":" + (replicaOk ? "true" : "false")
                + ",\"hallMonitorLocalMarked\":" + (hallLocalLeft > 0 ? "true" : "false") + ",\"hallMonitorGauge\":" + (hallGauge != null ? "true" : "false") + ",\"hallMonitorMarks\":" + hallMarksMade + ",\"hallMonitorWhistles\":" + hallWhistles + ",\"hallMonitorClientCues\":" + hallClientCues;
        }

        bool hallLabStarted;
        void UpdateHallMonitorLab()
        {
            if (hallLabStarted || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-hall-monitor-tour")) return;
            hallLabStarted = true; StartCoroutine(TourHallMonitor());
        }

        static byte[] HallSection(Action<BinaryWriter> write)
        { using (var memory = new MemoryStream()) { using (var writer = new BinaryWriter(memory)) write(writer); return memory.ToArray(); } }

        bool HallSectionRead(byte[] section, bool apply)
        {
            using (var memory = new MemoryStream(section)) using (var reader = new BinaryReader(memory)) { ReadHallMonitorWorld(reader, apply); return memory.Position == memory.Length; }
        }

        IEnumerator TourHallMonitor()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); AutoStartRun();
            while (!worldReady) yield return null;
            var manager = Singleton<BaseGameManager>.Instance; var ec = manager.Ec; var core = Singleton<CoreGameManager>.Instance; var player = core.GetPlayer(0);

            // Spawned through the floor's own NPC list, on a hall tile, with his poster and native components wired.
            int listIndex = ec.npcsToSpawn.FindIndex(n => n is HallMonitor);
            CheckNativeUi("hm-in-native-spawn-list", listIndex >= 0 && HallMonitorPrefab.IsPrefab(ec.npcsToSpawn[listIndex]));
            var tile = listIndex >= 0 && ec.npcSpawnTile != null && listIndex < ec.npcSpawnTile.Length ? ec.npcSpawnTile[listIndex] : null;
            CheckNativeUi("hm-spawn-tile-in-hall", tile != null && tile.room != null && tile.room.category == RoomCategory.Hall);
            if (ec.Npcs.Count == 0) ec.SpawnNPCs();
            float until = Time.realtimeSinceStartup + 20;
            while (CurrentHallMonitorLive(ec) == null && Time.realtimeSinceStartup < until) yield return null;
            npcs = ec.Npcs.ToArray();
            var monitor = CurrentHallMonitor();
            CheckNativeUi("hm-spawned-by-native-spawner", monitor != null && monitor.ec == ec && monitor.gameObject.activeInHierarchy && monitor.Character == HallMonitor.Id);
            if (monitor == null) { WriteLabResults("hall-monitor-results.json", "HallMonitor"); yield break; }
            CheckNativeUi("hm-native-components-wired", monitor.looker != null && NpcField<NPC>(monitor.looker, "npc") == monitor && monitor.Navigator != null && monitor.Navigator.npc == monitor
                && monitor.audMan != null && monitor.Poster == HallMonitorAssets.Poster && monitor.GetComponent<Principal>() == null
                && monitor.spriteRenderer.Length > 0 && monitor.spriteRenderer[0].sprite == monitor.patrolSprite);
            var hmPoster = HallMonitorAssets.Poster;
            CheckNativeUi("hm-poster-native-layout", hmPoster.baseTexture != null && hmPoster.baseTexture.width == 256 && hmPoster.baseTexture.isReadable && hmPoster.textData.Length == 2
                && hmPoster.textData[0].textKey == HallMonitorAssets.PosterName && hmPoster.textData[1].textKey == HallMonitorAssets.PosterDescription && hmPoster.textData.All(t => t.font != null));

            // The floor's character-poster room (CharacterPostersRoomFunction) hangs one poster per NPC on the floor;
            // find his among them: a wall tile whose renderer carries his generated poster texture.
            var posterRooms = Object.FindObjectsOfType<CharacterPostersRoomFunction>().Select(f => f.Room).Where(r => r != null && r.ec == ec).ToArray();
            int hmIndex = ec.loadedPosters.IndexOf(hmPoster);
            Texture hmTexture = hmIndex >= 0 && hmIndex < ec.posterTextures.Count ? ec.posterTextures[hmIndex] : null;
            Cell posterCell = null; Direction posterDir = Direction.North; int roomPosters = 0;
            var block = new MaterialPropertyBlock();
            foreach (var room in posterRooms)
                foreach (var cell in room.cells)
                {
                    if (cell == null || cell.Tile == null || cell.Tile.MeshRenderer == null) continue;
                    cell.Tile.MeshRenderer.GetPropertyBlock(block);
                    foreach (var side in new[] { Direction.North, Direction.East, Direction.South, Direction.West })
                    {
                        var texture = block.GetTexture("_Poster" + side.ToString().Substring(0, 1));
                        if (texture == null) continue;
                        roomPosters++;
                        if (texture == hmTexture) { posterCell = cell; posterDir = side; }
                    }
                }
            Log("LAB poster room: rooms=" + posterRooms.Length + " postersInRoom=" + roomPosters + " hallMonitorFound=" + (posterCell != null));
            CheckNativeUi("hm-poster-in-character-poster-room", posterRooms.Length > 0 && posterCell != null && roomPosters >= 2);
            if (posterCell != null)
            {
                // Frame his poster with its neighbours: the farthest room cell (up to 3 cells) in front of
                // his wall with a clear eye-level line to it; the room's desks block some cells.
                Vector3 wallPoint = posterCell.CenterWorldPosition + posterDir.ToVector3() * 4.9f + Vector3.up * 5;
                Vector3 spot = posterCell.CenterWorldPosition;
                foreach (var cell in posterRooms.SelectMany(r => r.cells).Where(c => c != null)
                    .Where(c => Vector3.Dot(c.CenterWorldPosition - posterCell.CenterWorldPosition, posterDir.ToVector3()) <= 0)
                    .OrderByDescending(c => Mathf.Min(30, (c.CenterWorldPosition - posterCell.CenterWorldPosition).magnitude)))
                {
                    if ((cell.CenterWorldPosition - posterCell.CenterWorldPosition).magnitude > 31) continue;
                    if (ClearWorldLine(cell.CenterWorldPosition + Vector3.up * 5, wallPoint - posterDir.ToVector3() * 0.2f)) { spot = cell.CenterWorldPosition; break; }
                }
                player.Teleport(spot);
                LabFace(player, wallPoint);
                yield return new WaitForSecondsRealtime(0.5f);
                Log("LAB poster room shot: " + (spot - posterCell.CenterWorldPosition).magnitude.ToString("0") + " units from his wall cell");
                QueueScreenshot(Path.Combine(Lab.Workspace, "artifacts/tests/hall-monitor-poster-room.png"));
                yield return new WaitForSecondsRealtime(0.5f);
            }

            // His poster on a real wall, rendered by the game's own poster builder and text generator.
            var wallCell = ec.AllTilesNoGarbage(false, false).Where(c => c.HasSoftFreeWall && c.room != null && c.room.category == RoomCategory.Hall)
                .OrderBy(c => (c.CenterWorldPosition - player.transform.position).sqrMagnitude).FirstOrDefault();
            bool built = false;
            if (wallCell != null)
            {
                Direction wallDir = wallCell.RandomUncoveredDirection(new System.Random(1));
                ec.BuildPoster(hmPoster, wallCell, wallDir); // false when the floor's poster room already generated his texture
                built = ec.loadedPosters.Contains(hmPoster);
                Vector3 view = wallCell.CenterWorldPosition - wallDir.ToVector3() * 4;
                player.Teleport(ClearWorldLine(wallCell.CenterWorldPosition + Vector3.up, view + Vector3.up) ? view : wallCell.CenterWorldPosition);
                LabFace(player, wallCell.CenterWorldPosition + wallDir.ToVector3() * 5);
                yield return new WaitForSecondsRealtime(0.5f);
                QueueScreenshot(Path.Combine(Lab.Workspace, "artifacts/tests/hall-monitor-poster-ingame.png"));
                yield return new WaitForSecondsRealtime(0.5f);
            }
            CheckNativeUi("hm-poster-built-on-wall-with-text", built);

            // Random like the game's own NPCs: about half of runs per floor, the same answer for the same seed and floor.
            var floor = core.sceneObject;
            int hits = 0; bool stable = true;
            for (int seed = 1; seed <= 400; seed++) { bool roll = HallMonitorSpawnPatch.Rolls(seed, floor); if (roll) hits++; stable &= roll == HallMonitorSpawnPatch.Rolls(seed, floor); }
            CheckNativeUi("hm-spawn-is-seeded-random", stable && hits > 140 && hits < 260);

            // He patrols by himself: the native navigator moves him between hall tiles.
            core.GetPlayer(0).Teleport(ec.spawnPoint);
            Vector3 start = monitor.transform.position;
            until = Time.realtimeSinceStartup + 6;
            while (Vector3.Distance(start, monitor.transform.position) < 5 && Time.realtimeSinceStartup < until) yield return null;
            CheckNativeUi("hm-patrols", Vector3.Distance(start, monitor.transform.position) >= 5 && monitor.behaviorStateMachine.CurrentState is HallMonitor_Patrol);

            // Fixture: stop him in an open hall spot facing a clear line; the checks below run synchronously.
            Vector3 origin = monitor.transform.position;
            var directions = new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
            Vector3 open = directions.FirstOrDefault(d => ClearWorldLine(origin + Vector3.up, origin + d * 10));
            Vector3 blocked = directions.FirstOrDefault(d => !ClearWorldLine(origin + Vector3.up, origin + d * 20));
            hallLabManual = true; // the tour drives the host steps itself from here
            monitor.behaviorStateMachine.ChangeState(new HallMonitor_Busy(monitor, HallMonitor.Look.Patrol, 999));
            monitor.transform.rotation = Quaternion.LookRotation(open == Vector3.zero ? Vector3.forward : open);
            // Evidence: what a player standing in front of him sees.
            Vector3 face = open == Vector3.zero ? Vector3.forward : open;
            player.Teleport(origin + face * 8); LabFace(player, monitor.transform.position);
            yield return new WaitForSecondsRealtime(0.5f);
            QueueScreenshot(Path.Combine(Lab.Workspace, "artifacts/tests/hall-monitor-patrol.png"));
            yield return new WaitForSecondsRealtime(0.5f);
            float now = Time.realtimeSinceStartup;
            var runner = new Peer { Id = 3, Name = "Runner", Accepted = true, Ready = true, HasPose = true, Position = origin + open * 8, PoseAt = now };
            peers[3] = runner;
            principalRules[3] = new RemoteRule { Rule = "Running", Sensitivity = 1, Visible = true, At = now };
            player.Teleport(origin - (open == Vector3.zero ? Vector3.forward : open) * 60); // host far away and walking
            Action freshen = () => { float t = Time.realtimeSinceStartup; runner.PoseAt = t; principalRules[3].At = t; };
            Func<int> scanTimes = () => { for (int i = 0; i < 6; i++) { freshen(); ScanHallRunners(monitor, 0.1f); } return hallMarks.Count; };

            runner.Eliminated = true; scanTimes(); bool elim = !hallMarks.ContainsKey(3);
            runner.Eliminated = false; runner.InLocker = true; scanTimes(); bool locker = !hallMarks.ContainsKey(3);
            runner.InLocker = false;
            CheckNativeUi("hm-ignores-eliminated-and-locker", elim && locker);
            principalRules[3].Rule = ""; scanTimes();
            CheckNativeUi("hm-ignores-walking-player", !hallMarks.ContainsKey(3));
            principalRules[3].Rule = "Running";
            if (blocked != Vector3.zero)
            {
                runner.Position = origin + blocked * 20; monitor.transform.rotation = Quaternion.LookRotation(blocked); scanTimes();
                CheckNativeUi("hm-ignores-runner-behind-wall", !hallMarks.ContainsKey(3));
                runner.Position = origin + open * 8; monitor.transform.rotation = Quaternion.LookRotation(open);
            }
            freshen(); ScanHallRunners(monitor, 0.1f);
            bool notYet = !hallMarks.ContainsKey(3);
            scanTimes();
            CheckNativeUi("hm-marks-running-remote-after-sight-time", notYet && hallMarks.ContainsKey(3) && hallMarkSeq == 1 && hallLastMarked == 3 && monitor.Pose == HallMonitor.Look.Writing
                && monitor.behaviorStateMachine.CurrentState is HallMonitor_Busy && monitor.audMan.QueuedAudioIsPlaying);
            hallMarks[3] = 5; monitor.SetPose(HallMonitor.Look.Patrol); scanTimes();
            CheckNativeUi("hm-repeat-sighting-refreshes-quietly", hallMarkSeq == 1 && Mathf.Approximately(hallMarks[3], HallMonitor.MarkSeconds));

            // The host's own player is judged from its native running guilt.
            toast = null;
            player.Teleport(origin + open * 6); player.RuleBreak("Running", 2f);
            monitor.SetPose(HallMonitor.Look.Patrol);
            for (int i = 0; i < 6 && !hallMarks.ContainsKey(self); i++) { freshen(); ScanHallRunners(monitor, 0.1f); }
            CheckNativeUi("hm-marks-running-host-player", hallMarks.ContainsKey(self) && hallMarkSeq == 2 && toast != null && toast.Contains("wrote you up"));
            runner.Position = player.transform.position + open * 40; freshen(); // apart, so this host step cannot whistle
            HostHallMonitor(monitor, 0.01f, false);
            yield return null;
            CheckNativeUi("hm-native-hud-gauge-for-own-mark", hallGauge != null && hallGauge.Active);

            // Two marked players apart: nothing. Together: one whistle, Baldi hears it at the pair, marks cleared.
            player.ClearGuilt();
            runner.Position = player.transform.position + open * 40; freshen();
            CheckHallWhistle(monitor);
            bool apart = hallWhistleSeq == 0;
            runner.Position = player.transform.position + open * 6; freshen();
            var baldi = ec.Npcs.OfType<Baldi>().FirstOrDefault();
            CheckHallWhistle(monitor);
            // A bystander right next to Baldi: without the whistle he is the nearest; with it Baldi picks the pair.
            var bystander = new Peer { Id = 5, Name = "Bystander", Accepted = true, Ready = true, HasPose = true, PoseAt = Time.realtimeSinceStartup, Position = baldi == null ? origin : baldi.transform.position + Vector3.right };
            peers[5] = bystander;
            Peer focused = baldi == null ? null : NearestBaldiPeer(baldi, Time.realtimeSinceStartup);
            float focus = hallFocusLeft; hallFocusLeft = 0;
            Peer unfocused = baldi == null ? null : NearestBaldiPeer(baldi, Time.realtimeSinceStartup);
            hallFocusLeft = focus; peers.Remove(5);
            Log("LAB hall whistle: apart=" + apart + " seq=" + hallWhistleSeq + " marks=" + hallMarks.Count + " pose=" + monitor.Pose + " focus=" + focus + " focused=" + (focused == null ? -1 : focused.Id) + " unfocused=" + (unfocused == null ? -1 : unfocused.Id));
            CheckNativeUi("hm-whistles-on-marked-pair-only", apart && hallWhistleSeq == 1 && hallMarks.Count == 0 && monitor.Pose == HallMonitor.Look.Whistle && toast.Contains("WHISTLE"));
            CheckNativeUi("hm-whistle-points-baldi-at-pair", focus > 9 && focused != null && (focused.Id == self || focused.Id == 3) && unfocused != null && unfocused.Id == 5);
            player.Teleport(origin + face * 8); LabFace(player, monitor.transform.position);
            yield return new WaitForSecondsRealtime(0.3f);
            QueueScreenshot(Path.Combine(Lab.Workspace, "artifacts/tests/hall-monitor-whistle.png"));
            yield return new WaitForSecondsRealtime(0.5f);
            hallMarks[3] = HallMonitor.MarkSeconds; hallMarks[self] = HallMonitor.MarkSeconds; freshen();
            CheckHallWhistle(monitor);
            CheckNativeUi("hm-whistle-cooldown", hallWhistleSeq == 1);

            // Marks expire in NPC time and leave with their player.
            hallMarks[3] = 0.05f; freshen(); HostHallMonitor(monitor, 0.1f, false);
            bool expired = !hallMarks.ContainsKey(3);
            hallMarks[3] = HallMonitor.MarkSeconds; peers.Remove(3); HostHallMonitor(monitor, 0.01f, false);
            CheckNativeUi("hm-marks-expire-and-clear-on-disconnect", expired && !hallMarks.ContainsKey(3));
            peers[3] = runner;

            // World section: round trip, client cue once per sequence step, malformed input rejected.
            hallMarks[self] = 12;
            byte[] section = HallSection(WriteHallMonitorWorld);
            ResetHallMonitor(); hallClientPrimed = true; hallSeenMarkSeq = 1; hallSeenWhistleSeq = 1; toast = null;
            // Read as a client would: Hosting is true in this process, so call the client half directly.
            bool consumed = HallSectionReadAsClient(section);
            int cues = hallClientCues;
            HallSectionReadAsClient(section);
            CheckNativeUi("hm-world-section-client-cues-once", consumed && cues == 1 && hallClientCues == 1 && toast != null && toast.Contains("wrote you up") && Mathf.Approximately(hallLocalLeft, 12));
            bool rejected = false;
            try { HallSectionRead(HallSection(w => { w.Write(0); w.Write(1); w.Write(1); w.Write(0); w.Write(-1); w.Write(-1); WriteVector(w, Vector3.zero); w.Write((byte)200); }), false); }
            catch (IOException) { rejected = true; }
            CheckNativeUi("hm-world-section-rejects-bad-count", rejected);

            // Clients find the same prefab by name when the host's NPC catalog lists him.
            CheckNativeUi("hm-replica-template-resolves", ReplicaTemplate((int)HallMonitor.Id, HallMonitor.PrefabName) == HallMonitorPrefab.Get());

            // Reset releases everything, and single player (no session) never adds him.
            ResetHallMonitor();
            yield return null;
            CheckNativeUi("hm-reset-clears-marks-and-gauge", hallMarks.Count == 0 && hallGauge == null && hallLocalLeft == 0);
            CheckNativeUi("hm-single-player-unchanged", !HallMonitorSpawnPatch.ShouldAdd(false, core.sceneObject) && !HallMonitorSpawnPatch.Decide(false, core.sceneObject, 7) && HallMonitorSpawnPatch.ShouldAdd(true, core.sceneObject));

            monitor.behaviorStateMachine.ChangeState(new HallMonitor_Patrol(monitor));
            peers.Remove(3); principalRules.Remove(3); hallLabManual = false;
            WriteLabResults("hall-monitor-results.json", "HallMonitor");
        }

        // PlayerMovement.MouseMove re-reads the transform's yaw each frame, so turning the transform turns the view.
        static void LabFace(PlayerManager player, Vector3 target)
        { player.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(target - player.transform.position, Vector3.up).normalized); }

        static HallMonitor CurrentHallMonitorLive(EnvironmentController ec) { return ec.Npcs.OfType<HallMonitor>().FirstOrDefault(); }

        // Lab helper: run the client half of ReadHallMonitorWorld in the host process.
        bool HallSectionReadAsClient(byte[] section)
        {
            hallLabClientView = true;
            try { return HallSectionRead(section, true); }
            finally { hallLabClientView = false; }
        }
        bool hallLabClientView, hallLabManual;
    }
}
