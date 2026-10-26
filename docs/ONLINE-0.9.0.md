# Steam online play and link health (0.9.0)

## Steam rooms

Baldi's Basics Plus is a Steam game and already ships Steamworks.NET (`BALDI_Data/Managed/com.rlabrecque.steamworks.net.dll`) with `steam_api64.dll`. Its own `SteamManager` starts Steam (`SteamAPI.Init`) and runs Steam callbacks every frame. The mod uses that copy under the game's own Steam app. Nothing is downloaded or bundled, no extra server is involved, and Steam shows players as playing Baldi's Basics Plus.

- **Host (`HOST ON STEAM`):** opens a Steam lobby (friends only by default, or public) and a P2P listen socket (`SteamNetworkingSockets.CreateListenSocketP2P`).
  - The lobby carries `bt=1`, the exact version (`bt_version` = mod version / protocol) and the host's name.
  - Rich presence `connect` is set to `+connect_lobby <id>`, so friends get **Join Game** on the host's Steam profile.
  - **INVITE FRIENDS** opens the Steam overlay's invite dialog.
- **Friend:** joins in one of three ways:
  - **FIND STEAM GAMES:** rooms friends are in (any lobby type, from their game info) plus public Baldi Together rooms (lobby filter `bt=1`). Rooms on another version are greyed out.
  - **An overlay invite** (`GameLobbyJoinRequested_t`).
  - **Join Game** (`GameRichPresenceJoinRequested_t`), or the `+connect_lobby <id>` argument when Steam starts the game from an invite.
- **Connecting:** the friend enters the lobby, checks `bt_version`, and connects to the lobby owner with `ConnectP2P`. Traffic goes through Valve's relay network: no IP addresses, no port forwarding, no VPN.
- **The carrier** (`src/SteamLink.cs`): each Steam connection is a Transport link. One Transport frame is one reliable Steam message. Steam keeps connections alive on its own thread. Everything above the Transport is the LAN protocol, including the exact-version and game-hash handshake.
- **Limits:**
  - No host migration over Steam: it needs addresses. A host leaving ends the room.
  - Both players need Steam running and signed in, and the game started from Steam.
  - Opening the Steam overlay pauses a running game (the game's own `SteamManager` does that). INVITE FRIENDS is on the lobby screen, not used during a run.

The global relay server added on 2026-10-24 (ONLINE GAME, `OnlineRelay.cs`, `RelayLink.cs`, `ModInfo.cs`) was never released. It has been removed: online play is Steam only, and LAN is unchanged.

## Link health

The user's log from a real session showed a client migrating after its link to the host ended. Before 0.9.0, link liveness came from the game loop: the only regular traffic was the 2 s heartbeat from `Update`, and the socket timed out after 15 s without data. So a host whose game loop stopped for 15 s looked dead, and the client migrated. Possible causes are a long load, a hitch, or perhaps an Alt+Tab; the last is not proven. A client stalled that long was dropped by the host the same way.

- **Keepalive:** the socket writer thread sends a 1-byte keepalive frame (`0x00`) after 2 s without traffic, and the reader swallows it. A stalled game loop keeps the link. A dead process or network still times out after 15 s. Keepalives start after the peer's first frame, so an old build still gets its version-mismatch message. Protocol 14.
- **Host silence:** the client measures the host's silence itself. After 5 s it shows "The host isn't responding". After 60 s it treats the host as gone and migrates (LAN) or leaves (Steam).
- **Queues:** packets waiting for the game loop are capped at 8 MB, not 512 packets. Unsent packets per link are capped at 4 MB, not 128 packets. The old caps dropped a client after about 25 s of host stall, or about 9 s with three clients, and dropped a link when a bad internet connection held TCP for a few seconds.

## Tests

- **`scripts/test.ps1`** (26 transport checks):
  - An idle link survives past the read timeout.
  - A silent raw peer still times out.
  - 1,500 queued packets wait for a stalled loop.
  - Mutation twin `BT_MUTANT=no-keepalive` must fail the idle-link check.
- **`tests/tcp-chaos.py`:** a seeded TCP impairment proxy (6 self-test checks). The hub's chaos box (coop-anything `tools/chaos`) is UDP only, so this proxy reuses its `profiles.json` for TCP:
  - delay and jitter in order;
  - Gilbert-Elliott loss as retransmission stalls;
  - bandwidth pacing with backpressure;
  - live `stall` and `cut` on a control port.
- **`scripts/link-test.ps1`** (`tests/verify-link.py`, BepInEx host and MelonLoader client through the proxy on `bad_wifi`):
  1. connected through the proxy, on the same floor;
  2. the host's game loop blocked for 25 s (`-bt-lab-stall-host 25`): the client waits without dropping or migrating, and the host keeps the client;
  3. a 10 s two-way outage: both stay connected and in sync;
  4. a cut link: still detected, and the client starts migration.

  `-Mutant` runs it with `-bt-lab-no-keepalive` and passes only if step 2 fails. It does: the client drops at 15.0 s and migrates, which is the reported symptom.
- **`steam-tour`** (`run-tour.ps1 -Steam`, one process, Steam signed in; 16 checks):
  - Steam ready; the `+connect_lobby` argument parses.
  - On Steam's in-process socket pair (the real Steam networking API under two real Transports):
    - connect;
    - frames both ways;
    - 64 KB frame;
    - 200 frames in order;
    - final frame before close, and the closing side ends its own peer.
  - The Steam and FIND STEAM GAMES pages build, and the lobby list answers.
  - HOST ON STEAM opens a real friends-only lobby (marked `bt=lab`, so it is never listed) with its data and Join Game presence.
  - The room page shows INVITE FRIENDS. Leaving closes the lobby, clears presence and returns to the Steam page.
  - Screenshots: `artifacts/tests/steam-page.png` and `steam-room.png`.
- **Not covered:** one Steam account can't connect to itself over P2P, so a two-account session over Valve's relay has not run. That needs two PCs (or two accounts) and is the first real-session check for 0.9.0.
