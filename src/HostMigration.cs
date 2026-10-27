using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        sealed class MigrationMember
        {
            public int Id;
            public string Identity, Name;
            public string[] Addresses;
            public bool Eliminated;
            public byte Colour;
        }
        readonly Dictionary<int, MigrationMember> migrationMembers = new Dictionary<int, MigrationMember>();
        readonly Dictionary<string, MigrationMember> migrationExpected = new Dictionary<string, MigrationMember>();
        readonly HashSet<string> migrationDeaths = new HashSet<string>();
        readonly string migrationIdentity = Guid.NewGuid().ToString("N");
        string migrationRoom, migrationTargetIdentity;
        string[] migrationAddresses = new string[0];
        int migrationTerm, migrationPort, migrationAddressIndex;
        bool migrating, migrationHostWaiting, migrationRestart, migrationHadRound, migrationHandingOver;
        float migrationDeadline, migrationRetryAt, migrationPublishAt;
        float migrationLabHandoverAt;
        bool migrationLabPrepared, migrationLabWritten;
        bool migrationFloorLabStarted, migrationFloorLabWritten;
        int migrationOriginalSeed, migrationOriginalEpoch;
        string migrationOriginalScene, migrationOriginalWorldHash;

        // ResetWorld must retain capture state during this controlled same-floor reload.
        bool PreserveMigrationEliminations { get { return migrationRestart; } }
        bool MigrationInProgress { get { return migrating; } }
        bool AwaitingMigrationFloor {get{return migrating||migrationRestart;}}

        string[] MigrationLocalAddresses()
        {
            var result = new List<string>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                    if (adapter.OperationalStatus == OperationalStatus.Up)
                        foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                            if (entry.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(entry.Address))
                                result.Add(entry.Address.ToString());
            }
            catch (NetworkInformationException) { }
            // The loopback address is for explicitly local developer sessions only.
            if (Lab.Enabled && (Address == "127.0.0.1" || Address == "localhost")) result.Insert(0, "127.0.0.1");
            return result.Distinct().Take(6).ToArray();
        }

        void InitializeMigration(bool host)
        {
            ResetMigration();
            if (!host) return;
            migrationRoom = Guid.NewGuid().ToString("N");
            migrationMembers[0] = new MigrationMember { Id = 0, Identity = migrationIdentity, Name = Name, Addresses = MigrationLocalAddresses() };
        }

        void ResetMigration()
        {
            migrating = migrationHostWaiting = migrationRestart = migrationHandingOver = false;
            migrationMembers.Clear(); migrationExpected.Clear(); migrationDeaths.Clear();
            migrationRoom = migrationTargetIdentity = null; migrationTerm = 0;
            migrationPublishAt = 0;
        }

        void WriteMigrationIdentity(BinaryWriter writer)
        {
            Wire.Text(writer, migrationIdentity);
            var addresses = MigrationLocalAddresses();
            writer.Write((byte)addresses.Length);
            foreach (string address in addresses) Wire.Text(writer, address);
            writer.Write(PlayerColour);
        }

        static MigrationMember ReadMigrationIdentity(BinaryReader reader, int id, string name)
        {
            string identity = Wire.Text(reader, 32);
            Guid unused;
            if (identity.Length != 32 || !Guid.TryParseExact(identity, "N", out unused)) throw new IOException("Invalid migration identity.");
            int count = reader.ReadByte();
            if (count > 6) throw new IOException("Too many migration addresses.");
            string[] addresses = new string[count];
            for (int i = 0; i < count; i++)
            {
                IPAddress address;
                addresses[i] = Wire.Text(reader, 45);
                if (!IPAddress.TryParse(addresses[i], out address) || address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
                    throw new IOException("Invalid migration address.");
            }
            byte colour = (byte)PlayerSpriteSheet.ClampColour(reader.ReadByte());
            return new MigrationMember { Id = id, Identity = identity, Name = name, Addresses = addresses.Distinct().ToArray(), Colour = colour };
        }

        void ReadInitialMigrationIdentity(int id, Peer sender, BinaryReader reader)
        {
            var member = ReadMigrationIdentity(reader, id, sender.Name);
            if (migrationMembers.Values.Any(p => p.Id != id && p.Identity == member.Identity)) throw new IOException("Duplicate migration identity.");
            migrationMembers[id] = member;
        }

        void PublishMigrationPlan()
        {
            if (!Hosting || migrating || migrationHandingOver || string.IsNullOrEmpty(migrationRoom)) return;
            foreach (int id in migrationMembers.Keys.Where(id => !peers.ContainsKey(id)).ToArray()) migrationMembers.Remove(id);
            foreach (var member in migrationMembers.Values)
            {
                Peer peer;
                if (peers.TryGetValue(member.Id, out peer)) { member.Name = peer.Name; member.Eliminated = peer.Eliminated; }
            }
            SendAll(Wire.Packet(Op.MigrationPlan, w =>
            {
                Wire.Text(w, migrationRoom); w.Write(migrationTerm); w.Write(Port);
                var members = migrationMembers.Values.Where(p => peers.ContainsKey(p.Id) && peers[p.Id].Accepted).OrderBy(p => p.Id).ToArray();
                w.Write((byte)members.Length);
                foreach (var member in members)
                {
                    w.Write(member.Id); Wire.Text(w, member.Identity); Wire.Text(w, member.Name); w.Write(member.Eliminated);
                    w.Write((byte)member.Addresses.Length);
                    foreach (string address in member.Addresses) Wire.Text(w, address);
                }
            }));
        }

        bool HandleMigrationHost(Op op, BinaryReader reader)
        {
            if (op == Op.Reject && migrating && !migrationHostWaiting)
            {
                // During same-machine handover the old listener can briefly accept
                // a reconnect before it releases the port. Retry the fixed candidate.
                string reason = Wire.Text(reader, 180);
                Log("Migration reconnect will retry: " + reason);
                if (Network != null) Network.Dispose(); Network = null;
                migrationRetryAt = Time.realtimeSinceStartup + .5f;
                return true;
            }
            if (op == Op.MigrationPlan)
            {
                string room = Wire.Text(reader, 32); int term = reader.ReadInt32(), port = reader.ReadInt32(), count = reader.ReadByte();
                Guid unused;
                if (!Guid.TryParseExact(room, "N", out unused) || term < migrationTerm || port < 1024 || port > 65535 || count < 1 || count > 4) throw new IOException("Invalid migration plan.");
                var members = new Dictionary<int, MigrationMember>();
                var identities = new HashSet<string>();
                for (int i = 0; i < count; i++)
                {
                    int id = reader.ReadInt32(); string identity = Wire.Text(reader, 32), name = Wire.Text(reader, 24); bool dead = reader.ReadBoolean();
                    int n = reader.ReadByte();
                    if (id < 0 || id > 3 || members.ContainsKey(id) || !identities.Add(identity) || !Guid.TryParseExact(identity, "N", out unused) || n > 6) throw new IOException("Invalid migration member.");
                    string[] addresses = new string[n];
                    for (int a = 0; a < n; a++)
                    {
                        IPAddress parsed; addresses[a] = Wire.Text(reader, 45);
                        if (!IPAddress.TryParse(addresses[a], out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.Broadcast)) throw new IOException("Invalid migration address.");
                    }
                    members.Add(id, new MigrationMember { Id = id, Identity = identity, Name = name, Addresses = addresses, Eliminated = dead });
                }
                if (!members.ContainsKey(0) || !members.Values.Any(m => m.Identity == migrationIdentity)) throw new IOException("Migration plan omits this player.");
                migrationRoom = room; migrationTerm = term; migrationPort = port;
                migrationMembers.Clear(); foreach (var pair in members) migrationMembers.Add(pair.Key, pair.Value);
                return true;
            }
            if (op == Op.MigrationResume)
            {
                string room = Wire.Text(reader, 32); int term = reader.ReadInt32();
                if (room != migrationRoom || term != migrationTerm + 1) throw new IOException("Invalid handover.");
                BeginHostMigration();
                return true;
            }
            return false;
        }

        // Both EOF and explicit host handover use exactly the same one-candidate election.
        bool BeginHostMigration()
        {
            if (migrating) return true;
            if (Hosting || string.IsNullOrEmpty(migrationRoom)) return false;
            var successor = migrationMembers.Values.Where(m => m.Id != 0 && m.Addresses.Length > 0).OrderBy(m => m.Id).FirstOrDefault();
            if (successor == null) return false;
            migrationExpected.Clear(); migrationDeaths.Clear();
            foreach (var member in migrationMembers.Values.Where(m => m.Id != 0))
            {
                migrationExpected[member.Identity] = member;
                Peer peer;
                if (member.Eliminated || (peers.TryGetValue(member.Id, out peer) && peer.Eliminated)) migrationDeaths.Add(member.Identity);
            }
            migrationTargetIdentity = successor.Identity; migrationAddresses = successor.Addresses; migrationAddressIndex = 0;
            migrationOriginalSeed = Seed; migrationOriginalEpoch = epoch;
            migrationOriginalScene = sceneName; migrationOriginalWorldHash = worldHash;
            migrationTerm++; migrating = true; migrationHostWaiting = migrationTargetIdentity == migrationIdentity;
            migrationHadRound = Running; migrationRestart = Running;
            migrationDeadline = Time.realtimeSinceStartup + 18; migrationRetryAt = Time.realtimeSinceStartup + .6f; levelStartedAt = Time.realtimeSinceStartup;
            Network.Dispose(); Network = null;
            foreach (var peer in peers.Values) DestroyAvatar(peer);
            peers.Clear(); self = -1;
            if (migrationHadRound)
            {
                worldReady = false; readyAcknowledged = false;
                Time.timeScale = 0;
            }
            Tell(migrationHostWaiting ? "Host left. Taking over the room..." : "Host left. Reconnecting to " + successor.Name + "...");
            if (migrationHostWaiting)
            {
                StartMigrationListener();
            }
            return true;
        }

        void StartMigrationListener()
        {
            try
            {
                Network = new Transport(); Network.Host(migrationPort, false); Port = migrationPort; self = 0;
                var me = migrationExpected[migrationIdentity];
                peers[0] = new Peer { Id = 0, Name = Name, Accepted = true, ModVersion = ModVersion, GameHash = gameHash, Colour = PlayerColour, Eliminated = migrationDeaths.Contains(migrationIdentity) };
                migrationMembers.Clear(); me.Id = 0; migrationMembers[0] = me;
            }
            catch (Exception ex)
            {
                if (Network != null) Network.Dispose(); Network = null;
                migrationRetryAt = Time.realtimeSinceStartup + .5f;
                Log("Migration listener will retry: " + ex.Message);
            }
        }

        // Called before Update's !Active return so retry gaps continue progressing.
        bool UpdateMigration()
        {
            if (migrationHandingOver) return true;
            float now = Time.realtimeSinceStartup;
            if (!migrating)
            {
                if (Hosting && now >= migrationPublishAt) { migrationPublishAt = now + 1; PublishMigrationPlan(); }
                UpdateMigrationLab(now);
                WriteMigrationFloorLabResult();
                return false;
            }
            if (migrationHostWaiting)
            {
                if (Network == null)
                {
                    if (now > migrationDeadline) { Disconnect("Host migration could not open the room's port."); return true; }
                    if (now >= migrationRetryAt) StartMigrationListener();
                    return true;
                }
                int accepted = peers.Values.Count(p => p.Accepted);
                if (accepted < migrationExpected.Count && now > migrationDeadline && accepted <= 1 && migrationExpected.Count > 1)
                { Disconnect("No other players followed the host migration. The original host may still be running."); return true; }
                if (accepted >= migrationExpected.Count || now > migrationDeadline)
                {
                    migrating = migrationHostWaiting = false;
                    Tell("Host migration complete." + (migrationHadRound ? " Restarting this floor with the same seed; caught players remain spectators." : ""));
                    BroadcastRoster(); PublishMigrationPlan();
                    if (migrationHadRound) RestartMigratedFloor();
                    WriteMigrationLabResult(true);
                }
                return migrating;
            }
            if (now > migrationDeadline) { Disconnect("Host migration timed out. The selected host could not be reached; return to the lobby to create a room."); return true; }
            if (Network == null && now >= migrationRetryAt)
            {
                Address = migrationAddresses[migrationAddressIndex++ % migrationAddresses.Length];
                Network = new Transport(); Network.Join(Address, migrationPort); migrationRetryAt = now + 1;
            }
            return true;
        }

        bool HandleMigrationConnectionEvent(NetworkEvent e)
        {
            if (migrationHandingOver && e.Kind == "disconnected") return true;
            if (!migrating || migrationHostWaiting) return false;
            if (e.Kind == "error" || e.Kind == "disconnected")
            { if (Network != null) Network.Dispose(); Network = null; migrationRetryAt = Time.realtimeSinceStartup + .5f; return true; }
            if (e.Kind == "connected")
            {
                Network.Send(0, Wire.Packet(Op.MigrationHello, w =>
                {
                    w.Write(Wire.Version); Wire.Text(w, ModVersion); Wire.Text(w, gameHash); Wire.Text(w, Code);
                    Wire.Text(w, migrationRoom); w.Write(migrationTerm); WriteMigrationIdentity(w);
                }));
                return true;
            }
            return false;
        }

        bool HandleMigrationClient(int id, Peer sender, Op op, BinaryReader reader)
        {
            if (op != Op.MigrationHello) return false;
            int version = reader.ReadInt32(); string mod = Wire.Text(reader, 16), hash = Wire.Text(reader, 64), code = Wire.Text(reader, 64);
            string room = Wire.Text(reader, 32); int term = reader.ReadInt32(); var claim = ReadMigrationIdentity(reader, id, "");
            MigrationMember expected;
            if (!migrationHostWaiting || version != Wire.Version || mod != ModVersion || hash != gameHash || code != Code || room != migrationRoom || term != migrationTerm ||
                !migrationExpected.TryGetValue(claim.Identity, out expected) || claim.Identity == migrationIdentity || migrationMembers.Values.Any(m => m.Identity == claim.Identity))
                throw new IOException("Invalid or expired host migration reconnect.");
            sender.Name = expected.Name; sender.Colour = claim.Colour; sender.Accepted = true; sender.ModVersion = ModVersion; sender.GameHash = gameHash; sender.Eliminated = migrationDeaths.Contains(claim.Identity);
            claim.Name = expected.Name; claim.Eliminated = sender.Eliminated; migrationMembers[id] = claim;
            Network.Send(id, Wire.Packet(Op.Welcome, w => { w.Write(id); w.Write(Seed); }));
            BroadcastRoster();
            return true;
        }

        void MigrationWelcomed()
        {
            if (!migrating || migrationHostWaiting) return;
            migrating = false; levelStartedAt = Time.realtimeSinceStartup;
            Peer me;
            if (peers.TryGetValue(self, out me)) me.Eliminated = migrationDeaths.Contains(migrationIdentity);
            Tell("Reconnected to the new host. Waiting for the same floor to restart.");
            WriteMigrationLabResult(false);
        }

        // UI Leave calls this first. Application exit/kill is handled by EOF election.
        bool HandoverHost()
        {
            if (migrationHandingOver) return true;
            if (!Hosting || migrating || peers.Values.Count(p => p.Accepted) < 2) return false;
            if (Network.Steam) return false; // a Steam room has no addresses to hand over: friends get "host left"
            PublishMigrationPlan();
            migrationHandingOver = true;
            byte[] handover = Wire.Packet(Op.MigrationResume, w => { Wire.Text(w, migrationRoom); w.Write(migrationTerm + 1); });
            foreach (var peer in peers.Values.Where(p => p.Accepted && p.Id != 0)) Network.SendAndClose(peer.Id, handover);
            StartCoroutine(FinishHostHandover());
            Tell("Handing the room to another player...");
            return true;
        }

        System.Collections.IEnumerator FinishHostHandover()
        {
            yield return new WaitForSecondsRealtime(.35f);
            Disconnect("Left the room; another player is hosting.");
        }

        void RestartMigratedFloor()
        {
            var core = Singleton<CoreGameManager>.Instance;
            var scene = Resources.FindObjectsOfTypeAll<SceneObject>().FirstOrDefault(s => s.name == sceneName);
            if (core == null || scene == null) { Disconnect("Host migration could not restore this floor."); return; }
            Applying = true;
            try
            {
                core.StopAllCoroutines();
                var manager = Singleton<BaseGameManager>.Instance;
                if (manager != null) { manager.StopAllCoroutines(); if (manager.Ec != null) { manager.Ec.ResetEvents(); manager.Ec.gameObject.SetActive(false); } }
                core.PrepareForReload(); core.SetSeed(Seed); core.sceneObject = scene;
                HostLoad(scene);
                Singleton<AdditiveSceneManager>.Instance.LoadScene("Game");
            }
            catch (Exception ex) { Disconnect("Host migration reload failed: " + ex.Message); }
            finally { Applying = false; }
        }

        // Call only after the migration floor reset is consumed, not on a failed attempt.
        void CompleteMigrationFloorReset() { migrationRestart = false; }

        void RestoreMigrationEliminations()
        {
            foreach (var peer in peers.Values)
            {
                MigrationMember member;
                if ((peer.Id == self && migrationDeaths.Contains(migrationIdentity)) ||
                    (Hosting && migrationMembers.TryGetValue(peer.Id, out member) && migrationDeaths.Contains(member.Identity))) peer.Eliminated = true;
            }
        }

        void ClearMigrationEliminations() { migrationDeaths.Clear(); }

        void UpdateMigrationLab(float now)
        {
            if (!Lab.Enabled || !Lab.HasArg("-bt-migration-tour") || !Hosting || migrationTerm != 0) return;
            bool floor = Lab.HasArg("-bt-migration-floor");
            if (floor && peers.Values.Count(p => p.Accepted) == 3)
            {
                if (!Running && !migrationFloorLabStarted)
                {
                    migrationFloorLabStarted = true;
                    AutoStartRun();
                }
                if (!worldReady || !peers.Values.Where(p => p.Accepted && p.Id != 0).All(p => p.Ready)) return;
            }
            if (!migrationLabPrepared && peers.Values.Count(p => p.Accepted) == 3)
            {
                // A real captured-state bit must survive the reconnect and identity remap.
                var successor = peers.Values.Where(p => p.Id != 0 && p.Accepted).OrderBy(p => p.Id).First();
                if (floor) EliminatePeer(successor.Id, null); else successor.Eliminated = true;
                PublishMigrationPlan();
                migrationLabPrepared = true; migrationLabHandoverAt = now + 2;
            }
            if (migrationLabPrepared && now >= migrationLabHandoverAt)
            {
                migrationLabHandoverAt = float.MaxValue;
                if (Lab.HasArg("-bt-migration-abrupt")) Disconnect("LAB abrupt host departure.");
                else HandoverHost();
            }
        }

        void WriteMigrationLabResult(bool promoted)
        {
            if (!Lab.Enabled || migrationLabWritten || !Lab.HasArg("-bt-migration-tour")) return;
            migrationLabWritten = true;
            bool role = promoted ? Hosting && self == 0 : !Hosting && self > 0;
            bool term = migrationTerm == 1;
            bool identity = promoted ? migrationMembers[0].Identity == migrationIdentity : migrationExpected.ContainsKey(migrationIdentity);
            bool dead = !promoted || peers[0].Eliminated;
            bool quorum = !promoted || peers.Values.Count(p => p.Accepted) == 2;
            bool[] checks = { role, term, identity, dead, quorum };
            int passed = checks.Count(c => c);
            string folder = Path.Combine(Lab.Workspace, "artifacts/tests"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "migration-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json"),
                "{\"passed\":" + passed + ",\"failed\":" + (checks.Length - passed) + ",\"promoted\":" + promoted.ToString().ToLowerInvariant() +
                ",\"role\":" + role.ToString().ToLowerInvariant() + ",\"term\":" + term.ToString().ToLowerInvariant() + ",\"identity\":" + identity.ToString().ToLowerInvariant() +
                ",\"capturedPlayerRetained\":" + dead.ToString().ToLowerInvariant() + ",\"survivorsReconnected\":" + quorum.ToString().ToLowerInvariant() + "}");
        }

        void WriteMigrationFloorLabResult()
        {
            if (!Lab.Enabled || migrationFloorLabWritten || migrationTerm != 1 || !Lab.HasArg("-bt-migration-floor") ||
                !worldReady || !Running || migrating) return;
            var manager = Singleton<BaseGameManager>.Instance;
            if (manager == null || manager.Ec == null || !manager.Ec.Active ||
                (Hosting ? !peers.Values.Where(p => p.Id != self && p.Accepted).All(p => p.Ready) : !readyAcknowledged)) return;
            migrationFloorLabWritten = true;
            bool seed = Seed == migrationOriginalSeed;
            bool scene = sceneName == migrationOriginalScene;
            bool hash = !string.IsNullOrEmpty(migrationOriginalWorldHash) && worldHash == migrationOriginalWorldHash;
            bool generation = epoch == migrationOriginalEpoch + 1;
            bool dead = peers.ContainsKey(0) && peers[0].Eliminated && (Hosting ? LocalEliminated : !LocalEliminated);
            bool survivor = peers.Values.Any(p => p.Accepted && !p.Eliminated && (Hosting ? p.Ready : p.Id == self && readyAcknowledged));
            bool[] checks = { seed, scene, hash, generation, dead, survivor };
            int passed = checks.Count(c => c);
            string folder = Path.Combine(Lab.Workspace, "artifacts/tests"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "migration-floor-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".json"),
                "{\"passed\":" + passed + ",\"failed\":" + (checks.Length - passed) + ",\"hosting\":" + Hosting.ToString().ToLowerInvariant() +
                ",\"sameSeed\":" + seed.ToString().ToLowerInvariant() + ",\"sameScene\":" + scene.ToString().ToLowerInvariant() + ",\"sameWorldHash\":" + hash.ToString().ToLowerInvariant() +
                ",\"epochAdvanced\":" + generation.ToString().ToLowerInvariant() + ",\"caughtPlayerRemainsSpectator\":" + dead.ToString().ToLowerInvariant() +
                ",\"survivorReady\":" + survivor.ToString().ToLowerInvariant() + "}");
        }
    }
}
