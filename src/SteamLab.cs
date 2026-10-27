using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Steamworks;
using UnityEngine;

namespace BaldiTogether
{
    // Lab (-bt-steam-tour, one process, Steam running and signed in; run-tour.ps1 -Steam sets SteamAppId).
    // One Steam account can't call itself over P2P, so the carrier is checked on Steam's own in-process socket
    // pair (real Steam networking API, real Transport, no network), and the room on a real friends-only lobby
    // marked "lab" so FIND STEAM GAMES never lists it. A two-account session over Valve's relay is not covered.
    public sealed partial class TogetherRuntime
    {
        bool steamLabStarted;

        void UpdateSteamLab()
        {
            if (steamLabStarted || !Lab.Enabled || !TitleVisible || !Lab.HasArg("-bt-steam-tour")) return;
            steamLabStarted = true; StartCoroutine(TourSteam());
        }

        IEnumerator TourSteam()
        {
            float until = Time.realtimeSinceStartup + 20;
            while (!SteamReady && Time.realtimeSinceStartup < until) yield return null;
            CheckNativeUi("steam-ready", SteamReady);
            CheckNativeUi("steam-launch-argument", ParseLobbyArgument("+connect_lobby 109775241234567890") == 109775241234567890UL && ParseLobbyArgument("-bt-lab") == 0);
            if (!SteamReady) { WriteLabResults("steam-results.json", "Steam"); yield break; }

            // The carrier: Steam socket pair under two Transports, pumped like the game does every frame.
            var hostSide = new Transport(); var clientSide = new Transport();
            var hostNet = new SteamNet(hostSide, true); var clientNet = new SteamNet(clientSide, false);
            var hostEvents = new List<NetworkEvent>(); var clientEvents = new List<NetworkEvent>();
            bool paired = SteamNet.Pair(hostNet, clientNet);
            Func<bool> pump = () => { hostNet.Pump(); clientNet.Pump(); NetworkEvent e; while (hostSide.Poll(out e)) hostEvents.Add(e); while (clientSide.Poll(out e)) clientEvents.Add(e); return true; };
            pump();
            CheckNativeUi("steam-pair-connects", paired && hostEvents.Any(e => e.Kind == "connected" && e.Peer == 1) && clientEvents.Any(e => e.Kind == "connected" && e.Peer == 0) && hostSide.Hosting && hostSide.Steam);
            clientSide.Send(0, new byte[] { 41, 1 });
            hostSide.Send(1, new byte[] { 42, 2 });
            byte[] big = Enumerable.Range(0, Transport.MaxFrame).Select(i => (byte)(i % 253)).ToArray();
            hostSide.Send(1, big);
            for (int i = 0; i < 200; i++) clientSide.Send(0, new byte[] { 43, (byte)i, (byte)(i >> 8) });
            until = Time.realtimeSinceStartup + 10;
            while (Time.realtimeSinceStartup < until && (clientEvents.Count(e => e.Kind == "packet") < 2 || hostEvents.Count(e => e.Kind == "packet") < 201)) { pump(); yield return null; }
            var toHost = hostEvents.Where(e => e.Kind == "packet").ToList(); var toClient = clientEvents.Where(e => e.Kind == "packet").ToList();
            CheckNativeUi("steam-frames-both-ways", toHost.Count > 0 && toHost[0].Data.SequenceEqual(new byte[] { 41, 1 }) && toClient.Count > 0 && toClient[0].Data.SequenceEqual(new byte[] { 42, 2 }));
            CheckNativeUi("steam-max-frame", toClient.Count > 1 && toClient[1].Data.SequenceEqual(big));
            var ordered = toHost.Skip(1).Select(e => e.Data[1] | e.Data[2] << 8).ToList();
            CheckNativeUi("steam-200-frames-in-order", ordered.Count == 200 && ordered.SequenceEqual(Enumerable.Range(0, 200)));
            hostSide.SendAndClose(1, new byte[] { 44, 9 });
            until = Time.realtimeSinceStartup + 10;
            while (Time.realtimeSinceStartup < until && !clientEvents.Any(e => e.Kind == "disconnected")) { pump(); yield return null; }
            var tail = clientEvents.Where(e => e.Kind == "packet" || e.Kind == "disconnected").ToList();
            CheckNativeUi("steam-final-frame-then-close", tail.Count >= 2 && tail[tail.Count - 2].Kind == "packet" && tail[tail.Count - 2].Data[0] == 44 && tail[tail.Count - 1].Kind == "disconnected");
            pump();
            // the closing side must end its own peer too (Steam posts no callback for a local close)
            CheckNativeUi("steam-close-ends-host-peer", hostEvents.Any(e => e.Kind == "disconnected" && e.Peer == 1));
            hostNet.Dispose(); clientNet.Dispose(); hostSide.Dispose(); clientSide.Dispose();

            // The menu pages.
            SetPanel(true);
            Online = true; OpenNativeOptions(SteamPage);
            CheckNativeUi("steam-page-builds", nativeLabels.ContainsKey("steam-host") && nativeLabels.ContainsKey("steam-find") && nativeLabels.ContainsKey("steam-visibility"));
            yield return new WaitForSecondsRealtime(0.5f);
            QueueMenuCapture(Path.Combine(Lab.Workspace, "artifacts/tests/steam-page.png"));
            yield return new WaitForSecondsRealtime(0.5f);
            FetchSteamRooms(); OpenNativeOptions(SteamListPage);
            until = Time.realtimeSinceStartup + 15;
            while (steamListStatus.StartsWith("Asking") && Time.realtimeSinceStartup < until) yield return null;
            BuildNativeMenu();
            CheckNativeUi("steam-list-answers", !steamListStatus.StartsWith("Asking") && !steamListStatus.StartsWith("Steam didn't") && nativeLabels.ContainsKey("steam-refresh"));
            Log("LAB steam list: " + steamRooms.Count + " rooms, status '" + steamListStatus + "'");

            // A real room: friends-only lobby + P2P listen socket, then leave.
            OnlinePublic = false; nativePage = SteamPage;
            Connect(true);
            until = Time.realtimeSinceStartup + 20;
            while (!steamLobby.IsValid() && Active && Time.realtimeSinceStartup < until) yield return null;
            var lobby = steamLobby;
            CheckNativeUi("steam-host-opens-lobby", Active && Hosting && Network.Steam && lobby.IsValid() && SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID());
            // The host is a member of its own lobby; an account that never joined it is not.
            CheckNativeUi("steam-lobby-gate", lobby.IsValid() && SteamMember(SteamUser.GetSteamID()) && !SteamMember(new CSteamID(76561197960265728UL + 4242)));
            CheckNativeUi("steam-lobby-data", lobby.IsValid() && SteamMatchmaking.GetLobbyData(lobby, LobbyKey) == LobbyLabMark && SteamMatchmaking.GetLobbyData(lobby, "bt_version") == SteamVersion && SteamMatchmaking.GetLobbyData(lobby, "bt_name") == Name);
            CheckNativeUi("steam-join-presence", lobby.IsValid() && SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "connect") == "+connect_lobby " + lobby.m_SteamID
                && ParseLobbyArgument(SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "connect")) == lobby.m_SteamID);
            BuildNativeMenu();
            CheckNativeUi("steam-room-has-invite", nativeLabels.ContainsKey("steam-invite") && nativeLabels["room-title"].text == "STEAM ROOM");
            yield return new WaitForSecondsRealtime(0.5f);
            QueueMenuCapture(Path.Combine(Lab.Workspace, "artifacts/tests/steam-room.png"));
            yield return new WaitForSecondsRealtime(0.5f);
            Disconnect("Lab: left the Steam room.");
            yield return null; yield return null;
            CheckNativeUi("steam-leave-closes-room", !Active && steamNet == null && !steamLobby.IsValid() && SteamFriends.GetFriendRichPresence(SteamUser.GetSteamID(), "connect").Length == 0);
            CheckNativeUi("steam-leave-returns-to-steam-page", Online && nativePage == SteamPage);
            SetPanel(false);
            WriteLabResults("steam-results.json", "Steam");
        }
    }
}
