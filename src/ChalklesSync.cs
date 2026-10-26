using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    // ChalkFace multiplayer sync. Owned by one feature; called from NpcFeatures.cs.
    // Ops: ChalklesToClient = 80 (host to clients), ChalklesToHost = 81 (client to host).
    //
    // Native behaviour (ChalkFace, ChalkFace_*, Chalkboard): Initialize places boards on random class/faculty
    // walls (UnityEngine.Random plus a time-seeded System.Random for the wall), so each machine would pick
    // different boards. A Chalkboard room function teleports Chalkles to its board when a player enters,
    // the Charging state raises `charge` (shader _PercentInvisible = 1 - charge/setTime) and, when full,
    // Laughing shuts and time-locks every door of the room, makes noise and circles the room; any exit cancels.
    //
    // Multiplayer: the host records its canonical boards (cell + wall) and sends them in the world section;
    // clients skip the native random placement and build the same boards. The host counts remote players'
    // validated poses as room occupants, chooses the trapped targets when laughter starts and broadcasts a
    // one-time Laugh command; every client shuts/locks the room doors natively at once (the shared door
    // state then keeps them) and target owners acknowledge. Charge and state ride the world section.
    public sealed partial class TogetherRuntime
    {
        const byte ChalkLaugh = 1, ChalkAck = 1;
        sealed class ChalkBoardInfo { public ChalkFace Face; public IntVector2 Cell; public Direction Dir; }

        // Boards are recorded on whichever machine spawned them (native host spawn or client replica build).
        static readonly Dictionary<ChalkFace, List<Chalkboard>> chalkBoards = new Dictionary<ChalkFace, List<Chalkboard>>();
        static readonly Dictionary<Chalkboard, ChalkBoardInfo> chalkBoardInfo = new Dictionary<Chalkboard, ChalkBoardInfo>();
        static readonly FieldInfo chalkChargeField = AccessTools.Field(typeof(ChalkFace), "charge");
        static readonly FieldInfo chalkSetTimeField = AccessTools.Field(typeof(ChalkFace), "setTime");
        static readonly FieldInfo chalkSoundField = AccessTools.Field(typeof(ChalkFace), "chargeSoundPlaying");
        static readonly MethodInfo chalkUpdateSprite = AccessTools.Method(typeof(ChalkFace), "UpdateSprite");
        static readonly FieldInfo boardFaceField = AccessTools.Field(typeof(Chalkboard), "chalkFace");
        static readonly FieldInfo boardVisualField = AccessTools.Field(typeof(Chalkboard), "chalkboard");
        static readonly FieldInfo boardPlayersField = AccessTools.Field(typeof(Chalkboard), "players");
        static readonly FieldInfo roomFunctionRoom = AccessTools.Field(typeof(RoomFunction), "room");

        readonly Dictionary<Chalkboard, HashSet<int>> chalkOccupants = new Dictionary<Chalkboard, HashSet<int>>();
        readonly Dictionary<int, HashSet<int>> chalkPendingAcks = new Dictionary<int, HashSet<int>>();
        readonly HashSet<int> chalkSeenTokens = new HashSet<int>();
        readonly Dictionary<ChalkFace, byte> chalkShownState = new Dictionary<ChalkFace, byte>();
        int[] chalkLastTargets = new int[0];
        int chalkToken, chalkRemoteEnters, chalkLaughs, chalkTrapped, chalkAcks, chalkBoardsBuilt;

        // ---- native hooks -------------------------------------------------------------------------

        internal static void RecordChalkboard(Chalkboard board, Cell tile, ChalkFace face)
        {
            if (board == null || tile == null || face == null) return;
            PruneChalkboards();
            GameObject visual = boardVisualField.GetValue(board) as GameObject;
            Direction dir = Direction.North;
            if (visual != null)
                for (int d = 0; d < 4; d++)
                    if (Quaternion.Angle(visual.transform.rotation, ((Direction)d).ToRotation()) < 1f) { dir = (Direction)d; break; }
            List<Chalkboard> list;
            if (!chalkBoards.TryGetValue(face, out list)) chalkBoards[face] = list = new List<Chalkboard>();
            list.Add(board);
            chalkBoardInfo[board] = new ChalkBoardInfo { Face = face, Cell = tile.position, Dir = dir };
        }

        static void PruneChalkboards()
        {
            foreach (var face in chalkBoards.Keys.ToArray())
            {
                if (face == null) { chalkBoards.Remove(face); continue; }
                chalkBoards[face].RemoveAll(b => b == null);
                if (chalkBoards[face].Count == 0) chalkBoards.Remove(face);
            }
            foreach (var board in chalkBoardInfo.Keys.ToArray()) if (board == null) chalkBoardInfo.Remove(board);
        }

        static List<Chalkboard> BoardsOf(ChalkFace face)
        {
            List<Chalkboard> list;
            return face != null && chalkBoards.TryGetValue(face, out list) ? list : new List<Chalkboard>();
        }

        static Chalkboard ChalkActiveBoard(ChalkFace face)
        {
            if (face == null || face.state == null) return null;
            if (!(face.state is ChalkFace_Charging) && !(face.state is ChalkFace_Laughing)) return null;
            return NpcField<Chalkboard>(face.state, "chalkboard");
        }

        static byte ChalkStateCode(ChalkFace face)
        {
            if (face == null) return 0;
            if (face.state is ChalkFace_Laughing) return 2;
            if (face.state is ChalkFace_Charging) return 1;
            return 0;
        }

        // Host: the local player left a board room natively (which always cancels); resume if teammates remain.
        internal void ChalkLocalExit(Chalkboard board)
        {
            if (!Active || !Hosting || board == null) return;
            ChalkFace face = boardFaceField.GetValue(board) as ChalkFace;
            HashSet<int> occupants;
            if (face == null || face.state == null || !chalkOccupants.TryGetValue(board, out occupants) || occupants.Count == 0) return;
            Peer peer;
            if (occupants.All(id => peers.TryGetValue(id, out peer) && !peer.Tagged)) face.state.Teleport(board);
        }

        // Host: laughter started; choose the trapped players and broadcast the one-time lock command.
        internal void ChalkActivated(ChalkFace face, RoomController room)
        {
            if (!Active || !Hosting || !worldReady || face == null) return;
            int index = Array.IndexOf(npcs, face);
            Chalkboard board = ChalkActiveBoard(face);
            List<Chalkboard> boards = BoardsOf(face);
            int boardIndex = board == null ? -1 : boards.IndexOf(board);
            if (index < 0 || boardIndex < 0) return;
            var targets = new List<int>();
            HashSet<int> occupants;
            if (chalkOccupants.TryGetValue(board, out occupants)) targets.AddRange(occupants.OrderBy(id => id));
            var locals = boardPlayersField.GetValue(board) as List<PlayerManager>;
            if (locals != null && locals.Count > 0 && !LocalEliminated) targets.Insert(0, self);
            chalkToken++; chalkLaughs++;
            chalkLastTargets = targets.ToArray();
            foreach (int id in targets)
            {
                if (id == self) continue;
                HashSet<int> pending;
                if (!chalkPendingAcks.TryGetValue(id, out pending)) chalkPendingAcks[id] = pending = new HashSet<int>();
                pending.Add(chalkToken);
            }
            SendAll(ChalkLaughPacket(epoch, chalkToken, index, boardIndex, face.LockTime, chalkLastTargets));
            Log("Chalkles laughter at board " + boardIndex + " token " + chalkToken + " targets " + string.Join(",", chalkLastTargets.Select(t => t.ToString()).ToArray()));
        }

        static byte[] ChalkLaughPacket(int generation, int token, int index, int boardIndex, float lockTime, int[] targets)
        {
            return Wire.Packet(Op.ChalklesToClient, w =>
            {
                w.Write(generation); w.Write(ChalkLaugh); w.Write(token);
                w.Write((ushort)index); w.Write((byte)boardIndex); w.Write(lockTime);
                w.Write((byte)targets.Length); foreach (int id in targets) w.Write(id);
            });
        }

        // ---- fixed interface ----------------------------------------------------------------------

        void ResetChalkles()
        {
            PruneChalkboards();
            chalkOccupants.Clear(); chalkPendingAcks.Clear(); chalkSeenTokens.Clear(); chalkShownState.Clear();
            chalkLastTargets = new int[0];
        }

        void UpdateChalkles()
        {
            if (!Hosting || !worldReady || !Running) return;
            BaseGameManager manager = Singleton<BaseGameManager>.Instance;
            if (manager == null || manager.Ec == null) return;
            EnvironmentController ec = manager.Ec;
            float now = Time.realtimeSinceStartup;
            if (chalkPendingAcks.Count > 0)
            {
                chalkIdScratch.Clear();
                foreach (int id in chalkPendingAcks.Keys) if (!peers.ContainsKey(id)) chalkIdScratch.Add(id);
                for (int i = 0; i < chalkIdScratch.Count; i++) chalkPendingAcks.Remove(chalkIdScratch[i]);
            }
            // Resolve each eligible remote peer's room once per frame; boards then compare room references.
            chalkPeerScratch.Clear(); chalkRoomScratch.Clear();
            foreach (Peer p in peers.Values)
            {
                if (p.Id == self || !p.Accepted || !p.Ready || !p.HasPose || p.Eliminated || p.InLocker || now - p.PoseAt >= 2) continue;
                RoomController room = ChalkPeerRoom(ec, p);
                if (room == null) continue;
                chalkPeerScratch.Add(p); chalkRoomScratch.Add(room);
            }
            for (int n = 0; n < npcs.Length; n++)
            {
                ChalkFace face = npcs[n] as ChalkFace;
                if (face == null || face.state == null) continue;
                List<Chalkboard> boards;
                if (!chalkBoards.TryGetValue(face, out boards)) continue;
                for (int b = 0; b < boards.Count; b++)
                {
                    Chalkboard board = boards[b];
                    if (board == null || board.Room == null) continue;
                    RoomController room = board.Room;
                    HashSet<int> current = chalkCurrentScratch; current.Clear();
                    bool anyTagged = false, anyUntagged = false;
                    for (int i = 0; i < chalkPeerScratch.Count; i++)
                        if (chalkRoomScratch[i] == room)
                        {
                            current.Add(chalkPeerScratch[i].Id);
                            if (chalkPeerScratch[i].Tagged) anyTagged = true; else anyUntagged = true;
                        }
                    HashSet<int> previous;
                    chalkOccupants.TryGetValue(board, out previous);
                    if (previous == null && current.Count == 0) continue;
                    bool exited = false;
                    if (previous != null) foreach (int id in previous) if (!current.Contains(id)) { exited = true; break; }
                    // Native Chalkboard.OnPlayerEnter teleports Chalkles unless the player is tagged.
                    chalkIdScratch.Clear();
                    foreach (int id in current) if (previous == null || !previous.Contains(id)) chalkIdScratch.Add(id);
                    chalkIdScratch.Sort();
                    for (int i = 0; i < chalkIdScratch.Count; i++)
                        if (!peers[chalkIdScratch[i]].Tagged) { chalkRemoteEnters++; face.state.Teleport(board); }
                    if (exited)
                    {
                        // Native exit always cancels; with teammates still in the room charging resumes instead.
                        var locals = boardPlayersField.GetValue(board) as List<PlayerManager>;
                        bool othersRemain = current.Count > 0 || (locals != null && locals.Count > 0);
                        if (!othersRemain) face.state.Cancel();
                        else if (face.state is ChalkFace_Idle && anyUntagged) face.state.Teleport(board);
                    }
                    // Native Chalkboard.Update cancels while any tagged player is inside.
                    if (anyTagged) face.state.Cancel();
                    if (current.Count == 0) chalkOccupants.Remove(board);
                    else if (previous != null) { previous.Clear(); previous.UnionWith(current); }
                    else chalkOccupants[board] = new HashSet<int>(current);
                }
            }
        }

        readonly List<Peer> chalkPeerScratch = new List<Peer>();
        readonly List<RoomController> chalkRoomScratch = new List<RoomController>();
        readonly List<int> chalkIdScratch = new List<int>();
        readonly HashSet<int> chalkCurrentScratch = new HashSet<int>();

        static RoomController ChalkPeerRoom(EnvironmentController ec, Peer peer)
        {
            IntVector2 grid = IntVector2.GetGridPosition(peer.Position);
            if (!ec.ContainsCoordinates(grid)) return null;
            Cell cell = ec.CellFromPosition(grid);
            return cell != null && !cell.Null ? cell.room : null;
        }


        bool HandleChalklesClient(int id, Peer sender, Op op, BinaryReader r)
        {
            if (op != Op.ChalklesToHost) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub != ChalkAck) throw new IOException("Unknown Chalkles command.");
            int token = r.ReadInt32();
            if (generation != epoch || sender == null || !sender.Ready || sender.Eliminated) return true;
            HashSet<int> pending;
            if (chalkPendingAcks.TryGetValue(id, out pending) && pending.Remove(token)) chalkAcks++;
            return true;
        }

        bool HandleChalklesHost(Op op, BinaryReader r)
        {
            if (op != Op.ChalklesToClient) return false;
            int generation = r.ReadInt32(); byte sub = r.ReadByte();
            if (sub != ChalkLaugh) throw new IOException("Unknown Chalkles command.");
            int token = r.ReadInt32(); int index = r.ReadUInt16(); int boardIndex = r.ReadByte(); float lockTime = Wire.Number(r);
            int count = r.ReadByte();
            if (index >= 128 || boardIndex >= 64 || count > 16 || lockTime < 0 || lockTime > 120) throw new IOException("Invalid Chalkles command.");
            var targets = new int[count];
            for (int i = 0; i < count; i++) targets[i] = r.ReadInt32();
            if (generation != epoch || !worldReady || index >= npcs.Length) return true;
            ChalkFace face = npcs[index] as ChalkFace;
            List<Chalkboard> boards = BoardsOf(face);
            if (face == null || boardIndex >= boards.Count || boards[boardIndex] == null) return true;
            if (!chalkSeenTokens.Add(token)) return true;
            Applying = true;
            try
            {
                // ChalkFace.Activate: every door of the room shuts and locks for lockTime.
                foreach (Door door in boards[boardIndex].Room.doors)
                    if (door != null) { door.Shut(); door.LockTimed(lockTime); }
                ShowChalkState(face, 2);
            }
            finally { Applying = false; }
            if (targets.Contains(self))
            {
                chalkTrapped++;
                if (!Hosting) Network.Send(0, Wire.Packet(Op.ChalklesToHost, w => { w.Write(epoch); w.Write(ChalkAck); w.Write(token); }));
            }
            return true;
        }

        void WriteChalklesWorld(BinaryWriter w)
        {
            var faces = new List<KeyValuePair<int, ChalkFace>>();
            for (int i = 0; i < npcs.Length && i < 128; i++)
            {
                ChalkFace face = npcs[i] as ChalkFace;
                if (face != null && faces.Count < 8) faces.Add(new KeyValuePair<int, ChalkFace>(i, face));
            }
            w.Write((byte)faces.Count);
            foreach (var pair in faces)
            {
                ChalkFace face = pair.Value;
                List<Chalkboard> boards = BoardsOf(face).Where(b => b != null && chalkBoardInfo.ContainsKey(b)).Take(64).ToList();
                Chalkboard active = ChalkActiveBoard(face);
                int activeIndex = active == null ? -1 : boards.IndexOf(active);
                w.Write((ushort)pair.Key); w.Write(ChalkStateCode(face)); w.Write((float)chalkChargeField.GetValue(face));
                w.Write(activeIndex < 0 ? (byte)255 : (byte)activeIndex);
                w.Write((byte)boards.Count);
                foreach (Chalkboard board in boards)
                {
                    ChalkBoardInfo info = chalkBoardInfo[board];
                    w.Write((ushort)info.Cell.x); w.Write((ushort)info.Cell.z); w.Write((byte)info.Dir);
                }
            }
        }

        void ReadChalklesWorld(BinaryReader r, bool apply)
        {
            int faceCount = r.ReadByte();
            if (faceCount > 8) throw new IOException("Chalkles limit exceeded.");
            EnvironmentController ec = apply && Singleton<BaseGameManager>.Instance != null ? Singleton<BaseGameManager>.Instance.Ec : null;
            for (int f = 0; f < faceCount; f++)
            {
                int index = r.ReadUInt16(); byte state = r.ReadByte(); float charge = Wire.Number(r); int active = r.ReadByte(); int count = r.ReadByte();
                if (index >= 128 || state > 2 || count > 64 || (active != 255 && active >= count)) throw new IOException("Invalid Chalkles record.");
                var cells = new IntVector2[count]; var dirs = new Direction[count];
                for (int i = 0; i < count; i++)
                {
                    cells[i] = new IntVector2(r.ReadUInt16(), r.ReadUInt16());
                    int dir = r.ReadByte(); if (dir > 3) throw new IOException("Invalid chalkboard wall.");
                    dirs[i] = (Direction)dir;
                }
                if (!apply || ec == null || index >= npcs.Length) continue;
                ChalkFace face = npcs[index] as ChalkFace;
                if (face == null) continue;
                EnsureChalkboards(ec, face, cells, dirs);
                float setTime = (float)chalkSetTimeField.GetValue(face);
                chalkChargeField.SetValue(face, Mathf.Clamp(charge, 0, setTime));
                chalkUpdateSprite.Invoke(face, null);
                ShowChalkState(face, state);
            }
        }

        // Client: build the host's boards in place of the skipped random ChalkFace.SpawnBoard calls.
        void EnsureChalkboards(EnvironmentController ec, ChalkFace face, IntVector2[] cells, Direction[] dirs)
        {
            if (BoardsOf(face).Count > 0 || cells.Length == 0) return;
            for (int i = 0; i < cells.Length; i++)
                if (!ec.ContainsCoordinates(cells[i]) || ec.CellFromPosition(cells[i]).Null || ec.CellFromPosition(cells[i]).room == null)
                    throw new IOException("Chalkboard outside this level.");
            Chalkboard prefab = NpcField<Chalkboard>(face, "chalkboardPre");
            if (prefab == null) throw new IOException("Chalkboard prefab unavailable.");
            for (int i = 0; i < cells.Length; i++)
            {
                Cell cell = ec.CellFromPosition(cells[i]);
                Chalkboard board = UnityEngine.Object.Instantiate(prefab, cell.TileTransform);
                // Chalkboard.Spawn without its time-seeded wall choice.
                boardFaceField.SetValue(board, face);
                roomFunctionRoom.SetValue(board, cell.room);
                cell.room.functions.AddFunction(board);
                GameObject visual = boardVisualField.GetValue(board) as GameObject;
                visual.transform.parent = cell.TileTransform;
                visual.transform.rotation = dirs[i].ToRotation();
                cell.HardCoverWall(dirs[i], true);
                board.transform.position = ec.RealRoomMid(cell.room) + Vector3.up * 5f;
                RecordChalkboard(board, cell, face);
                chalkBoardsBuilt++;
            }
            Log("Chalkles replica boards built: " + cells.Length);
        }

        // Replica audio follows the native state transitions (AdvanceTimer, Activate, Cancelled).
        void ShowChalkState(ChalkFace face, byte state)
        {
            byte shown;
            if (chalkShownState.TryGetValue(face, out shown) && shown == state) return;
            chalkShownState[face] = state;
            AudioManager audio = NpcField<AudioManager>(face, "audMan");
            if (audio == null) return;
            if (state == 1)
            {
                if (!(bool)chalkSoundField.GetValue(face))
                {
                    audio.QueueAudio(NpcField<SoundObject>(face, "audSpawn"));
                    audio.audioSourceManager.time = (float)chalkChargeField.GetValue(face);
                    chalkSoundField.SetValue(face, true);
                }
            }
            else if (state == 2)
            {
                chalkSoundField.SetValue(face, false);
                audio.QueueAudio(NpcField<SoundObject>(face, "audLaugh"));
                audio.SetLoop(true);
            }
            else
            {
                audio.FlushQueue(true);
                chalkSoundField.SetValue(face, false);
            }
        }

        string ChalklesDiagnostic()
        {
            int boards = chalkBoards.Values.Sum(l => l.Count(b => b != null));
            ChalkFace face = npcs.OfType<ChalkFace>().FirstOrDefault(n => n != null);
            float charge = face == null ? 0 : (float)chalkChargeField.GetValue(face);
            return ",\"chalkBoards\":" + boards + ",\"chalkBoardsBuilt\":" + chalkBoardsBuilt + ",\"chalkRemoteEnters\":" + chalkRemoteEnters
                + ",\"chalkLaughs\":" + chalkLaughs + ",\"chalkTrapped\":" + chalkTrapped + ",\"chalkAcks\":" + chalkAcks
                + ",\"chalkState\":" + ChalkStateCode(face) + ",\"chalkCharge\":" + charge.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- lab tour -----------------------------------------------------------------------------

        bool chalklesLabStarted;
        void UpdateChalklesLab()
        {
            if (!Lab.Enabled || !TitleVisible || chalklesLabStarted || !Lab.HasArg("-bt-chalkles-tour")) return;
            chalklesLabStarted = true; StartCoroutine(TourChalkles());
        }

        bool DeliverChalkHost(byte[] packet)
        {
            using (var reader = new BinaryReader(new MemoryStream(packet))) { reader.ReadByte(); return HandleChalklesHost((Op)packet[0], reader); }
        }

        void DeliverChalkClient(int id, Peer sender, byte[] packet)
        {
            using (var reader = new BinaryReader(new MemoryStream(packet))) { reader.ReadByte(); HandleChalklesClient(id, sender, (Op)packet[0], reader); }
        }

        IEnumerator TourChalkles()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            portText = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(); listener.Stop();
            Connect(true); if (!Active) yield break; AutoStartRun();
            while (!worldReady) yield return null;
            yield return new WaitForSecondsRealtime(1);
            var ec = Singleton<BaseGameManager>.Instance.Ec;
            var player = Singleton<CoreGameManager>.Instance.GetPlayer(0);
            ChalkFace face = ec.Npcs.OfType<ChalkFace>().FirstOrDefault();
            if (face == null)
            {
                var prefab = Resources.FindObjectsOfTypeAll<ChalkFace>().First(n => !n.gameObject.scene.IsValid());
                face = (ChalkFace)ec.SpawnNPC(prefab, ec.CellFromPosition(player.transform.position).position);
            }
            npcs = ec.Npcs.ToArray();
            int index = Array.IndexOf(npcs, face);
            RoomController playerRoom = ec.CellFromPosition(player.transform.position).room;
            List<Chalkboard> boards = BoardsOf(face);
            Chalkboard board = boards.FirstOrDefault(b => b != null && b.Room != null && b.Room != playerRoom);
            CheckNativeUi("canonical-boards-recorded", board != null && boards.All(b => chalkBoardInfo.ContainsKey(b)));
            if (board == null) { WriteLabResults("chalkles-results.json", "Chalkles"); yield break; }
            int boardIndex = boards.IndexOf(board);
            Vector3 inside = ec.CellFromPosition(chalkBoardInfo[board].Cell).FloorWorldPosition;
            float setTime = (float)chalkSetTimeField.GetValue(face);
            face.Cancel(); yield return null;

            var dead = new Peer { Id = 2, Name = "Caught", Accepted = true, Ready = true, HasPose = true, Eliminated = true, Position = inside, PoseAt = Time.realtimeSinceStartup };
            var locker = new Peer { Id = 4, Name = "Locker", Accepted = true, Ready = true, HasPose = true, InLocker = true, Position = inside, PoseAt = Time.realtimeSinceStartup };
            peers[2] = dead; peers[4] = locker; UpdateChalkles();
            CheckNativeUi("excluded-players-no-charge", face.state is ChalkFace_Idle && !chalkOccupants.ContainsKey(board));

            var remote = new Peer { Id = 3, Name = "Chalk teammate", Accepted = true, Ready = true, HasPose = true, Position = inside, PoseAt = Time.realtimeSinceStartup };
            peers[3] = remote; UpdateChalkles();
            CheckNativeUi("remote-occupant-charges", face.state is ChalkFace_Charging && ChalkActiveBoard(face) == board);
            float until = Time.realtimeSinceStartup + 3;
            while (Time.realtimeSinceStartup < until && (float)chalkChargeField.GetValue(face) <= 0)
            { remote.PoseAt = dead.PoseAt = locker.PoseAt = Time.realtimeSinceStartup; yield return null; }
            CheckNativeUi("remote-charge-advances", (float)chalkChargeField.GetValue(face) > 0 && face.state is ChalkFace_Charging);

            // World section round trip: parse-only leaves the replica alone, apply restores charge and shader.
            byte[] section;
            using (var memory = new MemoryStream()) { using (var writer = new BinaryWriter(memory)) WriteChalklesWorld(writer); section = memory.ToArray(); }
            float hostCharge = (float)chalkChargeField.GetValue(face);
            chalkChargeField.SetValue(face, 0f); chalkUpdateSprite.Invoke(face, null);
            bool consumed;
            using (var reader = new BinaryReader(new MemoryStream(section))) { ReadChalklesWorld(reader, false); consumed = reader.BaseStream.Position == section.Length; }
            bool untouched = (float)chalkChargeField.GetValue(face) == 0f;
            using (var reader = new BinaryReader(new MemoryStream(section))) { ReadChalklesWorld(reader, true); consumed &= reader.BaseStream.Position == section.Length; }
            var block = new MaterialPropertyBlock(); NpcField<SpriteRenderer>(face, "chalkRenderer").GetPropertyBlock(block);
            float shader = block.GetFloat("_PercentInvisible");
            CheckNativeUi("world-round-trip", consumed && untouched && Mathf.Abs((float)chalkChargeField.GetValue(face) - hostCharge) < .001f
                && Mathf.Abs(shader - (1 - hostCharge / setTime)) < .001f && BoardsOf(face).Count == boards.Count);

            remote.Position = player.transform.position; remote.PoseAt = Time.realtimeSinceStartup; UpdateChalkles();
            CheckNativeUi("remote-exit-cancels", face.state is ChalkFace_Idle && !chalkOccupants.ContainsKey(board));

            remote.Position = inside; remote.PoseAt = Time.realtimeSinceStartup; UpdateChalkles();
            int laughsBefore = chalkLaughs;
            chalkChargeField.SetValue(face, setTime);
            until = Time.realtimeSinceStartup + 2;
            while (Time.realtimeSinceStartup < until && !(face.state is ChalkFace_Laughing))
            { remote.PoseAt = dead.PoseAt = locker.PoseAt = Time.realtimeSinceStartup; yield return null; }
            int hostToken = chalkToken;
            HashSet<int> pending;
            CheckNativeUi("laugh-targets-remote", face.state is ChalkFace_Laughing && chalkLaughs == laughsBefore + 1 && chalkLastTargets.SequenceEqual(new[] { 3 })
                && chalkPendingAcks.TryGetValue(3, out pending) && pending.Contains(hostToken));
            CheckNativeUi("room-doors-locked", board.Room.doors.Count > 0 && board.Room.doors.All(d => d.locked && !d.open));

            // Owner side: the trap command is applied natively exactly once, stale generations never.
            foreach (Door door in board.Room.doors) door.Unlock();
            int trappedBefore = chalkTrapped;
            byte[] laugh = ChalkLaughPacket(epoch, 900001, index, boardIndex, face.LockTime, new[] { self });
            DeliverChalkHost(laugh);
            bool lockedOnce = board.Room.doors.All(d => d.locked && !d.open);
            DeliverChalkHost(laugh);
            CheckNativeUi("owner-applies-once", lockedOnce && chalkTrapped == trappedBefore + 1);
            foreach (Door door in board.Room.doors) door.Unlock();
            DeliverChalkHost(ChalkLaughPacket(epoch - 1, 900002, index, boardIndex, face.LockTime, new[] { self }));
            CheckNativeUi("stale-epoch-ignored", chalkTrapped == trappedBefore + 1 && board.Room.doors.All(d => !d.locked));

            int acksBefore = chalkAcks;
            byte[] ack = Wire.Packet(Op.ChalklesToHost, w => { w.Write(epoch); w.Write(ChalkAck); w.Write(hostToken); });
            DeliverChalkClient(2, dead, ack); DeliverChalkClient(3, remote, ack); DeliverChalkClient(3, remote, ack);
            CheckNativeUi("ack-token-once", chalkAcks == acksBefore + 1);

            bool rejected = false;
            try { DeliverChalkHost(ChalkLaughPacket(epoch, 900003, index, 200, face.LockTime, new[] { self })); }
            catch (IOException) { rejected = true; }
            CheckNativeUi("malformed-rejected", rejected && chalkTrapped == trappedBefore + 1);

            chalkPendingAcks[3] = new HashSet<int> { 777 };
            peers.Remove(3); UpdateChalkles();
            CheckNativeUi("disconnect-cleanup", !chalkOccupants.ContainsKey(board) && !chalkPendingAcks.ContainsKey(3) && face.state is ChalkFace_Idle);

            peers.Remove(2); peers.Remove(4);
            foreach (Door door in board.Room.doors) door.Unlock();
            ResetChalkles();
            CheckNativeUi("reset-clears-state", chalkOccupants.Count == 0 && chalkPendingAcks.Count == 0 && chalkSeenTokens.Count == 0 && chalkShownState.Count == 0
                && BoardsOf(face).Count == boards.Count && board.Room.doors.All(d => !d.locked));
            CheckNativeUi("host-native-gates", ChalkSpawnBoardGate.Prefix() && ChalkboardEnterGate.Prefix() && ChalkboardUpdateGate.Prefix());
            WriteLabResults("chalkles-results.json", "Chalkles");
        }
    }

    // Host (and single player) record each natively spawned board so clients can rebuild it.
    [HarmonyPatch(typeof(Chalkboard), "Spawn")]
    static class ChalkboardSpawnRecord
    { static void Postfix(Chalkboard __instance, Cell tile, ChalkFace chalkFace) { TogetherRuntime.RecordChalkboard(__instance, tile, chalkFace); } }

    // Clients never place random boards; the host's canonical boards arrive in the world section.
    [HarmonyPatch(typeof(ChalkFace), "SpawnBoard")]
    static class ChalkSpawnBoardGate
    { internal static bool Prefix() { return !ModHooks.ClientWorld; } }

    // Replica boards never drive the replica state machine; the host decides from all players' rooms.
    [HarmonyPatch(typeof(Chalkboard), "OnPlayerEnter")]
    static class ChalkboardEnterGate
    { internal static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(Chalkboard), "OnPlayerExit")]
    static class ChalkboardExitGate
    {
        static bool Prefix() { return !ModHooks.ClientWorld; }
        static void Postfix(Chalkboard __instance) { var r = ModHooks.Current; if (r != null && !ModHooks.ClientWorld) r.ChalkLocalExit(__instance); }
    }

    [HarmonyPatch(typeof(Chalkboard), "Update")]
    static class ChalkboardUpdateGate
    { internal static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(Chalkboard), "OnActivityProgress")]
    static class ChalkboardProgressGate
    { static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(Chalkboard), "OnActivityCompletion")]
    static class ChalkboardCompletionGate
    { static bool Prefix() { return !ModHooks.ClientWorld; } }

    [HarmonyPatch(typeof(ChalkFace), "Activate")]
    static class ChalkActivateTargets
    { static void Postfix(ChalkFace __instance, RoomController room) { var r = ModHooks.Current; if (r != null) r.ChalkActivated(__instance, room); } }
}
