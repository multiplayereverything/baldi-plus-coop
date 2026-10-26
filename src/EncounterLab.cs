using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Two-process encounter lab (-bt-lab -bt-encounter): a real host and a real client meet each NPC and use items over the network.
    //
    // The host drives a scripted scenario (EncRunHost). It writes one command at a time to the shared file
    // MultiplayerMod/private/encounter-command.json (the Melon client reaches it through a relative path, as NpcSyncLab does).
    // The client follows the command with its real player: teleport, face or aim its real camera at a replica, use a real item,
    // click Dr. Reflex's hotspots. Everything that is verified travels over the real network through the features' own code:
    // gaze reports, Pomp arrival reports, Reflex clicks, item relays, host commands and world sections.
    //
    // Lab-only shortcuts that do not change any feature rule: NPCs not under test (Baldi included) are parked far away with their
    // navigators off; the host stands far from the arena; the host tells the client which Reflex hotspot is correct (the click is
    // still validated by the host); the step NPC is spawned or placed next to the client's pose.
    //
    // Each process writes its observations: host to MultiplayerMod/artifacts/tests/encounter-host.json, client to
    // <its Root>/MultiplayerMod/artifacts/tests/encounter-client.json. tests/verify-encounter.py combines them.
    public sealed partial class TogetherRuntime
    {
        [Serializable] public sealed class EncounterCommand
        {
            public int Seq; public string Step = ""; public bool Teleport; public Vector3 Position; public Vector3 Face; public bool Aim;
            public int Look = -1; public int Item; public int ReflexSerial; public int ReflexSide; public string HostName = "";
        }

        static readonly string[] EncHostChecks =
        {
            "host-setup-ready", "host-client-at-arena",
            "host-beans-targets-client", "host-beans-gum-hits-client",
            "host-crafters-gaze-accepted",
            "host-pomp-assigns-client", "host-pomp-arrival-rewarded",
            "host-reflex-test-for-client", "host-reflex-click-accepted", "host-reflex-pass-pays-wallet",
            "host-chalkles-client-in-board-room", "host-chalkles-client-charges",
            "host-thetest-client-gaze-reported", "host-thetest-remote-gaze-freezes",
            "host-item-bsoda-relayed", "host-item-alarm-clock-relayed", "host-item-whistle-relayed",
            "host-client-eliminated-host-alive", "host-no-exceptions"
        };

        static readonly string[] EncClientChecks =
        {
            "client-ready-acknowledged", "client-reached-arena",
            "client-beans-native-gum-applied",
            "client-crafters-gaze-reported",
            "client-pomp-appointment-received", "client-pomp-reward-received",
            "client-reflex-test-started", "client-reflex-click-sent", "client-reflex-pass-received",
            "client-chalkles-boards-built", "client-chalkles-charge-mirrored",
            "client-thetest-gaze-reported", "client-thetest-freeze-mirrored",
            "client-item-bsoda-used", "client-item-alarm-clock-used", "client-item-whistle-used",
            "client-spectate-overlay-shows-host", "client-no-exceptions"
        };

        bool encChecked, encEnabled, encHooked, encStarted, encFinished, encAbort, encParkReady;
        string encRole = "", encStep = "waiting", encFirstException = "", encPending;
        int encEpoch, encSeq, encClient = -1, encExceptions;
        float encLabAt, encBeat, encNextSweep, encNextWrite, encStartedAt;
        readonly Dictionary<string, bool> encChecks = new Dictionary<string, bool>();
        readonly Dictionary<string, string> encInfo = new Dictionary<string, string>();
        readonly List<string> encNotes = new List<string>();
        EncounterCommand encCommand = new EncounterCommand();

        // Host geometry: the arena cell (client), a straight clear hall direction from it, the host's own spot and the NPC park.
        Vector3 encArena, encDir, encHostSpot, encPark;
        Cell encArenaCell;
        Direction encDirection;
        readonly HashSet<NPC> encActiveNpcs = new HashSet<NPC>(), encParked = new HashSet<NPC>();

        // Client state.
        EncounterCommand encClientCommand;
        int encClientSeq = -1, encReflexClicked, encReflexHinted, encReflexGuessed;
        string encClientStep = "";
        readonly Dictionary<string, int> encBase = new Dictionary<string, int>();
        bool encHolding, encPompMoved, encItemDone, encItemConsumed;
        Vector3 encHold;
        float encNextRead, encItemAt, encReflexOpenAt = -1, encDoneAt = -1;

        // ---------------------------------------------------------------- entry points

        void UpdateEncounterLab()
        {
            if (!encChecked)
            {
                encChecked = true;
                encEnabled = Lab.Enabled && Lab.HasArg("-bt-encounter");
                encLabAt = Time.realtimeSinceStartup;
            }
            if (!encEnabled || encFinished) return;
            if (!encHooked) HookEncounterLogs();
            if (!Active) return;
            float now = Time.realtimeSinceStartup;
            if (Hosting)
            {
                if (!encStarted && Running && worldReady)
                {
                    encStarted = true; encRole = "host"; encEpoch = epoch; encBeat = encStartedAt = now;
                    foreach (string name in EncHostChecks) encChecks[name] = false;
                    EncLog("host scenario started, epoch " + epoch);
                    StartCoroutine(EncRunHost());
                }
                else if (encStarted)
                {
                    EncFlush();
                    // A coroutine that died without finishing stops beating; write what was observed.
                    if (now - encBeat > 90) { EncNote("host scenario stalled in step " + encStep); EncFinish(); }
                }
                return;
            }
            EncClientFrame(now);
        }

        string EncounterDiagnostic()
        {
            if (!encEnabled) return "";
            return ",\"encounterRole\":" + EncJson(encRole) + ",\"encounterStep\":" + EncJson(encStep)
                + ",\"encounterPassed\":" + encChecks.Count(p => p.Value) + ",\"encounterFailed\":" + encChecks.Count(p => !p.Value)
                + ",\"encounterExceptions\":" + encExceptions + ",\"encounterFinished\":" + (encFinished ? "true" : "false");
        }

        // Counts exceptions reported by Unity and error lines written through the mod log (Guard, rejected peers).
        void HookEncounterLogs()
        {
            encHooked = true;
            var previous = Log;
            Log = message => { previous(message); EncCountLog(message, false); };
            Application.logMessageReceived += (condition, stack, type) => { if (type == LogType.Exception) EncCountLog(condition, true); };
        }

        void EncCountLog(string message, bool unity)
        {
            if (message == null) return;
            if (!unity && message.IndexOf("Exception", StringComparison.Ordinal) < 0 && message.IndexOf(" failed: ", StringComparison.Ordinal) < 0
                && !message.StartsWith("Rejected peer", StringComparison.Ordinal) && message.IndexOf("Host sent invalid", StringComparison.Ordinal) < 0) return;
            encExceptions++;
            if (encFirstException.Length == 0) encFirstException = message.Length > 400 ? message.Substring(0, 400) : message;
        }

        // ---------------------------------------------------------------- shared helpers

        void EncLog(string message) { Log("ENCOUNTER " + encRole + ": " + message); }
        void EncNote(string message) { encNotes.Add(encStep + ": " + message); EncLog(message); }
        void EncCheck(string name, bool value) { encChecks[name] = value; }
        void EncLatch(string name, bool value) { bool old; if (value && (!encChecks.TryGetValue(name, out old) || !old)) { encChecks[name] = true; EncLog("observed " + name); } }
        static string EncSeconds(float value) { return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }
        static string EncVector(Vector3 v) { return EncSeconds(v.x) + "," + EncSeconds(v.y) + "," + EncSeconds(v.z); }

        static string EncJson(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        string EncCommandPath
        { get { return Path.GetFullPath(Path.Combine(Root, encRole == "host" ? "MultiplayerMod/private/encounter-command.json" : "../../../MultiplayerMod/private/encounter-command.json")); } }

        void EncWriteResults(bool final)
        {
            try
            {
                string folder = Path.Combine(Root, "MultiplayerMod/artifacts/tests");
                Directory.CreateDirectory(folder);
                var sb = new StringBuilder();
                sb.Append("{\"role\":").Append(EncJson(encRole)).Append(",\"finished\":").Append(final ? "true" : "false")
                    .Append(",\"step\":").Append(EncJson(encStep)).Append(",\"elapsed\":").Append(EncSeconds(Time.realtimeSinceStartup - encStartedAt))
                    .Append(",\"passed\":").Append(encChecks.Count(p => p.Value)).Append(",\"failed\":").Append(encChecks.Count(p => !p.Value))
                    .Append(",\"checks\":{").Append(string.Join(",", encChecks.Select(p => EncJson(p.Key) + ":" + (p.Value ? "true" : "false")).ToArray()))
                    .Append("},\"info\":{").Append(string.Join(",", encInfo.Select(p => EncJson(p.Key) + ":" + EncJson(p.Value)).ToArray()))
                    .Append("},\"notes\":[").Append(string.Join(",", encNotes.Select(n => EncJson(n)).ToArray()))
                    .Append("],\"exceptions\":").Append(encExceptions).Append(",\"firstException\":").Append(EncJson(encFirstException)).Append("}");
                File.WriteAllText(Path.Combine(folder, encRole == "host" ? "encounter-host.json" : "encounter-client.json"), sb.ToString());
            }
            catch (Exception ex) { Log("ENCOUNTER: results not written (" + ex.GetType().Name + ")"); }
        }

        void EncFinish()
        {
            if (encFinished) return;
            EncCheck(encRole == "host" ? "host-no-exceptions" : "client-no-exceptions", encExceptions == 0);
            encFinished = true;
            EncWriteResults(true);
            EncLog("finished: " + encChecks.Count(p => p.Value) + "/" + encChecks.Count + " checks observed");
        }

        static void EncFace(PlayerManager player, Vector3 direction)
        {
            direction.y = 0;
            if (direction.sqrMagnitude < .0001f) return;
            player.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        // ---------------------------------------------------------------- host: scenario

        bool EncAlive() { return Active && Hosting && epoch == encEpoch && worldReady; }

        EnvironmentController EncEc() { var manager = Singleton<BaseGameManager>.Instance; return manager == null ? null : manager.Ec; }

        Peer EncPeer() { Peer peer; return encClient >= 0 && peers.TryGetValue(encClient, out peer) ? peer : null; }

        bool EncClientNear(Vector3 position, float radius)
        {
            Peer peer = EncPeer();
            return peer != null && peer.HasPose && Time.realtimeSinceStartup - peer.PoseAt < 2 && Flat(peer.Position, position) < radius;
        }

        bool EncClientInRoom(RoomController room)
        {
            Peer peer = EncPeer(); var ec = EncEc();
            if (peer == null || ec == null || room == null || !peer.HasPose) return false;
            Cell cell = ec.CellFromPosition(peer.Position);
            return cell != null && !cell.Null && cell.room == room;
        }

        void EncTick()
        {
            float now = Time.realtimeSinceStartup;
            encBeat = now;
            EncFlush();
            if (now >= encNextSweep) { encNextSweep = now + 1; EncSweep(); EncHoldHost(); }
            if (now >= encNextWrite) { encNextWrite = now + 3; EncWriteResults(false); }
        }

        IEnumerable<object> EncWait(float seconds, Func<bool> done)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (EncAlive() && Time.realtimeSinceStartup < until && (done == null || !done())) { EncTick(); yield return null; }
            EncTick();
        }

        void EncSend(string step, bool teleport, Vector3 position, Vector3 face, bool aim, int look, int item)
        {
            encCommand = new EncounterCommand
            {
                Seq = ++encSeq, Step = step, Teleport = teleport, Position = position, Face = face, Aim = aim, Look = look, Item = item,
                ReflexSerial = encCommand.ReflexSerial, ReflexSide = encCommand.ReflexSide, HostName = Name
            };
            EncLog("command " + encSeq + " " + step);
            EncQueue();
        }

        void EncQueue() { encPending = JsonUtility.ToJson(encCommand); EncFlush(); }

        void EncFlush()
        {
            if (encPending == null) return;
            try
            {
                using (var file = new FileStream(EncCommandPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file)) writer.Write(encPending);
                encPending = null;
            }
            catch (IOException) { /* The client is reading; retry next frame. */ }
            catch (UnauthorizedAccessException) { }
        }

        T EncSpawn<T>(EnvironmentController ec) where T : NPC
        {
            T existing = ec.Npcs.OfType<T>().FirstOrDefault(n => n != null);
            if (existing == null)
            {
                T prefab = Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(n => !n.gameObject.scene.IsValid());
                if (prefab == null) { EncNote("no " + typeof(T).Name + " prefab"); return null; }
                existing = (T)ec.SpawnNPC(prefab, ec.CellFromPosition(encPark).position);
                encInfo["spawned-" + typeof(T).Name] = "true";
            }
            npcs = ec.Npcs.ToArray();
            return existing;
        }

        // The NPC under test: placed by the client, navigator on or off as the step needs.
        void EncUse(NPC npc, Vector3 position, Vector3 face, bool navigate)
        {
            encActiveNpcs.Add(npc); encParked.Remove(npc);
            if (npc.Navigator != null)
            {
                npc.Navigator.enabled = navigate;
                if (navigate) disabledNavigators.Remove(npc.Navigator);
                else if (!disabledNavigators.Contains(npc.Navigator)) disabledNavigators.Add(npc.Navigator);
            }
            npc.Entity.Teleport(position);
            if (face.sqrMagnitude > .01f) npc.transform.rotation = Quaternion.LookRotation(face, Vector3.up);
        }

        void EncPark(NPC npc)
        {
            if (npc == null) return;
            encActiveNpcs.Remove(npc); encParked.Add(npc);
            if (npc.Navigator != null)
            {
                npc.Navigator.enabled = false;
                if (!disabledNavigators.Contains(npc.Navigator)) disabledNavigators.Add(npc.Navigator);
            }
            npc.Entity.Teleport(encPark);
        }

        // Every NPC not under test (including late spawns from the shared spawner) is kept at the park.
        void EncSweep()
        {
            var ec = EncEc();
            if (!encParkReady || ec == null) return;
            foreach (NPC npc in ec.Npcs.ToArray())
            {
                if (npc == null || encActiveNpcs.Contains(npc) || npc is ChalkFace) continue;
                if (!encParked.Contains(npc) || (npc.gameObject.activeInHierarchy && Flat(npc.transform.position, encPark) > 15)) EncPark(npc);
            }
        }

        void EncHoldHost()
        {
            if (!encParkReady || LocalEliminated) return;
            var core = Singleton<CoreGameManager>.Instance; var player = core == null ? null : core.GetPlayer(0);
            if (player != null && Flat(player.transform.position, encHostSpot) > 3) player.Teleport(encHostSpot);
        }

        // A hall cell with `count` wall-free cells of the same hall straight ahead and a clear physics line along them.
        bool EncStraight(EnvironmentController ec, Cell cell, Direction direction, int count)
        {
            Cell current = cell;
            IntVector2 step = direction.ToIntVector2();
            for (int i = 0; i < count; i++)
            {
                if (current.HasWallInDirection(direction)) return false;
                var next = new IntVector2(current.position.x + step.x, current.position.z + step.z);
                if (!ec.ContainsCoordinates(next)) return false;
                Cell nextCell = ec.CellFromPosition(next);
                if (nextCell == null || nextCell.Null || nextCell.room != cell.room) return false;
                current = nextCell;
            }
            return ClearWorldLine(cell.CenterWorldPosition, current.CenterWorldPosition);
        }

        Cell EncCellAhead(EnvironmentController ec, int cells)
        {
            IntVector2 step = encDirection.ToIntVector2();
            return ec.CellFromPosition(new IntVector2(encArenaCell.position.x + step.x * cells, encArenaCell.position.z + step.z * cells));
        }

        IEnumerator EncRunHost()
        {
            var steps = new List<KeyValuePair<string, Func<IEnumerator>>>
            {
                new KeyValuePair<string, Func<IEnumerator>>("setup", EncSetup),
                new KeyValuePair<string, Func<IEnumerator>>("beans", EncBeans),
                new KeyValuePair<string, Func<IEnumerator>>("crafters", EncCrafters),
                new KeyValuePair<string, Func<IEnumerator>>("pomp", EncPomp),
                new KeyValuePair<string, Func<IEnumerator>>("reflex", EncReflex),
                new KeyValuePair<string, Func<IEnumerator>>("chalkles", EncChalkles),
                new KeyValuePair<string, Func<IEnumerator>>("thetest", EncTheTest),
                new KeyValuePair<string, Func<IEnumerator>>("items", EncItems),
                new KeyValuePair<string, Func<IEnumerator>>("spectate", EncSpectate)
            };
            foreach (var step in steps)
            {
                if (encAbort) break;
                if (!EncAlive()) { EncNote("run ended before " + step.Key); break; }
                encStep = step.Key;
                float started = Time.realtimeSinceStartup;
                EncLog("step " + step.Key);
                IEnumerator inner = step.Value();
                while (true)
                {
                    bool more;
                    // One step's error is recorded and the scenario moves on; it still counts against the no-exceptions check.
                    try { more = inner.MoveNext(); }
                    catch (Exception ex) { more = false; encExceptions++; if (encFirstException.Length == 0) encFirstException = step.Key + ": " + ex; EncLog("step " + step.Key + " threw " + ex.GetType().Name + ": " + ex.Message); }
                    if (!more) break;
                    encBeat = Time.realtimeSinceStartup;
                    yield return inner.Current;
                }
                encInfo["seconds-" + step.Key] = EncSeconds(Time.realtimeSinceStartup - started);
                EncWriteResults(false);
            }
            encStep = "done";
            EncSend("done", false, Vector3.zero, Vector3.zero, false, -1, 0);
            float until = Time.realtimeSinceStartup + 3;
            while (encPending != null && Time.realtimeSinceStartup < until) { EncFlush(); yield return null; }
            EncFinish();
        }

        IEnumerator EncSetup()
        {
            Peer client = null;
            foreach (object o in EncWait(240, () =>
            {
                client = peers.Values.FirstOrDefault(p => p.Id != self && p.Accepted && p.Ready && p.HasPose && Time.realtimeSinceStartup - p.PoseAt < 2);
                var ec0 = EncEc(); var core0 = Singleton<CoreGameManager>.Instance;
                return client != null && hidingFinished && ec0 != null && ec0.Active && core0 != null && core0.GetPlayer(0) != null;
            })) yield return o;
            var ec = EncEc();
            if (client == null || !hidingFinished || ec == null) { EncNote("client or level never became ready (hidingFinished=" + hidingFinished + ")"); encAbort = true; yield break; }
            // The shared spawner releases NPCs after the countdown; let it finish so indices settle.
            foreach (object o in EncWait(40, () => ec.npcsLeftToSpawn.Count == 0)) yield return o;
            foreach (object o in EncWait(3, null)) yield return o;
            client = peers.Values.FirstOrDefault(p => p.Id != self && p.Accepted && p.Ready && p.HasPose);
            if (client == null) { EncNote("client left during setup"); encAbort = true; yield break; }
            encClient = client.Id;
            encInfo["client-id"] = client.Id.ToString(); encInfo["client-name"] = client.Name ?? "";

            var halls = ec.AllTilesNoGarbage(false, false).Where(c => c != null && !c.Null && c.room != null
                && (c.room.type == RoomType.Hall || c.room.category == RoomCategory.Hall)).ToList();
            Cell best = null; Direction bestDirection = Direction.North; float bestDistance = float.MaxValue;
            foreach (Cell cell in halls)
                for (int d = 0; d < 4; d++)
                {
                    float distance = Flat(cell.CenterWorldPosition, client.Position);
                    if (distance >= bestDistance || !EncStraight(ec, cell, (Direction)d, 2)) continue;
                    best = cell; bestDirection = (Direction)d; bestDistance = distance;
                }
            if (best == null) { EncNote("no hall cell with two clear cells ahead"); encAbort = true; yield break; }
            encArenaCell = best; encDirection = bestDirection; encArena = best.CenterWorldPosition; encDir = bestDirection.ToVector3();
            Cell hostCell = halls.OrderByDescending(c => Flat(c.CenterWorldPosition, encArena)).First();
            Vector3 hostAt = hostCell.CenterWorldPosition;
            Cell parkCell = halls.Where(c => c != hostCell).OrderByDescending(c => Mathf.Min(Flat(c.CenterWorldPosition, encArena), Flat(c.CenterWorldPosition, hostAt))).First();
            encHostSpot = hostAt; encPark = parkCell.CenterWorldPosition;
            encInfo["arena"] = EncVector(encArena); encInfo["arena-direction"] = bestDirection.ToString();
            encInfo["host-spot-distance"] = EncSeconds(Flat(encHostSpot, encArena));
            encInfo["park-distance-arena"] = EncSeconds(Flat(encPark, encArena)); encInfo["park-distance-host"] = EncSeconds(Flat(encPark, encHostSpot));
            encInfo["level-npcs"] = string.Join(",", ec.Npcs.Where(n => n != null).Select(n => n.GetType().Name).ToArray());

            Singleton<CoreGameManager>.Instance.GetPlayer(0).Teleport(encHostSpot);
            encParkReady = true; EncSweep();
            EncCheck("host-setup-ready", true);
            EncSend("arena", true, encArena, encDir, true, -1, 0);
            foreach (object o in EncWait(25, () => EncClientNear(encArena, 3))) yield return o;
            EncCheck("host-client-at-arena", EncClientNear(encArena, 3));
        }

        // Beans: native sight targets the client's pose, chews, spits; the host resolves the hit against the pose and commands the owner.
        IEnumerator EncBeans()
        {
            var ec = EncEc(); int targets = beansRemoteTargets, hits = beansGumHits;
            var beans = EncSpawn<Beans>(ec);
            if (beans == null) yield break;
            EncSend("beans", true, encArena, encDir, true, -1, 0);
            foreach (object o in EncWait(10, () => EncClientNear(encArena, 3))) yield return o;
            EncUse(beans, encArena + encDir * 20, -encDir, true);
            beans.behaviorStateMachine.ChangeState(new Beans_Wandering(beans));
            float natural = Time.realtimeSinceStartup;
            foreach (object o in EncWait(25, () => beansGumHits > hits)) yield return o;
            encInfo["beans-natural-seconds"] = EncSeconds(Time.realtimeSinceStartup - natural);
            // Fallback: the native spit from 4 units in front of the client's pose, so the gum starts on the pose.
            int forced = 0;
            for (int attempt = 0; attempt < 3 && beansGumHits == hits && EncAlive(); attempt++)
            {
                Peer peer = EncPeer(); if (peer == null) break;
                forced++;
                EncUse(beans, peer.Position + encDir * 4, -encDir, false);
                if (BeansRemoteSpit(beans, encClient)) beans.behaviorStateMachine.ChangeState(new Beans_Watch(beans));
                foreach (object o in EncWait(3, () => beansGumHits > hits)) yield return o;
            }
            encInfo["beans-forced-spits"] = forced.ToString();
            EncCheck("host-beans-targets-client", beansRemoteTargets > targets);
            EncCheck("host-beans-gum-hits-client", beansGumHits > hits);
            foreach (object o in EncWait(4, null)) yield return o;
            EncPark(beans);
        }

        // Arts and Crafters: the client's own gaze report (from its real camera) is validated by host sight.
        IEnumerator EncCrafters()
        {
            var ec = EncEc(); int accepted = craftersGazeAccepted, rejected = craftersGazeRejected;
            var crafters = EncSpawn<ArtsAndCrafters>(ec);
            if (crafters == null) yield break;
            int index = Array.IndexOf(npcs, (NPC)crafters);
            Cell target = EncCellAhead(ec, 2);
            EncUse(crafters, target.CenterWorldPosition, -encDir, false);
            CraftersChange(crafters, new ArtsAndCrafters_Waiting(crafters));
            crafters.SpawnAt(target.position);
            crafters.transform.rotation = Quaternion.LookRotation(-encDir, Vector3.up);
            EncSend("crafters", true, encArena, encDir, true, index, 0);
            foreach (object o in EncWait(25, () => craftersGazeAccepted > accepted)) yield return o;
            EncCheck("host-crafters-gaze-accepted", craftersGazeAccepted > accepted);
            encInfo["crafters-gaze-accepted"] = (craftersGazeAccepted - accepted).ToString();
            encInfo["crafters-gaze-rejected"] = (craftersGazeRejected - rejected).ToString();
            encInfo["crafters-state"] = crafters.behaviorStateMachine.CurrentState == null ? "none" : crafters.behaviorStateMachine.CurrentState.GetType().Name;
            foreach (object o in EncWait(2, null)) yield return o;
            foreach (var trigger in Object.FindObjectsOfType<CraftersTrigger>()) trigger.spawnChance = 0;
            CraftersChange(crafters, new ArtsAndCrafters_Waiting(crafters));
            EncPark(crafters);
        }

        // Mrs. Pomp: catches the client's pose, the owner gets the appointment, walks in, its own Arrived report earns the reward.
        IEnumerator EncPomp()
        {
            var ec = EncEc(); var core = Singleton<CoreGameManager>.Instance;
            int assigns = pompAssigns, rewards = pompRewards, points = core.GetPoints(0);
            var pomp = EncSpawn<NoLateTeacher>(ec);
            if (pomp == null) yield break;
            EncSend("pomp", true, encArena, encDir, false, Array.IndexOf(npcs, (NPC)pomp), 0);
            foreach (object o in EncWait(10, () => EncClientNear(encArena, 3))) yield return o;
            EncUse(pomp, encArena + encDir * 3, -encDir, true);
            pomp.behaviorStateMachine.ChangeState(new NoLateTeacher_Wander(pomp));
            pomp.behaviorStateMachine.ChangeNavigationState(new NavigationState_WanderRounds(pomp, 0));
            foreach (object o in EncWait(20, () => pompAssigns > assigns)) yield return o;
            var appointment = pompAppointments.LastOrDefault(a => a.Peer == encClient);
            EncCheck("host-pomp-assigns-client", pompAssigns > assigns && (appointment != null || pompRewards > rewards));
            if (appointment != null && appointment.Room != null) encInfo["pomp-room"] = appointment.Room.name;
            float assigned = Time.realtimeSinceStartup;
            foreach (object o in EncWait(75, () => pompRewards > rewards)) yield return o;
            encInfo["pomp-reward-seconds"] = EncSeconds(Time.realtimeSinceStartup - assigned);
            encInfo["pomp-wallet-gain"] = (core.GetPoints(0) - points).ToString();
            EncCheck("host-pomp-arrival-rewarded", pompRewards > rewards && core.GetPoints(0) > points);
            foreach (object o in EncWait(3, null)) yield return o;
            EncPark(pomp);
        }

        // Dr. Reflex: native test for the client; the client's clicks are validated and pay the shared wallet.
        IEnumerator EncReflex()
        {
            var ec = EncEc(); var core = Singleton<CoreGameManager>.Instance;
            int started = reflexTestsStarted, clicks = reflexClicks, passes = reflexPasses, rejectedClicks = reflexRejectedClicks, points = core.GetPoints(0);
            var reflex = EncSpawn<DrReflex>(ec);
            if (reflex == null) yield break;
            EncSend("reflex", true, encArena, encDir, false, Array.IndexOf(npcs, (NPC)reflex), 0);
            foreach (object o in EncWait(10, () => EncClientNear(encArena, 3))) yield return o;
            int attempts = 0, opens = 0; bool wasOpen = false, forClient = false;
            float until = Time.realtimeSinceStartup + 60, restartAt = 0;
            while (EncAlive() && Time.realtimeSinceStartup < until && reflexPasses == passes)
            {
                EncTick();
                var session = FindReflexSession(reflex);
                if (session != null && session.Phase >= ReflexSession.Failed)
                {
                    // A failed test sends him to the office; restart the encounter instead of waiting for the hunt.
                    reflex.behaviorStateMachine.ChangeState(new DrReflex_Wandering(reflex, 0f));
                    session = null; restartAt = 0;
                }
                if (session == null && attempts < 3 && Time.realtimeSinceStartup >= restartAt)
                {
                    Peer peer = EncPeer();
                    attempts++; restartAt = Time.realtimeSinceStartup + 12;
                    EncUse(reflex, (peer != null ? peer.Position : encArena) + encDir * 4, -encDir, true);
                    reflex.behaviorStateMachine.ChangeState(new DrReflex_Wandering(reflex, 0f));
                }
                if (session != null && session.Peer == encClient) forClient = true;
                bool open = session != null && session.Phase == ReflexSession.Test && NpcField<GameObject>(reflex, "leftHotSpot").activeSelf;
                if (open && !wasOpen)
                {
                    // Lab hint only: the client still has to click, and the host validates that click natively.
                    var side = NpcField<Direction>(reflex, "correctSide");
                    if (reflex.Entity.Flipped) side = side == Direction.East ? Direction.West : Direction.East;
                    encCommand.ReflexSerial++; encCommand.ReflexSide = (int)side; EncQueue(); opens++;
                }
                wasOpen = open;
                yield return null;
            }
            encInfo["reflex-attempts"] = attempts.ToString(); encInfo["reflex-opens"] = opens.ToString();
            encInfo["reflex-rejected-clicks"] = (reflexRejectedClicks - rejectedClicks).ToString();
            encInfo["reflex-wallet-gain"] = (core.GetPoints(0) - points).ToString();
            EncCheck("host-reflex-test-for-client", reflexTestsStarted > started && forClient);
            EncCheck("host-reflex-click-accepted", reflexClicks > clicks);
            EncCheck("host-reflex-pass-pays-wallet", reflexPasses > passes && core.GetPoints(0) > points);
            foreach (object o in EncWait(4, null)) yield return o;
            EncPark(reflex);
        }

        // Chalkles: the client's pose in a board room counts as an occupant and charges the host's real Chalkles.
        IEnumerator EncChalkles()
        {
            var ec = EncEc(); int enters = chalkRemoteEnters, laughs = chalkLaughs, acks = chalkAcks;
            var face = EncSpawn<ChalkFace>(ec);
            if (face == null) yield break;
            int index = Array.IndexOf(npcs, (NPC)face);
            var board = BoardsOf(face).FirstOrDefault(b => b != null && b.Room != null && b.Room != encArenaCell.room);
            if (board == null) { EncNote("Chalkles has no board room"); yield break; }
            RoomController room = board.Room;
            Cell spot = room.RandomEntitySafeCellNoGarbage();
            Vector3 at = spot != null ? spot.CenterWorldPosition : ec.RealRoomMid(room);
            encInfo["chalkles-room"] = room.name; encInfo["chalkles-boards"] = BoardsOf(face).Count.ToString();
            EncSend("chalkles", true, at, Vector3.zero, false, index, 0);
            foreach (object o in EncWait(20, () => EncClientInRoom(room))) yield return o;
            EncCheck("host-chalkles-client-in-board-room", EncClientInRoom(room));
            float maxCharge = 0; bool charging = false;
            foreach (object o in EncWait(30, () =>
            {
                float charge = (float)chalkChargeField.GetValue(face);
                if (charge > maxCharge) maxCharge = charge;
                if (ChalkStateCode(face) >= 1) charging = true;
                return chalkRemoteEnters > enters && charging && maxCharge > 0;
            })) yield return o;
            encInfo["chalkles-max-charge"] = EncSeconds(maxCharge);
            EncCheck("host-chalkles-client-charges", chalkRemoteEnters > enters && charging && maxCharge > 0);
            // Informational: a full charge makes him laugh and lock the room; the client acknowledges the lock once.
            foreach (object o in EncWait(35, () => chalkLaughs > laughs)) yield return o;
            foreach (object o in EncWait(6, () => chalkAcks > acks)) yield return o;
            encInfo["chalkles-laughed"] = (chalkLaughs > laughs) ? "true" : "false";
            encInfo["chalkles-client-acknowledged-lock"] = (chalkAcks > acks) ? "true" : "false";
        }

        // The Test: the client's real gaze report reaches the host, which freezes the NPCs although the host is not looking.
        IEnumerator EncTheTest()
        {
            var ec = EncEc(); int reports = testReports, holds = testRemoteHolds; bool froze = false;
            var test = EncSpawn<LookAtGuy>(ec);
            if (test == null) yield break;
            int index = Array.IndexOf(npcs, (NPC)test);
            EncSend("thetest-prep", true, encArena, -encDir, true, -1, 0);
            foreach (object o in EncWait(15, () => EncClientNear(encArena, 3))) yield return o;
            foreach (object o in EncWait(1, null)) yield return o;
            Cell target = EncCellAhead(ec, 2);
            EncUse(test, target.CenterWorldPosition, -encDir, false);
            if (!(test.behaviorStateMachine.CurrentState is LookAtGuy_Active)) test.behaviorStateMachine.ChangeState(new LookAtGuy_Active(test));
            foreach (object o in EncWait(1.5f, null)) yield return o;
            EncSend("thetest", true, encArena, encDir, true, index, 0);
            float aimed = Time.realtimeSinceStartup;
            foreach (object o in EncWait(30, () =>
            {
                if (NpcField<bool>(test, "freezing")) froze = true;
                return testReports > reports && testRemoteHolds > holds && froze;
            })) yield return o;
            encInfo["thetest-freeze-seconds"] = EncSeconds(Time.realtimeSinceStartup - aimed);
            EncCheck("host-thetest-client-gaze-reported", testReports > reports);
            EncCheck("host-thetest-remote-gaze-freezes", testRemoteHolds > holds && froze);
            // Hold the freeze long enough for the client to see the mirrored state, then look away.
            foreach (object o in EncWait(5, null)) yield return o;
            EncSend("thetest-away", true, encArena, -encDir, true, -1, 0);
            foreach (object o in EncWait(6, () => !NpcField<bool>(test, "freezing"))) yield return o;
            encInfo["thetest-freeze-released"] = NpcField<bool>(test, "freezing") ? "false" : "true";
            EncPark(test);
        }

        // Items: the client's real ItemManager.UseItem is relayed and applied to the host's world.
        IEnumerator EncItems()
        {
            var ec = EncEc();
            var principal = ec.Npcs.OfType<Principal>().FirstOrDefault(p => p != null) ?? EncSpawn<Principal>(ec);
            if (principal != null && !encActiveNpcs.Contains(principal)) EncPark(principal);
            var items = new[]
            {
                new { Name = "bsoda", Item = Items.Bsoda, Kind = ItemSpray },
                new { Name = "alarm-clock", Item = Items.AlarmClock, Kind = ItemClock },
                new { Name = "whistle", Item = Items.PrincipalWhistle, Kind = ItemWhistle }
            };
            foreach (var entry in items)
            {
                int relays = itemRelays, rejects = itemRejects; bool seen = false;
                byte kind = entry.Kind;
                EncSend("item-" + entry.Name, true, encArena, encDir, true, -1, (int)entry.Item);
                foreach (object o in EncWait(15, () =>
                {
                    if (kind == ItemWhistle)
                        seen = itemRelays > relays && (principal == null || ec.Npcs.OfType<Principal>().Any(p => p != null && p.behaviorStateMachine.CurrentState is Principal_WhistleApproach));
                    else seen |= hostItemEffects.Any(e => e.Relayed && e.Owner == encClient && e.Kind == kind && e.Obj != null);
                    return seen;
                })) yield return o;
                EncCheck("host-item-" + entry.Name + "-relayed", seen);
                encInfo["item-" + entry.Name + "-relays"] = (itemRelays - relays).ToString();
                if (itemRejects > rejects) encInfo["item-" + entry.Name + "-rejected"] = itemRejectReason;
                foreach (object o in EncWait(2, null)) yield return o;
            }
        }

        // Spectate screen: the host eliminates the client; the client's overlay must name the host it now watches.
        IEnumerator EncSpectate()
        {
            EncSend("spectate", true, encArena, encDir, false, -1, 0);
            foreach (object o in EncWait(3, null)) yield return o;
            var baldi = npcs.OfType<Baldi>().FirstOrDefault(b => b != null);
            EliminatePeer(encClient, baldi);
            Peer peer = EncPeer();
            bool eliminated = peer != null && peer.Eliminated;
            EncCheck("host-client-eliminated-host-alive", eliminated && !LocalEliminated && !deathSent);
            // Stay alive and broadcasting while the client inspects its overlay.
            foreach (object o in EncWait(20, null)) yield return o;
            EncCheck("host-client-eliminated-host-alive", eliminated && !LocalEliminated && !deathSent);
        }

        // ---------------------------------------------------------------- client

        EncounterCommand EncReadCommand()
        {
            string path = EncCommandPath;
            if (!File.Exists(path)) return null;
            string payload;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var reader = new StreamReader(file)) payload = reader.ReadToEnd();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            if (string.IsNullOrEmpty(payload)) return null;
            try { return JsonUtility.FromJson<EncounterCommand>(payload); }
            catch (ArgumentException) { return null; }
        }

        void EncClientBaseline()
        {
            encBase["gum"] = beansGumApplied; encBase["craftersSeq"] = craftersSendSeq;
            encBase["pompAssigns"] = pompOwnerAssigns; encBase["pompPoints"] = pompOwnerPoints;
            encBase["reflexStarts"] = reflexLocalStarts; encBase["reflexClicks"] = reflexLocalClicks; encBase["reflexPasses"] = reflexLocalPasses;
            encBase["testSeq"] = testSeq; encBase["itemToken"] = clientItemToken;
        }

        int EncBase(string key) { int value; return encBase.TryGetValue(key, out value) ? value : 0; }

        void EncClientFrame(float now)
        {
            if (Hosting || !Running || !worldReady || !readyAcknowledged) return;
            if (!encStarted)
            {
                encStarted = true; encRole = "client"; encEpoch = epoch; encStartedAt = now;
                foreach (string name in EncClientChecks) encChecks[name] = false;
                EncCheck("client-ready-acknowledged", true);
                EncClientBaseline();
                EncLog("client scenario started, epoch " + epoch);
            }
            if (epoch != encEpoch) { EncNote("the run moved to another floor"); EncFinish(); return; }
            var core = Singleton<CoreGameManager>.Instance; var manager = Singleton<BaseGameManager>.Instance;
            var player = core == null ? null : core.GetPlayer(0);
            if (player == null || manager == null || manager.Ec == null) return;
            if (now >= encNextRead) { encNextRead = now + .1f; var read = EncReadCommand(); if (read != null) encClientCommand = read; }
            var command = encClientCommand;
            if (command != null && command.Seq != encClientSeq) EncClientApply(command, player);
            if (command != null) EncClientStep(command, player, manager.Ec, now);
            if (now >= encNextWrite) { encNextWrite = now + 2; EncWriteResults(false); }
            if (encDoneAt > 0 && now >= encDoneAt) EncFinish();
            else if (now - encStartedAt > 900) { EncNote("client gave up after 15 minutes"); EncFinish(); }
        }

        void EncClientApply(EncounterCommand command, PlayerManager player)
        {
            encClientSeq = command.Seq;
            if (command.Step != encClientStep)
            {
                encClientStep = command.Step; encStep = command.Step;
                EncClientBaseline();
                encPompMoved = encItemDone = encItemConsumed = false; encReflexOpenAt = -1;
                EncLog("step " + command.Step);
                EncWriteResults(false);
            }
            if (LocalEliminated) return;
            if (command.Teleport) { player.Teleport(command.Position); encHold = command.Position; encHolding = true; }
            else encHolding = false;
            if (command.Face.sqrMagnitude > .01f) EncFace(player, command.Face);
            if (command.Item > 0) { encItemAt = Time.realtimeSinceStartup + .5f; encItemDone = false; }
        }

        void EncClientStep(EncounterCommand command, PlayerManager player, EnvironmentController ec, float now)
        {
            string step = command.Step ?? "";
            NPC target = command.Look >= 0 && command.Look < npcs.Length ? npcs[command.Look] : null;
            if (!LocalEliminated)
            {
                if (encHolding && Flat(player.transform.position, encHold) > 2.5f) player.Teleport(encHold);
                if (command.Aim)
                {
                    Vector3 direction = target != null ? target.transform.position - player.transform.position : command.Face;
                    EncFace(player, direction);
                }
            }
            if (step == "arena") EncLatch("client-reached-arena", Flat(player.transform.position, command.Position) < 3);
            else if (step == "beans") EncLatch("client-beans-native-gum-applied", beansGumApplied > EncBase("gum"));
            else if (step == "crafters")
            {
                bool looking;
                EncLatch("client-crafters-gaze-reported", target is ArtsAndCrafters && craftersSendSeq > EncBase("craftersSeq")
                    && craftersLastReport.TryGetValue(command.Look, out looking) && looking);
            }
            else if (step == "pomp")
            {
                if (!encPompMoved && !LocalEliminated && pompLocal.Count > 0 && pompLocal[0].Room != null)
                {
                    // The owner walks into its appointment room; its own PompSync then reports the arrival.
                    RoomController room = pompLocal[0].Room;
                    Cell cell = room.RandomEntitySafeCellNoGarbage();
                    Vector3 at = cell != null ? cell.CenterWorldPosition : ec.RealRoomMid(room);
                    player.Teleport(at); encHold = at; encHolding = true; encPompMoved = true;
                    encInfo["pomp-room"] = room.name;
                }
                EncLatch("client-pomp-appointment-received", pompOwnerAssigns > EncBase("pompAssigns"));
                EncLatch("client-pomp-reward-received", pompOwnerPoints > EncBase("pompPoints"));
            }
            else if (step == "reflex")
            {
                if (localReflex != null && localReflex.Open && localReflex.Npc != null && !LocalEliminated)
                {
                    if (encReflexOpenAt < 0) encReflexOpenAt = now;
                    bool hinted = command.ReflexSerial > encReflexClicked && (command.ReflexSide == (int)Direction.East || command.ReflexSide == (int)Direction.West);
                    if (hinted)
                    {
                        encReflexClicked = command.ReflexSerial; encReflexHinted++;
                        localReflex.Npc.HotspotClicked((Direction)command.ReflexSide);
                    }
                    else if (now - encReflexOpenAt > .75f)
                    {
                        // No hint in time: guess, as a player would.
                        encReflexClicked = Math.Max(encReflexClicked, command.ReflexSerial); encReflexGuessed++;
                        localReflex.Npc.HotspotClicked(Direction.East);
                    }
                }
                else encReflexOpenAt = -1;
                encInfo["reflex-hinted-clicks"] = encReflexHinted.ToString(); encInfo["reflex-guessed-clicks"] = encReflexGuessed.ToString();
                EncLatch("client-reflex-test-started", reflexLocalStarts > EncBase("reflexStarts"));
                EncLatch("client-reflex-click-sent", reflexLocalClicks > EncBase("reflexClicks"));
                EncLatch("client-reflex-pass-received", reflexLocalPasses > EncBase("reflexPasses"));
            }
            else if (step == "chalkles")
            {
                var face = target as ChalkFace;
                EncLatch("client-chalkles-boards-built", face != null && BoardsOf(face).Count > 0);
                EncLatch("client-chalkles-charge-mirrored", face != null && (float)chalkChargeField.GetValue(face) > 0);
                if (chalkTrapped > 0) encInfo["chalkles-trapped"] = chalkTrapped.ToString();
            }
            else if (step == "thetest")
            {
                var replica = target as LookAtGuy;
                EncLatch("client-thetest-gaze-reported", replica != null && testSeq > EncBase("testSeq") && testEntries.Any(e => e.Index == command.Look && e.Looking));
                EncLatch("client-thetest-freeze-mirrored", replica != null && (NpcField<bool>(replica, "freezing") || testMirrorFrozen.Contains(replica)));
            }
            else if (step.StartsWith("item-", StringComparison.Ordinal))
            {
                if (command.Item > 0 && !encItemDone && now >= encItemAt && !LocalEliminated)
                {
                    encItemDone = true;
                    ItemObject item = Resources.FindObjectsOfTypeAll<ItemObject>().FirstOrDefault(o => o != null && o.itemType == (Items)command.Item && o.addToInventory);
                    if (item == null) EncNote("item " + (Items)command.Item + " not found");
                    else
                    {
                        // A real use: the item goes into the selected slot and ItemManager.UseItem instantiates and uses it.
                        var inventory = player.itm;
                        inventory.ClearItems(); inventory.selectedItem = 0; inventory.AddItem(item);
                        bool held = inventory.items[0] != null && inventory.items[0].itemType == item.itemType;
                        inventory.UseItem();
                        encItemConsumed = held && inventory.items[0] != null && inventory.items[0].itemType == Items.None;
                    }
                }
                EncLatch("client-" + step + "-used", encItemDone && encItemConsumed && clientItemToken > EncBase("itemToken"));
            }
            else if (step == "spectate")
            {
                bool shown = LocalEliminated && spectatorUiState == "watching" && spectatorUiRoot != null && spectatorUiRoot.activeSelf
                    && spectatorUiName != null && !string.IsNullOrEmpty(command.HostName) && spectatorUiName.text == command.HostName && spectatorTarget == 0;
                encInfo["spectator-ui-state"] = spectatorUiState;
                encInfo["spectator-ui-name"] = spectatorUiName == null ? "" : spectatorUiName.text;
                if (shown && encDoneAt < 0) encDoneAt = now + 2;
                EncLatch("client-spectate-overlay-shows-host", shown);
            }
            else if (step == "done" && encDoneAt < 0) encDoneAt = now + 1;
        }
    }
}
