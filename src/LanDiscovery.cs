using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TMPro;
using UnityEngine;

namespace BaldiTogether
{
    // Hosts broadcast a small UDP beacon while their room is open; the LAN GAMES page lists them.
    // Everything runs on the main thread with non-blocking sockets. Room codes are never broadcast.
    public sealed partial class TogetherRuntime
    {
        public const int DiscoveryPort = 27016;
        const string DiscoveryMagic = "BTLAN1";
        const int LanPage = 5;

        sealed class LanRoom
        {
            public string Room, Address, Name, Version;
            public int Port, Protocol, Players;
            public bool NeedsCode, Running;
            public float SeenAt;
            public bool Compatible { get { return Protocol == Wire.Version && Version == ModVersion; } }
        }

        readonly Dictionary<string, LanRoom> lanRooms = new Dictionary<string, LanRoom>();
        UdpClient lanBeacon, lanListener;
        IPEndPoint[] lanTargets;
        float nextLanBeacon, lanTargetsAt, lanListenRetryAt;
        string lanStatus = "";
        bool labLanListen;

        void UpdateLanDiscovery()
        {
            float now = Time.realtimeSinceStartup;
            bool advertise = Hosting && !Running && !migrating && !string.IsNullOrEmpty(migrationRoom);
            if (advertise) { if (now >= nextLanBeacon) { nextLanBeacon = now + 1; SendLanBeacon(now); } }
            else if (lanBeacon != null) { lanBeacon.Close(); lanBeacon = null; }
            if ((show && nativePage == LanPage && !Active) || labLanListen) PollLanRooms(now);
            else { CloseLanListener(); }
        }

        void SendLanBeacon(float now)
        {
            try
            {
                if (lanBeacon == null) { lanBeacon = new UdpClient(); lanBeacon.EnableBroadcast = true; }
                if (lanTargets == null || now >= lanTargetsAt) { lanTargets = LanBroadcastTargets(); lanTargetsAt = now + 10; }
                string payload = string.Join("\n", new[] { DiscoveryMagic, Wire.Version.ToString(), ModVersion, migrationRoom, CleanName(Name), Port.ToString(),
                    peers.Values.Count(p => p.Accepted).ToString(), string.IsNullOrEmpty(Code) ? "0" : "1", Running ? "1" : "0" });
                byte[] data = Encoding.UTF8.GetBytes(payload);
                foreach (var target in lanTargets)
                {
                    try { lanBeacon.Send(data, data.Length, target); }
                    catch (SocketException) { }
                }
            }
            catch (Exception ex) { Log("LAN beacon unavailable: " + ex.Message); if (lanBeacon != null) lanBeacon.Close(); lanBeacon = null; nextLanBeacon = now + 10; }
        }

        // Limited broadcast plus each interface's directed broadcast, because Windows sends 255.255.255.255 on one adapter only.
        static IPEndPoint[] LanBroadcastTargets()
        {
            var targets = new List<IPEndPoint> { new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), new IPEndPoint(IPAddress.Loopback, DiscoveryPort) };
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                        byte[] address = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                        if (mask.All(b => b == 0)) continue;
                        for (int i = 0; i < 4; i++) address[i] = (byte)(address[i] | ~mask[i]);
                        var broadcast = new IPEndPoint(new IPAddress(address), DiscoveryPort);
                        if (!targets.Any(t => t.Equals(broadcast))) targets.Add(broadcast);
                    }
                }
            }
            catch (Exception ex) { Log("LAN adapters unavailable: " + ex.Message); }
            return targets.ToArray();
        }

        void PollLanRooms(float now)
        {
            if (lanListener == null)
            {
                if (now < lanListenRetryAt) return;
                try
                {
                    var listener = new UdpClient();
                    // Several game copies on one PC can browse at once.
                    listener.ExclusiveAddressUse = false;
                    listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                    lanListener = listener; lanStatus = "Searching your network for rooms...";
                }
                catch (Exception ex) { lanStatus = "LAN search unavailable: " + ex.Message; lanListenRetryAt = now + 3; return; }
            }
            for (int budget = 0; budget < 32; budget++)
            {
                byte[] data; IPEndPoint from = null;
                try { if (lanListener.Available <= 0) break; data = lanListener.Receive(ref from); }
                catch (SocketException) { break; }
                ReadLanBeacon(data, from, now);
            }
            foreach (string key in lanRooms.Where(p => now - p.Value.SeenAt > 4).Select(p => p.Key).ToArray()) lanRooms.Remove(key);
        }

        void ReadLanBeacon(byte[] data, IPEndPoint from, float now)
        {
            if (data == null || data.Length > 512 || from == null) return;
            string[] fields;
            try { fields = Encoding.UTF8.GetString(data).Split('\n'); }
            catch (ArgumentException) { return; }
            int protocol, port, players; Guid room;
            if (fields.Length != 9 || fields[0] != DiscoveryMagic || !int.TryParse(fields[1], out protocol) || fields[2].Length > 16
                || !Guid.TryParseExact(fields[3], "N", out room) || !int.TryParse(fields[5], out port) || port < 1024 || port > 65535
                || !int.TryParse(fields[6], out players) || players < 1 || players > 4) return;
            LanRoom existing;
            bool loopback = IPAddress.IsLoopback(from.Address);
            // The same room can arrive by loopback and by LAN broadcast; prefer the LAN address friends can also use.
            if (lanRooms.TryGetValue(fields[3], out existing) && loopback && !IPAddress.IsLoopback(IPAddress.Parse(existing.Address))) { existing.SeenAt = now; return; }
            if (existing == null) PlayTeamSound("found");
            lanRooms[fields[3]] = new LanRoom
            {
                Room = fields[3], Address = from.Address.ToString(), Protocol = protocol, Version = fields[2], Name = CleanName(fields[4]),
                Port = port, Players = players, NeedsCode = fields[7] == "1", Running = fields[8] == "1", SeenAt = now
            };
        }

        void CloseLanListener()
        {
            if (lanListener != null) { lanListener.Close(); lanListener = null; }
            if (lanRooms.Count > 0) lanRooms.Clear();
            lanListenRetryAt = 0;
        }

        void ShutdownLanDiscovery()
        {
            CloseLanListener();
            if (lanBeacon != null) { lanBeacon.Close(); lanBeacon = null; }
        }

        LanRoom[] VisibleLanRooms() { return lanRooms.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Address).Take(4).ToArray(); }

        string LanSignature() { return nativePage != LanPage ? "" : string.Join("|", VisibleLanRooms().Select(r => r.Room + r.Players + r.Running + r.Compatible).ToArray()) + lanStatus; }

        void BuildNativeLanGames()
        {
            nativeLabels["heading"].text = "LAN GAMES"; nativeLabels["heading"].fontSize = 30;
            var rooms = VisibleLanRooms();
            for (int i = 0; i < rooms.Length; i++)
            {
                LanRoom room = rooms[i];
                float y = 82 - i * 34;
                string state = !room.Compatible ? "needs " + room.Version : room.Running ? "in a run" : room.Players + "/4" + (room.NeedsCode ? "  code" : "");
                if (room.Compatible && !room.Running && room.Players < 4)
                {
                    var choice = NativeChoice("lan-" + i, room.Name + "'S ROOM", -45, y, 330, 19, delegate { JoinLanRoom(room); });
                    choice.text.enableWordWrapping = false; choice.text.overflowMode = TextOverflowModes.Ellipsis;
                }
                else NativeText("lan-" + i, room.Name + "'S ROOM", -45, y, 330, 30, 19, TextAlignmentOptions.Center).color = Color.gray;
                NativeText("lan-state-" + i, state, 170, y, 110, 30, 12, TextAlignmentOptions.Right);
            }
            NativeText("lan-status", rooms.Length == 0 ? lanStatus : "Pick a room to join. Hosts must have their room open.", 0, rooms.Length == 0 ? 40 : -64, 440, 40, 13, TextAlignmentOptions.Center);
            NativeText("lan-note", "Not listed? Use JOIN GAME with the host's address.", 0, -96, 440, 24, 11, TextAlignmentOptions.Center);
            NativeChoice("back", "BACK", 0, -147, 260, 24, NativeBack);
        }

        void JoinLanRoom(LanRoom room)
        {
            Address = room.Address; portText = room.Port.ToString(); joining = true; Online = false;
            if (room.NeedsCode)
            {
                // Codes are never broadcast; open the Join form on its room code field.
                Action openForm = delegate { nativePage = 1; Status = "Enter the room code for " + room.Name + "'s room."; };
                var global = Singleton<GlobalCam>.Instance;
                // NativePageTransition skips its action while another dissolve is running; switch directly then.
                if (global != null && global.TransitionActive) { openForm(); BuildNativeMenu(); }
                else NativePageTransition(openForm);
                nativeEditing = "code";
                return;
            }
            nativePage = 1; Status = "Joining " + room.Name + "'s room...";
            Connect(false);
            BuildNativeMenu();
        }
    }
}
