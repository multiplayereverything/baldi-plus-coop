using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using TMPro;
using UnityEngine;

namespace BaldiTogether
{
    // Online play over Steam, under the game's own Steam app (the game's SteamManager starts Steam).
    // The host opens a Steam lobby (friends only, or public) and a P2P listen socket; friends join from
    // FIND STEAM GAMES, an overlay invite, or Join Game on the host's Steam profile, and connect to the
    // lobby owner through Valve's relay (SteamLink.cs). No IP addresses, port forwarding or other servers.
    // Everything after the connection is the LAN protocol. Host migration needs addresses, so it is LAN only.
    public sealed partial class TogetherRuntime
    {
        const int SteamPage = 6, SteamListPage = 7;
        const string LobbyKey = "bt", LobbyMark = "1", LobbyLabMark = "lab";
        public bool Online;
        public bool OnlinePublic;
        SteamNet steamNet;
        Transport steamOwner;
        CSteamID steamLobby = CSteamID.Nil;
        ulong steamJoinLobby, steamLaunchLobby;
        bool steamCallbacksReady;
        Callback<GameLobbyJoinRequested_t> lobbyJoinRequested;
        Callback<GameRichPresenceJoinRequested_t> presenceJoinRequested;
        CallResult<LobbyCreated_t> lobbyCreated;
        Callback<LobbyEnter_t> lobbyEntered; // a callback, not a CallResult: a second Set would cancel the first and strand us in its lobby
        CallResult<LobbyMatchList_t> lobbyList;
        sealed class SteamRoom { public CSteamID Lobby; public string Name; public int Players; public bool Friend, VersionOk = true; }
        readonly List<SteamRoom> steamRooms = new List<SteamRoom>();
        string steamListStatus = "";
        int steamListRevision;

        // Host and friends must run the same build; checked from lobby data before connecting, then by the handshake.
        static string SteamVersion { get { return ModVersion + "/" + Wire.Version; } }

        static bool SteamReady
        {
            get { try { return SteamManager.Initialized && SteamUser.BLoggedOn(); } catch (Exception) { return false; } }
        }

        // "+connect_lobby <id>": Steam starts the game this way when a friend accepts an invite while it's closed.
        public static ulong ParseLobbyArgument(string text)
        {
            ulong id = 0;
            string[] parts = (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < parts.Length; i++)
                if (parts[i] == "+connect_lobby" && ulong.TryParse(parts[i + 1], out id)) return id;
            return 0;
        }

        void EnsureSteamCallbacks()
        {
            if (steamCallbacksReady || !SteamReady) return;
            steamCallbacksReady = true;
            lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => RequestSteamJoin(r.m_steamIDLobby.m_SteamID, "invite from " + SteamFriends.GetFriendPersonaName(r.m_steamIDFriend)));
            presenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(r => RequestSteamJoin(ParseLobbyArgument(r.m_rgchConnect), "Join Game"));
            lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            lobbyList = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
        }

        // Called by Connect() in online mode. False: nothing was started (Tell explains why).
        bool ConnectOnline(bool host)
        {
            EnsureSteamCallbacks();
            if (!SteamReady) { Tell("Steam isn't available. Start the game from Steam and stay signed in."); return false; }
            if (host)
            {
                Network = new Transport();
                steamOwner = Network; steamNet = new SteamNet(Network, true); steamNet.Listen();
                self = 0;
                peers[0] = new Peer { Id = 0, Name = Name, Accepted = true, ModVersion = ModVersion, GameHash = gameHash, Colour = PlayerColour };
                lobbyCreated.Set(SteamMatchmaking.CreateLobby(OnlinePublic ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly, 4));
                nativePage = 1; // the room page, as after HOST on LAN
                Tell("Opening a Steam room...");
                return true;
            }
            if (steamJoinLobby == 0) { Tell("Pick a room on FIND STEAM GAMES, or accept a friend's Steam invite."); return false; }
            SteamMatchmaking.JoinLobby(new CSteamID(steamJoinLobby));
            Tell("Joining the Steam room...");
            return true;
        }

        void OnLobbyCreated(LobbyCreated_t result, bool failure)
        {
            // a room that ended (or was replaced) before Steam answered, or a second lobby: leave it
            if (steamOwner == null || Network != steamOwner || steamLobby.IsValid()) { if (!failure && result.m_eResult == EResult.k_EResultOK && new CSteamID(result.m_ulSteamIDLobby) != steamLobby) SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby)); return; }
            if (failure || result.m_eResult != EResult.k_EResultOK) { Disconnect("Couldn't open a Steam room (" + result.m_eResult + ")."); return; }
            steamLobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(steamLobby, LobbyKey, Lab.Enabled && Lab.HasArg("-bt-steam-tour") ? LobbyLabMark : LobbyMark);
            SteamMatchmaking.SetLobbyData(steamLobby, "bt_version", SteamVersion);
            SteamMatchmaking.SetLobbyData(steamLobby, "bt_name", Name);
            SteamFriends.SetRichPresence("status", "Hosting Baldi Together");
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + steamLobby.m_SteamID);
            Tell("Steam room open (" + (OnlinePublic ? "public" : "friends only") + "). Use INVITE FRIENDS, or friends pick Join Game on your Steam profile.");
            BuildNativeMenuIfOpen();
        }

        // Every lobby entered (ours as creator too) arrives here. Only the one the player last asked to join is used.
        void OnLobbyEntered(LobbyEnter_t result)
        {
            var lobby = new CSteamID(result.m_ulSteamIDLobby);
            bool success = result.m_EChatRoomEnterResponse == (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
            if (Hosting && Network == steamOwner && (lobby == steamLobby || !steamLobby.IsValid())) return; // our own new room: OnLobbyCreated
            if (Active || lobby.m_SteamID != steamJoinLobby) { if (success) SteamMatchmaking.LeaveLobby(lobby); return; }
            steamJoinLobby = 0;
            if (!success)
            { Tell("Couldn't join that Steam room (" + ((EChatRoomEnterResponse)result.m_EChatRoomEnterResponse).ToString().Replace("k_EChatRoomEnterResponse", "") + ")."); return; }
            string version = SteamMatchmaking.GetLobbyData(lobby, "bt_version");
            if (version != SteamVersion)
            {
                SteamMatchmaking.LeaveLobby(lobby);
                Tell(string.IsNullOrEmpty(version) ? "That Steam room isn't a Baldi Together room." : "Version mismatch: the host runs Baldi Together " + version.Split('/')[0] + ", you run " + ModVersion + ".");
                return;
            }
            var owner = SteamMatchmaking.GetLobbyOwner(lobby);
            if (owner == SteamUser.GetSteamID()) { SteamMatchmaking.LeaveLobby(lobby); Tell("That's your own room."); return; }
            steamLobby = lobby;
            Network = new Transport();
            steamOwner = Network; steamNet = new SteamNet(Network, false); steamNet.Connect(owner);
            nativePage = 1;
            Tell("Connecting to " + SteamFriends.GetFriendPersonaName(owner) + " over Steam...");
            SaveSettings();
            BuildNativeMenuIfOpen();
        }

        // Invites, Join Game and +connect_lobby all end here.
        void RequestSteamJoin(ulong lobby, string source)
        {
            if (lobby == 0) return;
            Log("Steam join request (" + source + "): lobby " + lobby);
            if (Active) { Tell("Leave the current room before joining another."); return; }
            if (Singleton<CoreGameManager>.Instance != null && !TitleVisible) { Tell("Return to the main menu, then accept the Steam invite again."); return; }
            Online = true; joining = true; steamJoinLobby = lobby;
            Connect(false);
        }

        // Every frame (from Update): Steam messages in, join requests from the command line, cleanup after a room ends.
        void UpdateSteam()
        {
            if (steamNet != null && Network != steamOwner) CloseSteamRoom();
            if (steamNet != null) steamNet.Pump();
            if (!TitleVisible && Singleton<CoreGameManager>.Instance != null) return;
            EnsureSteamCallbacks();
            if (steamLaunchLobby != 0 && steamCallbacksReady && !Active && Time.realtimeSinceStartup > 3)
            { ulong lobby = steamLaunchLobby; steamLaunchLobby = 0; RequestSteamJoin(lobby, "launch"); }
        }

        void CloseSteamRoom()
        {
            if (steamNet != null) steamNet.Dispose();
            steamNet = null; steamOwner = null; steamJoinLobby = 0;
            // back to the Steam page, not the LAN form (whose HOST/JOIN would otherwise still act in Steam mode)
            if (Online && nativePage == 1) { nativePage = SteamPage; BuildNativeMenuIfOpen(); }
            if (steamLobby.IsValid()) { SteamMatchmaking.LeaveLobby(steamLobby); steamLobby = CSteamID.Nil; }
            if (SteamReady) SteamFriends.ClearRichPresence();
        }

        void InviteSteamFriends()
        {
            if (!steamLobby.IsValid()) { Tell("The Steam room isn't open yet."); return; }
            SteamFriends.ActivateGameOverlayInviteDialog(steamLobby);
        }

        // FIND STEAM GAMES: rooms friends are in (any lobby type) plus public Baldi Together rooms.
        void FetchSteamRooms()
        {
            EnsureSteamCallbacks();
            steamRooms.Clear(); steamListRevision++;
            if (!SteamReady) { steamListStatus = "Steam isn't available. Start the game from Steam."; return; }
            var app = SteamUtils.GetAppID();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                FriendGameInfo_t game;
                if (SteamFriends.GetFriendGamePlayed(friend, out game) && game.m_gameID.AppID() == app && game.m_steamIDLobby.IsValid())
                    steamRooms.Add(new SteamRoom { Lobby = game.m_steamIDLobby, Name = SteamFriends.GetFriendPersonaName(friend), Players = -1, Friend = true });
            }
            steamListStatus = "Asking Steam for rooms...";
            SteamMatchmaking.AddRequestLobbyListStringFilter(LobbyKey, LobbyMark, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            lobbyList.Set(SteamMatchmaking.RequestLobbyList());
        }

        void OnLobbyList(LobbyMatchList_t result, bool failure)
        {
            if (!failure)
                for (int i = 0; i < result.m_nLobbiesMatching; i++)
                {
                    var lobby = SteamMatchmaking.GetLobbyByIndex(i);
                    if (steamRooms.Any(r => r.Lobby == lobby)) continue;
                    string name = SteamMatchmaking.GetLobbyData(lobby, "bt_name");
                    steamRooms.Add(new SteamRoom { Lobby = lobby, Name = name.Length > 0 ? name.Substring(0, Math.Min(24, name.Length)) : "?", Players = SteamMatchmaking.GetNumLobbyMembers(lobby), VersionOk = SteamMatchmaking.GetLobbyData(lobby, "bt_version") == SteamVersion });
                }
            steamListStatus = failure ? "Steam didn't answer. Try again." : steamRooms.Count == 0 ? "No rooms right now. A friend's room shows here once they host." : "";
            steamListRevision++;
        }

        void BuildNativeMenuIfOpen() { if (show) BuildNativeMenu(); }

        void BuildNativeSteam()
        {
            NativeText("subheading", "PLAY OVER STEAM", 0, 101, 420, 30, 21, TextAlignmentOptions.Center);
            if (!SteamReady)
            {
                NativeText("steam-off", "Steam isn't available. Start Baldi's Basics Plus from Steam, signed in, to play online.", 0, 30, 430, 60, 15, TextAlignmentOptions.Center);
                NativeChoice("back", "BACK", 0, -119, 240, 26, NativeBack);
                return;
            }
            NativeField("name", "Name", 65);
            NativeChoice("steam-host", "HOST ON STEAM", 0, 26, 360, 25, delegate { Online = true; joining = false; steamJoinLobby = 0; Connect(true); });
            NativeChoice("steam-find", "FIND STEAM GAMES", 0, -8, 420, 22, delegate { FetchSteamRooms(); OpenNativeOptions(SteamListPage); });
            NativeChoice("steam-visibility", OnlinePublic ? "ROOM: PUBLIC" : "ROOM: FRIENDS ONLY", 0, -40, 420, 18, delegate { OnlinePublic = !OnlinePublic; SaveSettings(); BuildNativeMenu(); });
            NativeText("steam-note", "Friends join from FIND STEAM GAMES, your invite, or Join Game on your Steam profile.", 0, -72, 440, 30, 11, TextAlignmentOptions.Center);
            NativeText("status", Status, 0, -97, 440, 22, 11, TextAlignmentOptions.Center);
            NativeChoice("back", "BACK", 0, -127, 240, 24, NativeBack);
        }

        void BuildNativeSteamList()
        {
            nativeLabels["heading"].text = "STEAM GAMES"; nativeLabels["heading"].fontSize = 30;
            var rooms = steamRooms.Take(4).ToArray();
            for (int i = 0; i < rooms.Length; i++)
            {
                var room = rooms[i];
                float y = 82 - i * 34;
                string label = room.Name.ToUpperInvariant() + "'S ROOM";
                if (room.VersionOk && (room.Players < 0 || room.Players < 4))
                {
                    var choice = NativeChoice("steam-room-" + i, label, -45, y, 330, 19, delegate { RequestSteamJoin(room.Lobby.m_SteamID, "FIND STEAM GAMES"); });
                    choice.text.enableWordWrapping = false; choice.text.overflowMode = TextOverflowModes.Ellipsis;
                }
                else NativeText("steam-room-" + i, label, -45, y, 330, 30, 19, TextAlignmentOptions.Center).color = Color.gray;
                NativeText("steam-state-" + i, !room.VersionOk ? "other version" : room.Friend ? "friend" : room.Players + "/4", 170, y, 110, 30, 12, TextAlignmentOptions.Right);
            }
            NativeText("steam-list-status", rooms.Length == 0 ? steamListStatus : "Pick a room to join.", 0, rooms.Length == 0 ? 40 : -64, 440, 40, 13, TextAlignmentOptions.Center);
            NativeChoice("steam-refresh", "REFRESH", 0, -112, 260, 20, delegate { FetchSteamRooms(); BuildNativeMenu(); });
            NativeChoice("back", "BACK", 0, -147, 260, 24, NativeBack);
        }

        string SteamSignature() { return Online + ":" + steamLobby.m_SteamID + ":" + (nativePage == SteamListPage ? steamListRevision + steamListStatus : ""); }

        string SteamDiagnostic()
        {
            return ",\"steamReady\":" + SteamReady.ToString().ToLowerInvariant() + ",\"steamLobby\":\"" + steamLobby.m_SteamID + "\",\"steamTransport\":" + (Network != null && Network.Steam).ToString().ToLowerInvariant()
                + ",\"steamReceived\":" + (steamNet != null ? steamNet.Received : 0) + ",\"steamSent\":" + (steamNet != null ? steamNet.Sent : 0);
        }
    }
}
