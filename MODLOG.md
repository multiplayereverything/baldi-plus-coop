# Development journal

## 2026-10-26: 0.9.1 glue sticks, late shop acks, lab startup race

**Request:** relayed from the user ("tell each mod to continue its work on the game and its current progress"):
- the 44/45 encounter result;
- the two-process startup stall;
- Alt+Tab;
- then the handoff's next steps (glue-stick ownership, late shop-ack refunds);
- a tagged GitHub pre-release per version.

**Lab evidence first:**
- The lab suite deleted each two-process report before the next test, and `run-tour` overwrote a stalled attempt's log with its retry.
- `scripts/save-failure.ps1` now copies a failed attempt's reports, game logs, MelonLoader `Latest.log`, runtime diagnostics and last screenshot to `private/failures/<name>-<time>/`. The suite, `run-tour`, `link-test` and `shop-test` call it.

**Startup stall: a join race, not a hang:**
- Caught live: the MelonLoader client reached the title, connected at 19:03:56 and got "Connection refused". The host only opened its room at 19:04:05, and lab auto-join never retried, so the pair waited until the verifier timed out.
- Fix (lab only; real players press JOIN): auto-join retries every 2 s, up to 30 times.
- The warning-screen wait also logs which camera part is missing, and advances after 20 s. It is a separate condition seen in single-process tours.

**Encounter 44/45:**
- Not reproduced: 45/45 in four repeats plus one rerun.
- The failing check from the suite run is unknown, because its report was deleted. A repeat would now be kept.

**Late shop acks:**
- Cause: at the 12 s deadline the host closed a remote purchase as sold and forgot it. A failure ack arriving later was ignored: the team lost the YTPs and the item.
- Fix:
  - expired deliveries are kept for 120 s while the buyer is connected;
  - a late failure refunds and puts the item back, if the shelf still shows the deadline's sold state;
  - a late success runs the same `ItemPurchased`/`ItemCollected` bookkeeping as an on-time ack.

**Glue sticks:**
- Native rule: `StoreRoomFunction` prices the glue stick at 1000 + 500 × `StickerManager.TotalGlueSticks` and hides it at 4. The shared shelf used the host's count.
- Fix, with no wire change (the request already quotes a price):
  - the client shows its own price, or sold out at 4, and quotes it;
  - the host accepts any quote that matches a possible count (1000, 1500, 2000 or 2500) for the glue slot only, and charges that.
- The count is the client's own sticker state: a modified client could quote 1000. That affects only its own upgrade price.

**Evidence: `scripts/shop-test.ps1`** (focused sync, BepInEx host and MelonLoader client, real Johnny shelf):
- plain 8/8;
- `-LateAck` 9/9, with the client holding every ack 15 s: the host refunds the late failure, books two late sales and puts the item back;
- `-Mutant late-ack` (host ignores late acks): caught;
- `-Mutant glue` (host-priced glue for everyone): caught.
- The client with 2 glue sticks saw 2000 and paid 2000; the host's shelf said 1000.
- **Test-script race found:** right after a failed delivery, the client's last snapshot could still show the pre-reservation wallet, so it bought again while the item was reserved. The client step now waits for its ack to be sent plus 1 s.
- **Verifier fixes:** exact purchase counts and the owned count after buying (the glue stick itself counts) became "at least" checks, or a count recorded when the price was shown.

**Alt+Tab:** still not tested. The 0.9.0 keepalive already covers the likely failure path, a stalled host making clients migrate.
- After 0.9.1 the user said yes to the visible test. Setup: a host and a client from the same game folder, which is the user's setup, in a soak run so nobody gets caught. Keystrokes come from the desktop-control tool, because Windows blocks foreground changes from a background script.
- Attempt 1: the host crashed at BepInEx startup. It lost the race for the shared `BepInEx.cfg` against the client, which started 3 s later (`ConfigFile.Save` IOException, then a NullReferenceException in `InputManager`). This is a same-folder hazard, not a mod bug. The script now starts the client only after the host has logged the mod.
- Attempt 2: both copies reached the same level. The desktop-control grant for "Baldi's Basics Plus" did not cover the `baldi.exe` windows, and adding `baldi.exe` was declined. No keys were sent, and the user then asked to stop the mods for now.
- To resume: run `alttab-pair.ps1` (kept in `private/alttab-pair.ps1`), grant desktop control of `baldi.exe`, press Alt+Tab away from the host for over 15 s and back, then write `private/alttab-done.txt`.

**Encounter 44/45 found:** the full suite reproduced it, and the new failure capture kept the evidence.
- Run 1, 44/45: the client never counted its Pomp appointment, but was rewarded. Run 2, 41/45: the host logged a remote Pomp assignment, then a cancel; the client's Pomp counters stayed at 0, so it never applied the appointment.
- Cause (a real bug): `HandlePompHost` dropped an assignment when `npcs[index]` was not yet a Pomp replica on the client. Pomp catches a client right after spawning, before the next snapshot creates the replica. The host then waited out its 75 s timer and cancelled.
- Fix: an assignment for a replica that doesn't exist yet waits in a short list and is retried each frame for up to 5 s (`pompLateAssigns` counts these).
- Lab race fixed too: the client reads each scenario step from a file every 0.1 s, so a Pomp assignment or Reflex start could arrive before the step's baseline and be counted into it. Pomp and Reflex counters now keep the baseline from the previous step.

**Full lab suite on 0.9.1:** run while two other sessions had six MiSide and Spooky game windows open (CPU 70 to 100%).
- Passed: transport 26 plus the no-keepalive mutant; all 16 single-process tours except compat-fail; LAN 15/15 (one retry); NPC sync 15/15; core co-op 14/14 (one retry); link 7/7 with the mutant caught; shop plain, late-ack, and both mutants caught. Steam tour 16/16.
- Single-process tours that wrote no results on their first attempt (hide-and-seek audio, spectator UI, Crafters, Steam) passed on the retry; their evidence is in `private/failures/`.
- Failed, then rerun after the fixes above:
  - compat-fail 2/3 (no title within 23 s under load): rerun 3/3.
  - encounter 44/45 and 41/45: the Pomp bug. Reruns: 45/45 once, and twice 44/45 where the only failure was Unity's `Failed to confine the cursor - ClipCursor failed` warning, which the hidden lab window logs when another app holds focus. `verify-encounter.py` now ignores that one engine message.
  - soak 8/12, twice: every playability check passed (floor 2 reached, steady heap, no exceptions); only the timing limits failed. A/B under the same load, alternating 0.9.0 release DLLs and 0.9.1 (`launch-lab.ps1 -NoBuild`, new, installs the DLLs in artifacts/ without rebuilding):
    - 0.9.0: 8/12 and 8/12; median host mod cost 0.95 and 1.11 ms, frame p95 9.7 and 14.0 ms.
    - 0.9.1: 9/12 and 8/12; median host mod cost 1.03 and 1.32 ms, frame p95 12.2 and 17.9 ms.
    - Both versions fail the same limits, and the cost follows each run's frame time (machine load), so no 0.9.1 regression shows. Quiet-machine soaks measured about 0.2 ms; the soak needs a rerun on a quiet machine.
    - A first A/B attempt was invalid: `launch-lab.ps1` rebuilt 0.9.1 before every launch, which is why `-NoBuild` exists.

## 2026-10-26: 0.9.0 Steam online play and link health

**Requests:**
- Continue and improve, using the coop-research, coop-revive, coop-qa, coop-reverse-engineer and coop-sync-testing skills.
- Then: "update and upload and push to this github, this won't use the global relay server and work with steam api" (github.com/multiplayereverything/baldi-plus-coop).
- The user chose a clean public snapshot (local history has personal emails) and removing the relay code.

**Pending 0.8.3 work, finished:**
- The poster-room check ran: 1 poster room, 24 posters, his among them.
- The first screenshot stood too close and showed only his poster, and a desk blocked a straight step back. The shot now uses the farthest room cell with a clear view.
- `artifacts/tests/hall-monitor-poster-room.png` shows his poster with the native ones. Hall Monitor tour 25/25.

**Found uncommitted:** an ONLINE GAME mode through the global relay (`OnlineRelay.cs`, `RelayLink.cs`, `ModInfo.cs`). The global-mods-server sessions wrote it on 2026-10-24 and installed it, still labelled 0.8.3. It was never released, and it is now removed.

**Link health (coop-qa / coop-sync-testing: lag and loss runs, mutation twins):**
- **Cause of "client migrated after its link to the host ended":**
  - Liveness came from the game loop: the 2 s heartbeat is sent from `Update`, and sockets time out after 15 s.
  - A host game loop stalled for 15 s therefore looked dead. A client stalled that long was dropped by the host.
  - The receive queue dropped a peer at 512 queued packets: about 25 s of one client's poses during a host stall, about 9 s with three clients.
  - The send queue dropped a link at 128 packets.
- **Fix:**
  - keepalive frames (1 byte, `0x00`) from the socket writer after 2 s of idle, swallowed by the reader, and started only after the peer's first frame;
  - the client's own host-silence notice (5 s) and limit (60 s, then migration on LAN or leaving on Steam);
  - byte caps of 8 MB queued and 4 MB unsent.
  - Protocol 14.
- **Tool:** the hub chaos box is UDP only, so `tests/tcp-chaos.py` models TCP with the hub's `profiles.json`:
  - ordered delay and jitter;
  - Gilbert-Elliott loss as retransmission stalls;
  - bandwidth with backpressure;
  - live stall and cut.
  - Self-test 6/6.
- **Evidence:**
  - `scripts/test.ps1` 26 checks: idle link survives, silent peer times out, 1,500 queued poses kept. The `BT_MUTANT=no-keepalive` twin is caught.
  - `scripts/link-test.ps1` over `bad_wifi`, 7/7:
    - the client waited through a 25 s host stall (quiet max 25,060 ms, no drop, no migration);
    - the host kept the client;
    - a 10 s outage was survived (host packet age 178 ms afterwards);
    - a cut was detected (the client migrated).
  - Mutant run with `-bt-lab-no-keepalive`: the client dropped at 14,998 ms and started migration, the reported symptom. Caught.
  - Traffic through the proxy: about 19 kbit/s host to client and 3.4 kbit/s client to host.
  - The first link-test attempt hit the known two-process startup stall (black host window). The script now retries once, like the lab suite.

**Steam (coop-reverse-engineer: map what the game already has):**
- **What the game has:**
  - The game ships Steamworks.NET (`com.rlabrecque.steamworks.net.dll`, networking sockets v012, matchmaking v009) and `steam_api64.dll` (SHA-256 `1add7f151fa644870a735ae86e68d1f019f296130d8e7c0a7ed3ecc7482dccbc`).
  - Its `SteamManager` calls `SteamAPI.Init` and `RunCallbacks`. It also pauses a running game when the overlay opens.
- **So the mod:**
  - uses the game's own Steam app: no Spacewar 480, nothing bundled or downloaded;
  - carries Transport frames over `SteamNetworkingSockets` P2P (`src/SteamLink.cs`): one reliable message per frame, through the new `IPacketLink`/`AttachExternal` in `Transport.cs`;
  - runs lobbies, FIND STEAM GAMES (friends' game info plus a public `bt=1` filter), the overlay invite, Join Game rich presence and `+connect_lobby` in `src/SteamOnline.cs`.
- **No migration in Steam rooms:** `HandoverHost` and `BeginHostMigration` are skipped.
- **Evidence:** `steam-tour` 14/14, under SteamAppId 1275890 through `run-tour -Steam`:
  - Steam's in-process socket pair under two real Transports: connect, both ways, 64 KB, 200 in order, final frame before close;
  - a real friends-only lobby marked `bt=lab`: data and Join Game presence;
  - the room page with INVITE FRIENDS;
  - leaving clears the lobby and presence.
  - A menu bug it found: after HOST ON STEAM the menu fell through to the options page. Fixed.
  - The first launch exited before writing results and the retry passed; the cause was not established.
- **Not covered:** a two-account session over Valve's relay. One account can't P2P to itself.

**Research (coop-research):** the Co-op Anything directory has no Baldi entry, and web searches found no other Baldi's Basics Plus co-op project. The `searched:` list is in `docs/IDEAS.md`.

**Full lab suite** (game closed, every launch with `-bt-lab -bt-diagnostics`):
- transport 26 plus its mutant;
- tours: lobby 32, spectator 16, hide-and-seek audio 7, spectator UI 18, Beans 14, Crafters 16, Pomp 17, The Test 15, Reflex 16, Chalkles 15, items 18, Hall Monitor 25, compat-fail 3, Steam 14;
- two-process: LAN 15/15, NPC sync 15/15, core co-op 14/14, soak 12/12, link 7/7 and the mutant caught.
- Startup stalls cost one retry each for hide-and-seek audio, Hall Monitor, NPC sync and soak (soak's first attempt was 8/12).
- **Encounter:** the first attempt got 3/45 and the retry 44/45, so the suite reported FAILED. The suite deletes the encounter report before its next test, so the failing check is unknown. A rerun on the final build passed 45/45. Recorded as intermittent.

**Code review (independent subagent), confirmed and fixed:**
- **Ghost peer over Steam:** `SendAndClose` never ended the closing side's peer. Steam posts no callback for a local close, so a rejected peer stayed in the room. The Steam tour now checks it.
- **Steam mode stuck on:** after leaving a Steam room, the LAN form's HOST/JOIN still acted in Steam mode. Leaving now returns to the Steam page (checked).
- **Silent send failures:** a refused Steam send was dropped and the link kept. It now drops the peer, and the global Steam send buffer is raised to 4 MB.
- **Stranded lobbies:** overlapping join or host requests could leave the player in a lobby nothing would leave. Lobby entry now uses the broadcast callback, and only the requested lobby is used.
- **Small ones:** an oversize Steam message drops the peer, and the status callback only closes connections it owns.
- **Not fixed (cosmetic):** an old build joining a host whose loop is stalled could see a keepalive before its version Reject.
- **After the fixes:** Steam tour 16/16 and encounter 45/45 on the final build. Both release folders pass `um publish check --game BALDI_Data`: 58 files, 0 failures, 0 warnings.

**Line endings:**
- Python edits in text mode had turned `HallMonitorSync.cs`, `build.ps1` and `launch-lab.ps1` from CRLF into LF.
- The 0.8.3 work had turned `draw-hall-monitor.py` and the Hall Monitor doc from LF into CRLF.
- All five are back to the endings they have in HEAD.

## 2026-10-20: 0.8.3 Hall Monitor random spawn and native poster

**Request:** make the Hall Monitor random, and show him in the character-poster rooms like the native posters the user sent (Baldi, 1st Prize).

**Native posters (dumped by `-bt-menu-dump`):**
- **Base image:** a readable, transparent 256x256 with a portrait frame at x14-113 y30-161, an empty white card at x142-241 and a plate at x48-207 y176-207, all tan (219,155,85) with 2 px borders.
- **Text boxes:**
  - name: pos 48,48, size 160x32, COMIC_18_Pro;
  - description: pos 144,96, size 96x128, COMIC_12_Pro.
- **Rendering:** `TextTextureGenerator` draws the boxes over the base image.

**Changes:**
- **Base image:** `scripts/draw-hall-monitor.py` draws his base in that layout: his own dithered head-and-shoulders portrait, no game pixels.
- **Text boxes:** `HallMonitorAssets.UsePosterText` copies the Principal poster's two boxes (fonts included) with his name and description.
- **Spawn:** a 50% roll per floor from the run seed and the floor, using a stable hash.
- **Lab switches:** `-bt-hall-monitor` and `-bt-no-hall-monitor`; NpcSync forces him on.

**Evidence:**
- **Hall Monitor tour 24/24:**
  - `hm-poster-native-layout`;
  - `hm-poster-built-on-wall-with-text`, through the native `BuildPoster`. On the tour floor his texture was already generated by the floor's own poster room. The screenshot `artifacts/tests/hall-monitor-poster-ingame.png` matches the native style.
  - `hm-spawn-is-seeded-random`: 400 seeds land near 50% and the answer is stable.

## 2026-10-20: 0.8.2 host thrown back to the menu

**Report:** "when a host alt tabs it goes back to the menu weirdly".

**Focus behaviour:**
- Neither the game nor the mod has focus handlers; the mod sets `runInBackground`.
- The new `-bt-focus-tour` (with `scripts/focus-test.ps1`) logs focus, scene loads, pause, menu returns and transitions. A real host lost focus via Alt+Tab keystrokes, and was minimized and restored.
  - In a run: nothing changed (FullScreenWindow, frames continued).
  - In the lobby: the same.
- **With a client:** a host minimized for 25 s kept its link.
  - BepInEx host with MelonLoader client: no timeout or migration.
  - Two instances from the same folder (`scripts/focus-same-folder-test.ps1`): the same, and the host's diagnostics kept updating every second.
- **Limit:** Windows refuses foreground changes from a background script, so a true Alt+Tab between two windows was not driven.

**The user's own logs** (`AppData/LocalLow/.../Player*.log`):
- **Player.log:** two instances writing to one file (lines spliced mid-word).
  - The client migrated after its link to the host ended, and the host logged nothing more.
  - The cause was not reproduced. The client's socket times out after 15 s of silence, and any link end is treated as "host left".
- **Player-prev.log:** this PC hosted, and the run failed with "Level compatibility check failed: An item with the same key has already been added. Key: SodaMachine:95,0,65", followed by an endless `ElevatorScreen.Update` NullReferenceException.
  - Reproduced on seed 2101520539: a DietSodaMachine shares the key.
  - Reproduced the stuck elevator with the new `compat-fail-tour`, which starts through the real Hide & Seek button: 1,106 elevator exceptions on the baseline (`-bt-keep-elevator`), none with the fix.

**Fixes:**
- **Vending keys:** soda machines are registered in key-then-hierarchy order, and a repeated key gets `#n`.
- **Purchases:** client purchase requests send the registered key.
- **Disconnect:** returning to the menu destroys any live ElevatorScreen first.

**Evidence:**
- Seed 2101520539: the hall-monitor tour reached world-ready, and the two-process co-op test passed 14/14 including `matching_level`.
- compat-fail-tour 3/3.
- Full lab suite passed with no retries: transport 22; 13 tours, 209 checks; LAN 15/15, NPC sync 15/15, core co-op 14/14, encounter 45/45, soak 12/12.

## 2026-10-20: 0.8.1 only the host starts a run

**Report:** the user sent a screenshot of a joined, non-host player who pressed PLAY: the elevator screen (F1, a seed) sat over the title chalkboard. They said it had been like this since the start of the project.

**Cause:** a lab dump (`-bt-menu-dump`, `src/HostOnlyStart.cs`) listed every title button's inspector-wired `OnPress` calls. Every run button hides its menu, calls `GameLoader.Initialize`, `AssignElevatorScreen` and `LoadLevel` (Continue calls `LoadSavedGame`), then runs `ElevatorScreen.SetActive(true)`. The mod refused a client's `Initialize`, `LoadLevel` and `LoadSavedGame`, but the menu hiding and the elevator activation still ran, so the elevator was stranded over the menu.

**Fix:**
- A prefix on `StandardMenuButton.Press` and `MenuButton.Press` refuses the whole press on a connected client, but only when the button's `OnPress` targets one of those GameLoader methods (checked once per button). The client gets a notice.
- The host, single player and every other button are unaffected.
- Other client paths were checked and are fine:
  - pause is blocked during runs;
  - Quit and ReturnToMenu disconnect cleanly;
  - auto-start is host-only.

**Evidence:**
- The LAN test client presses the real Hide & Seek New and Continue buttons after joining. No elevator opens, no CoreGameManager is created, the menu stays put, the notice shows, and another button is unaffected. The client then still joins and plays the host's run.
- LAN 15/15, lobby tour 32/32, transport 22/22.

## 2026-10-20: 0.8.0 the Hall Monitor NPC

The user set the goal: add the Hall Monitor NPC with proper support, generate pictures or use a placeholder, make him fully functional using the game's own code, and use Graphify.

**Design:** he is a co-op-only NPC.
- He marks players he sees running; the mark lasts 25 s.
- When two marked players are within 15 units, he whistles.
- He spawns only on multiplayer floors that have Baldi.

**Native basis:** read in `private/full-decompile`:
- `NPC`, `Principal`, `Principal_Wandering`, `NpcState`, `NavigationState_TargetPosition` and `NavigationState_DoNothing`.
- `Looker`, `Navigator` and `Entity` (trigger discovery).
- `LevelBuilder.AddNpcsFromPreviousLevels`, `LevelGenerator` (spawn tiles from `spawnableRooms`), `EnvironmentController.SpawnNPC` and `SpawnNPCs`.
- `CharacterPostersRoomFunction`, `EnvironmentController.BuildPoster`, `PosterObject`, `SoundObject` and `LocalizationManager` (missing keys show as themselves).
- `PlayerFileManager.Find` (`foundChars` has 256 slots, so the Character id is 200).
- `Baldi.Hear` and `UpdateSoundTarget`, and `PlayerMovement.MouseMove`.

**Graphify:** queried how NPCs replicate (World snapshot, `EnsureReplicaNpc`, NPC presentation), and checked `NearestBaldiPeer`/`UseNearestBaldi` in `HidingSync`. This showed that in multiplayer Baldi ignores noises while he has a nearest-player target, so a plain `MakeNoise` whistle would do nothing in a real session. The whistle therefore narrows `NearestBaldiPeer` to the whistled pair for 10 s. The graph was updated after the change: 1,824 nodes, with a new HallMonitor community.

**Implementation:**
- **`src/HallMonitor.cs`:** the NPC, its two states, the asset loader, the prefab builder (a Principal prefab copy with the component swapped and references repointed) and the spawn postfix.
- **`src/HallMonitorSync.cs`:** host rules, the world section, the client cues and gauge, and the lab tour.
- **Other changes:**
  - `NpcInteractions.ReplicaTemplate`;
  - `HidingSync.NearestBaldiPeer`;
  - a Hall Monitor stage in `NpcSyncLab`;
  - two checks in `tests/verify-npcs.py`;
  - the dispatcher entries in `NpcFeatures`;
  - build resources.
- **Version:** 0.8.0, protocol 13.

**Assets:**
- **Sprites:** procedural, from `scripts/draw-hall-monitor.py`.
- **Sounds:** a whistle and a scribble from Stable Audio 3 small-sfx (five seeds each; picked by tonality and broadband measurements). The two voice lines are SAPI speech, pitched up 1.3x.

**Fixes found by the tour:**
- The first prefab came from `Principal_AllKnowing` (a challenge variant); the standard prefab is now preferred.
- A fixture step triggered the whistle early.
- Baldi's sound slot is consumed immediately by `UpdateSoundTarget`, and in multiplayer his hearing is skipped while he has a target. This led to the focus design above.
- The first screenshot stood behind a wall.

**Results:**
- **Full `scripts/lab-suite.ps1`:** passed with no retries.
  - Transport 22.
  - 12 tours, 206 checks, including hall-monitor 22/22.
  - Two-process tests: LAN 13/13, NPC sync 15/15 (with both new Hall Monitor checks), core co-op 14/14, encounter 45/45.
  - Soak 12/12: worst mod cost 0.195 ms on the host and 0.278 ms on the client.
- **Final build:** the sight and whistle checks moved to 10 Hz after the suite. On that build the hall-monitor tour passed 22/22 and NPC sync 15/15. The first NPC sync launch stalled at the client's profile screen; the retry passed.
- **Screenshots:** `artifacts/tests/hall-monitor-patrol.png` and `hall-monitor-whistle.png` show him in a hall at the right scale.
- **Install:** both copies are installed with matching hashes. Assembly-CSharp is unchanged (`635B0D16...`).

## 2026-10-20: 0.7.1 performance and playability

The user set the goal "make sure the game is playable and performance ready and use graphify".

**Finding per-frame code with Graphify:**
- The updated graph's call edges were walked from every per-frame entry point: the runtime Update, OnGUI, sprite LateUpdate, and Harmony patches on 15 native per-frame methods. That reached 704 methods.
- Their bodies were scanned for scene searches, component lookups, reflection, LINQ and string building.

**Measuring:** `src/PerfProbe.cs` adds a lab profiler that times every mod per-frame method in 10 s windows (FPS, frame p50/p95/max, mod ms per frame, gen-0 GC per minute, heap). `-bt-soak` runs a two-player session: random teleports, pings and BSODA use, every NPC active, catches suppressed, and floor 1 → Pitstop → floor 2.

**Baseline problem:** the client's mod cost was 1.4-3.3 ms per frame, against about 0.19 ms on the host.
- `UpdateClientElevator` used about 440 ms/s: `FindObjectOfType<ElevatorScreen>` every frame for the whole floor.
- `UpdateHiding` used about 280 ms/s during the countdown: `FindObjectOfType<HappyBaldi>`.

**Fixes:**
- Use the native Singleton and cached FieldInfos.
- Cache HappyBaldi per epoch.
- Cache `TitleVisible` per frame.
- Cache command-line flags in `Lab.HasArg` (45 call sites).

**After the fixes:** the client's mod cost is about 0.14 ms per frame, and client FPS rose from about 390 to about 950 (uncapped lab).

**Remaining spikes:** occasional 50-90 ms frames on both sides also appear on the title screen, where the mod idles. They are game GC, not mod work.

**Verification:**
- `tests/verify-perf.py`: 12/12. Worst mod cost per frame 0.20 ms on the host and 0.29 ms on the client; p95 frame time under 3 ms; floor-1 heap drift +0.6/-4.3 MB; no exceptions. All three loads matched on both sides, and 34 pings were delivered each way.
- Full lab suite with the soak: transport 22, eleven tours (184 checks), LAN 13, NPC sync 13, core 14, encounter 45, soak 12. All passed with no retries.

Version 0.7.1, protocol 12.

## 2026-10-20: 0.7.0 NPC abilities, item relay and spectate screen

The user set a goal: improve the spectate UI, sync all NPCs, use Opus sub-agents to implement whatever features are missing and necessary, and make sure everything is good.

**Scaffolding (by the coordinator):**
- `NpcFeatures.cs` dispatches per-feature reset, update, packet handlers, a length-prefixed world-snapshot section per feature, diagnostics and lab hooks.
- Packet codes 70-83, protocol 12.
- `build.ps1 -OutDir`, with game and deps discovery so worktrees can build.
- A shared brief in `docs/AGENT-BRIEF-0.7.md`.

**Opus agents:** eight agents worked in isolated worktrees, one file each: Beans, Arts and Crafters, Mrs. Pomp, The Test, Dr. Reflex, Chalkles, client item effects, and the spectate UI. Each wrote a lab tour. The coordinator merged each branch, ran its tour in the real game and returned failures with logs.

Real bugs found this way and fixed:
- **Spectate screen:** blank names, caused by TMP ellipsis on text boxes shorter than one line.
- **Arts and Crafters:** a flee DijkstraMap left in the environment's update queue after its proxy was destroyed caused a NullReferenceException in `EnvironmentController.Update`.

Fixture problems were also fixed: Pomp's hall-cell lookup, Crafters' trigger placement, The Test's host-gaze contamination, and the empty spectate captures from the hidden window.

**Independent review:** an independent Opus review of the merged code found:
- remote Dr. Reflex wins paid nothing (now awarded on the host);
- apple and scissors claims lacked state, cooldown and reach guards;
- apple duplication on a late ack;
- an uncached unknown-item scan;
- per-frame allocations in Crafters, The Test, Chalkles and spectating;
- a bystander host ending a remote Reflex hunt;
- no isolation between feature updates.

All were fixed by the owning agents and the coordinator. Two regressions from the rework (Crafters disconnect pruning, an items tour token order) were found by the suite and fixed.

**Other changes:**
- The coordinator made Baldi's nearest-player chase skip cloud-invisible teammates.
- `run-tour` retries a startup stall once and exits 0 on success.
- `verify-live` drops the obsolete `loading_barrier` check.

**Verification:**
Final merged build: transport 22/22. Eleven single-process tours, 184 checks, all passing:
- lobby 32, spectator 16, Hide & Seek audio 7, spectate screen 18
- Beans 14, Arts and Crafters 16, Mrs. Pomp 17, The Test 15, Dr. Reflex 16, Chalkles 15
- client items 18

Two-process tests: LAN join 13/13, NPC sync 13/13 and core co-op 14/14. No exceptions in any log.

These tests use simulated teammates in single-process tours. A real multi-player session is still needed to judge feel, latency and natural encounters.

## 2026-10-20: 0.6.3 lab suite for lobby features

The user asked to test the new features and add lab tests for them.

New tests:
- **`LobbyLab.cs`** adds two tours:
  - `-bt-lobby-tour`: one process, synthetic peers, real handlers and native HUD, audio and menu components.
  - `-bt-lan-tour`: the client half of a two-process join through FIND LAN GAMES with no address given.
- **Launcher modes:** `launch-lab.ps1` gains `-LobbyTour` and `-LanTour`.
- **`run-tour.ps1`** runs any single-process tour and stops only its own PID.
- **`tests/verify-lobby.py`** combines tour results with host diagnostics.
- **`lab-suite.ps1`** runs everything.

Supporting changes:
- `RecolourPixel` was extracted so the recolour rule can be tested.
- A team-sound log was added to diagnostics.
- A lab-only spectator step lets tours change teammate without key input.

Bugs the tests found:
- **Ping mixer:** the 3D ping's mixer lookup read the wrong object and returned null, so the ping bypassed the SFX volume. It now uses `AudioSourceManager.sfxMixerGroup`, the group the game assigns to Effect sounds. The ping check now rejects a null group.
- **Coded-room join:** it relied on `NativePageTransition`, which skips its action during an active dissolve. It now switches directly in that case.
- **Test-only:** the loopback round trip needed the listener to stay open.
- **LAN verifier:** it now waits for the host to receive the client's pose and HUD instead of reading once.

`baldi-keeps-moving` in the existing spectator tour failed on both the current build and an untouched 0.5.3 baseline. The baseline was built in a temporary git worktree with a deps junction; the junction was removed before the worktree. Logging showed two causes:
- The survivor was placed at a non-walkable point (player + 30 north), so Baldi wandered with no path.
- Baldi's first slap after a capture comes 2.9 s later, beyond the 2.5 s wait.

The fixture now uses the level spawn point and waits for the first slap, up to 8 s. It passed 16/16 on three runs.

Final suite on 0.6.2 code: transport 22/22, lobby 32/32, spectator 16/16, Hide & Seek audio 7/7, LAN join 13/13 (8 client and 5 host checks).

The version was bumped to 0.6.3, then rebuilt and installed in both copies; the lobby tour passed again at 32/32. Only test-owned PIDs were stopped.

## 2026-10-20: 0.6.2 switch and found sounds

The user added switch-s17 and found-s5 from the round-two candidates.
- **Switch** plays when the watched teammate changes, but not when spectating first starts.
- **Found** plays when a room id is first listed on the FIND LAN GAMES page.

Verification:
- Both loaders build.
- In the two-process lab (PIDs 15544 and 21276), both report `teamSoundsLoaded` 6, with zero exceptions in either log.
- Neither hook was triggered in the lab.
- Only those PIDs were stopped.

## 2026-10-20: 0.6.1 generated team sounds

The user asked to generate sounds with Universal Modder's fal recipes. Generation stopped because the fal account had no balance; nothing was charged. The fal key is stored outside the project in `~/.secrets/fal-key`, restricted to the user with icacls.

The user then chose Stable Audio 3. It is cloned in `toolkits/stable-audio-3` (commit 77e0398) with CPU PyTorch 2.7.1. The user accepted the gated small-sfx licence and logged in to Hugging Face themselves.

**Round 1** used abstract prompts at 0.8-1.5 s and was rejected as too loud and strange. Measured, the raw output was near full-scale noise: RMS about -1 to -4 dB at 0 dBFS peaks. My -3 dBFS peak normalisation made every clip louder still. The game's short SFX median is -15 dB RMS.

**Round 2** followed the repo's prompting guide:
- Prompts name the physical source, the action and the recording, prefixed with `TrackType: SFX`.
- Each take generates 2 s.
- Five seeds per sound.
- Noise blasts are filtered out.
- Clips are levelled to -18 dB RMS with peaks capped at -6 dBFS.

The ping takes showed real bell envelopes: a sharp attack, then a smooth decay to silence. All 30 takes passed the filter. The user picked ping-s5, join-s53, leave-s17 and caught-s5.

**Integration:**
- `TeamSounds.cs` builds runtime `SoundObject`s with subtitles off and plays them through `MusicManager.PlaySoundEffect`, the game's SFX source and mixer.
- The ping is a 3D `AudioSource` on the marker, routed to the same mixer group.
- Hooks: host Hello accept and peer disconnect; client roster additions and removals after it already knows the room; host elimination and client RoundState elimination flips; `ShowPing`.
- The WAV decoder was generalised to `LoadEmbeddedWave`.

**Verification:**
- Both loaders build.
- In the two-process lab with test actions (PIDs 35688 and 20844), both processes report `teamSoundsLoaded` 4. The host played 4 cues, starting with join then ping; the client played 3, including the ping. Both logs show zero exceptions.
- Only those PIDs were stopped.
- The leave and caught hooks were not triggered in this run.
- Audible playback was not listened to by me; the user selected the takes by ear.

Version 0.6.1, protocol 11.

## 2026-10-20: 0.6.0 LAN discovery, colours, spectator HUD

The user requested LAN discovery, player colours that change the sprite, and a spectator HUD showing the watched player.

**LAN discovery** (`LanDiscovery.cs`):
- While a room is open, the host sends a main-thread, non-blocking UDP beacon each second. Targets are the limited broadcast address, each adapter's directed broadcast address and loopback.
- The FIND LAN GAMES page binds UDP 27016 with address reuse. The same room arriving on several addresses is de-duplicated by its random migration room id, preferring a non-loopback address.
- Room codes are never broadcast.

**Colours:**
- Only teal clothing pixels are recoloured, using HSV hue replacement.
- A Pillow preview of the same filter on the supplied sheet was inspected: skin, hair and eyes are untouched.
- Colour travels in the migration identity (Hello and MigrationHello) and the roster.
- `PlayerSpriteSheet.Load` no longer fails permanently when the native NPC material is absent on the title screen. The material lookup moved to a retrying `EnsureMaterial`.

**Spectator HUD** (`SpectatorHud.cs`):
- New `PlayerHud` op, relayed by the host.
- The spectator's own HUD is driven with native `UpdateInventorySize`, `UpdateItemIcon`, `SetItemSelect` and `SetStaminaValue`.
- On release, the native `UpdateTargetInventorySize`, `UpdateItems` and `UpdateSelect` restore the local inventory.

Lab-only `-bt-colour` flag; diagnostics report `lobbyFeatures` and `lanBeacon`.

Verification:
- Build passes, and transport passes 22/22.
- Two-process lab (host PID 16784 coloured red, client PID 40068 coloured blue):
  - An external Python listener received the beacon 36 times over loopback, the LAN and a VPN adapter, with correct fields. The beacon stopped once the run started.
  - Diagnostics show matching remote colours and avatar sheets on both sides, plus fresh 5-slot HUD data.
  - Both logs show zero exceptions, and no patch failures were logged.
- Only those two PIDs were stopped.
- Not exercised: the LAN page UI, the colour picker UI and the spectator HUD rendering (no capture occurred). In-world screenshots did not frame the other avatar.

Version 0.6.0, protocol 11.

## 2026-10-20: 0.5.4 bug-fix pass

The user asked for code improvements and chose bug finding and fixing. They also approved a local git repository: the baseline commit holds 0.5.3 and nothing is pushed.

Four parallel read-only reviews covered these areas:
- transport, runtime and migration;
- shop, vending, results and hiding;
- NPC sync and spectating;
- menu, console and loader entry.

Every finding used was re-checked against the source. Where game behaviour was involved it was also checked against private decompilation, including `NavigationStateMachine.ChangeState` priority gating, the `NavigationState_TargetPlayer` holdPriority, `Playtime.PlayerTurnAround`, `LevelBuilder` room-function parenting and `CoreGameManager.Update`.

The fixes are listed in docs/FIXES-0.5.4.md.

Not changed:
- **Host HideState re-broadcast.** Intentionally kept: late-loading clients need the repeated release state.
- **Glue-stick ownership.** Needs a protocol change.
- **Refund after a late shop ack.** Not fixed in this pass.
- **Cursor restoration on console close.** Restoring from the game's cursor record was left out; the new pause gate removes the reported same-frame race.

Verification:
- Both loaders compile.
- `scripts/test.ps1` passes 22/22.
- No game-process fixtures or two-player sessions were run, so each fix still needs real-session confirmation.

Version is now 0.5.4 and protocol stays 10. Changes are committed in the local MultiplayerMod git repository.

## 2026-10-19: 0.5.3 Baldi remote contact investigation

User reported second-floor capture failing until a locker cycle; clarified that a player was spectating, without knowing the affected role. Inspected original Baldi_Chase contact, Looker.Raycast, Entity interaction counters/layers, HideableLocker enter/exit and player/floor lifecycle. REA confirmed the unchanged installed assembly identity. Native contact uses Looker's layer mask and ignored transforms; our remote capture instead used ClearWorldLine across every layer. An excluded-layer collider therefore caused a false obstruction, independently of the host's spectator state.

Replaced only remote capture visibility with the original Looker.Raycast through a reusable collider-free target at the remote pose. It uses the player entity's native target layer, not a spectating host's current layer 19. No interaction counters are forcibly reset. Existing ready/fresh-pose/distance/locker/elimination gates remain; native local contact remains untouched. Added F2 contacts and a CONTACTS section to saved reports, including interaction locks, frozen/invincible flags, Baldi state, mask, native sight and named all-layer blockers.

The corrected regression keeps an excluded-layer collider present through the actual capture check. Old code passed 10/12, failing survivor capture on both floors; fixed code passes 12/12 on each loader, including host spectating, locker protection and included-layer obstruction. An initial exploratory fixture removed the blocker before capture, so its 12/12 result did not demonstrate the gameplay regression; the retained-blocker baseline is the valid comparison. Updated console checks pass 7/7 on MelonLoader. These are native-component/actual-handler fixtures with synthetic remote peers, not the user's original save or a two-client encounter. The exact locker-cycle trigger remains unconfirmed; contacts reports are intended to distinguish additional causes. Both builds compile. Protocol remains 10; exact mod version is now 0.5.3. Only recorded lab PIDs were stopped; user processes were not terminated. Root BepInEx DLL matches the artifact and original Assembly-CSharp hash is unchanged.

Watch v26 passes all six assertions with isolated-local receipt vr_4fe3ef3e9ce0. Both 51-file packages pass Universal Modder lint with zero warnings against original BALDI_Data. README, changelog, handoff and CONTACT-0.5.3 document the fix and investigation limits. No unrelated transport suite was repeated because wire format did not change.

## 2026-10-19: 0.5.2 vending, results, locker spawning and F2 diagnostics

Traced the installed game's SodaMachine, ITM_Quarter, MainGameManager.LoadNextLevel, ElevatorScreen.Results, HappyBaldi.SpawnWait and EnvironmentController spawning. Added host-owned vending stock/reward selection with buyer-only quarter/reward receipts, stale/replayed request rejection and idempotent refunds. Unknown delivery on disconnect remains consumed. Added native results snapshots after original host reward calculation; clients assign totals and run the original results animation with completed-floor points scoped around its iterator, preserving next-floor points and the same elevator screen across loading. Connection artwork receives the name tag's exact fade alpha.

Countdown release no longer skips the native NPC/event queues because an actor already exists. It waits for manager/environment readiness and runs once per environment. Spawn buffers keep the native radius and both-axis geometry but check exposed living teammates, excluding lockers and spectators. The initial iterator-factory patch still left four/five actors queued in the two-floor lab; intercepting public SpawnNPCs and initializing its original lists resolved both floors. Factory inlining is a possible explanation, not established runtime evidence.

F2 opens a bounded, read-only console with status/peers/npcs/help/clear/save, captured game errors and safe diagnostic report output. Opening it blocks gameplay input without pausing the round and restores its owned cursor state on close.

Necessary verification: both loader builds pass; transport 22 checks pass; MelonLoader console 7/7 and vending validation 12/12; native-floor vending transaction 5/5; native results fixture 11/11; first/second-floor locker countdown 12/12; real BepInEx host to MelonLoader client results delivery 4/4 host and 5/5 client, including exact native monitor text and retained screen after floor advance. Vending transaction validation uses native components and actual handlers, not a two-client socket purchase or physical click-through. Locker checks use an isolated forced-buffer fixture, not the user's original save. Only recorded test-owned processes were stopped. Protocol increases to 10; everyone must update. Public docs describe delivery and migration limits.

Watch v25 passed all 14 assertions with isolated-local receipt vr_8c3242c38d32. Installed BepInEx and isolated MelonLoader DLL hashes match their build artifacts; the original Assembly-CSharp hash remains unchanged. Both 50-file packages pass Universal Modder lint with zero warnings against BALDI_Data. An initial check against the whole workspace incorrectly treated this project's own source/docs and installed plugins as game files; using the original game-data directory removes that self-comparison without excluding original game assets/assemblies.

## 2026-10-19: 0.5.1 Baldi stops after capture

The reported freeze was caused by native Baldi.CaughtPlayer entering Baldi_Attack before forwarding to BaseGameManager/CoreGameManager.EndGame. The 0.5.0 spectator fixture only called the core entry, so it missed that terminal state and the manager's subsequent StopAllCoroutines. Added multiplayer guards at CaughtPlayer and the manager entry, before those side effects. Individual capture now preserves chase; single-player and the Applying team-wipe path remain native. Protocol stays 9.

Updated the focused fixture to invoke real CaughtPlayer, confirm Baldi_Chase, retarget a survivor and verify actual totalDistance growth over five seconds. The manager entry, existing spectator limits, balanced camera locks, reset and native team wipe also pass: 16 checks, zero failures. This uses native components and a synthetic remote teammate, not a natural two-machine encounter. Both loader builds compile, installed hashes match, and the original game assembly is unchanged. Both packages pass Universal Modder lint without warnings. Watch v24 passes with isolated-local receipt vr_98e537a1de9a. Only the test-owned process was stopped. README, changelog and docs/CAPTURE-0.5.1.md document the fix; no unrelated/transport suites were rerun.

## 2026-10-19: 0.5.0 spectating, migration and NPC expansion

Added host-authoritative individual capture and teammate-only spectating. Eliminated avatars/name tags disappear, their entities are tagged/hidden/frozen, interaction/item/shop requests are blocked, and their personal HUD is hidden. Camera targets are restricted to recent living teammate poses; locker/no-target views are concealed. Left/Right/Tab cycles eligible teammates. Elimination is monotonic within the round; next-floor/attempt reset restores owned state. Camera control locks are released by balancing the owned counter, including when native elevator control was already locked. Team wipe uses the native ending. Remote survivors can activate native elevators while the host spectates; host checks actual collider bounds, generation, pose freshness, alive/locker state, native failure quota and final-exit conditions. Elevator state is replicated and client trigger progression is suppressed.

Implemented fixed-successor LAN host migration for graceful Leave and unannounced TCP loss. Stable room/player identities permit socket-ID remapping; bounded retries include the same-machine listener-drain case. Active floors reload with the same scene/seed, retaining captured players. Recovery tests exposed stale-world registration before the replacement Start and old-ID elimination mapping on clients; gated registration until the migration reload and restricted identity restoration to valid mappings/self. Inventory updates wait for a ready world during reload. Early-loading clients defer asset lookup rather than rejecting a floor before assets load. Migration restarts floor progress, does not transfer every NPC/item/shop transaction, provides no relay/NAT traversal and does not fence arbitrary network partitions.

Expanded Bully physical colliders/obstacle, remote sight/guilt and spawn safety; Cloudy Copter native wind-region/direction/speed/forces; NPC sprite-child activation, including Chalkles forms. Dead and locker peers are excluded from remote sight/catch eligibility. README lists all 13 named installed NPCs and required outstanding ability work; docs/NPC-COVERAGE-0.5.0.md covers extra actor classes and evidence. No claim of complete NPC ability replication.

Added native Compatibility pages with exact mod/game-build status and handshake rejection details. The supplied signal sheet is embedded unchanged (SHA256 31E823FE0DBEF2086794CE20BD81F0A735B3D740295FF2DD45B67AF3B8CC956B), cropped/tinted at runtime beside player names. Quality reflects real RTT, with stale samples gray after eight seconds; clients do not invent peer-to-peer samples. Native UI captures were visually inspected. Version is 0.5.0, protocol 9; all players must update.

117 focused checks pass: spectator 12, compatibility UI/quality 16, NPC serializer/native-component 14, remote elevator 15, transport/claims 22, live handshake rejection 4, lobby migration roles 10 and active-floor graceful/unannounced-loss recovery 12 each. Three-process recovery spans both loaders: BepInEx promotion after graceful departure and MelonLoader promotion after unannounced transport loss. Both active-floor tests match scene/seed/layout/generation, keep the replacement host captured and reconnect a living survivor into native play. Single-process NPC/spectator fixtures and handler-based UI checks are distinct from the live migration tests; no physical-input play-through, WAN/partition test or exhaustive natural NPC encounter is claimed.

Both loader builds compile, installed hashes match, the supplied artwork matches its source, and the original game assembly hash is unchanged. Both mod-only packages pass Universal Modder lint with no warnings. Watch v23 passes with isolated-local receipt vr_5564902e3e4a. Only recorded test-owned processes were stopped; existing user game processes were preserved. README, changelog, asset credits and docs/MULTIPLAYER-0.5.0.md document behavior, evidence and limits.

## 2026-10-19: 0.4.13 multiplayer-only Hide & Seek audio

Copied the user's Game Sound Preview/NewAudio/BAL_HideSeek.wav unchanged into embedded assets for both loaders (SHA256 C3A443F5A67778FEEAA79C477C4706416EAA1F43C7074E2F144E9EC44AE27DDF). Decode PCM16 into an owned AudioClip and substitute cloned SoundObjects only when multiplayer is connected and the original clip is exactly BAL_HideSeek. Native single/queued playback retains mixer, volume, voice type and subtitle key; replacement subtitle duration follows its length. Original game assets remain untouched; disconnected single-player, countdown and unrelated clips use originals. Owned sounds/clip are released with the runtime. Protocol stays 8.

Seven focused runtime checks pass for native HappyBaldi intro identity, loaded sample format, source preservation, actual intro/queued playback selection, disconnected original playback and an unrelated sound. These verify audio selection/data, not a recorded listening session. Both loaders build and installed hashes match; supplied source hash matches the embedded asset and original game assembly is unchanged. Both release packages pass Universal Modder lint without warnings. Watch v22 passes with isolated-local receipt vr_ec23a421ac46. Only the test-owned process was stopped. README/changelog, asset credits and docs/HIDESEEK-AUDIO-0.4.13.md record behavior and evidence; no transport or unrelated gameplay suites were rerun.

## 2026-10-19: 0.4.12 remote locker visibility

Inspected native HideableLocker: entering freezes the entity and redirects its camera without moving the player transform. Remote avatars therefore remained in the corridor. Track native entry and sample playerInside in each pose; host and client readers retain the flag and host relays it. Hidden peers deactivate their avatar, which also suppresses name tags. Exit restores visibility at the current pose; ResetWorld clears locker references and flags. Native locker behavior and NPC detection are unchanged. Wire protocol increases to 8, requiring all peers to update.

Eight focused runtime checks pass using a generated native locker, Clicked/ForceOpen, serialized poses through actual host/client handlers, a synthetic remote avatar and reset cleanup. This fixture is not a full two-machine visual session. All 22 transport checks pass. Both loaders compile and installed hashes match; original game assembly is unchanged. Both mod-only packages pass Universal Modder lint without warnings. Watch v21 passes with isolated-local receipt vr_ca24e2f426ae. Only the test-owned process was stopped; existing user game processes were left running. README/changelog and docs/LOCKER-VISIBILITY-0.4.12.md record behavior and scope.

## 2026-10-19: 0.4.11 Multiplayer page dissolves

Extended the native Dither to Host Game, Join Game, Player/Lobby Options, internal Back, Host/Join confirmation and Leave/Cancel. Each action captures the outgoing custom menu, performs its existing action and renders the new page into the native destination. Camera targets and pointer restore afterward. Navigation is blocked during transitions; text fields continue editing immediately and Coming Soon options remain labels.

The focused runtime tour passes 47 checks across eleven navigation actions, destination and progressive-blend pixels, camera restoration, real local hosting/leaving, invalid-port validation and immediate text editing. Its intermediate Join dissolve was visually inspected. The fixture uses component handlers, not physical clicks, and does not test a remote Join handshake. Both loaders build and installed hashes match; original game assembly is unchanged. Both release packages pass Universal Modder lint without warnings. Watch v20 passes with isolated-local receipt vr_bec1fadacf0f. Only the test-owned process was stopped. README/changelog and docs/PAGE-TRANSITIONS-0.4.11.md document scope; transport/gameplay suites were not rerun.

## 2026-10-19: 0.4.10 Back dissolve

Back/Escape from the Multiplayer root or connected room now returns to the title through GlobalCam's native Dither transition. The outgoing frame is captured with the custom menu camera into the native transition texture; the existing title capture hook supplies the destination including the purple artwork. Returning from a connected room preserves the connection. Internal page navigation, gameplay and programmatic loading closes retain their existing behavior.

Six focused runtime checks pass for transition start, outgoing menu capture, destination artwork, progressive blending, restored cameras and preserved hosting. The intermediate capture was visually inspected and saved as docs/back-dissolve-0.4.10.png. Checks invoke actual component handlers rather than physical mouse input. Both loader builds compile and installed hashes match. Both release packages pass Universal Modder lint with zero warnings; Watch v19 passes with isolated-local receipt vr_cb28c60cff03. Original game assembly is unchanged and the test-owned process was stopped. README, changelog and docs/BACK-TRANSITION-0.4.10.md document the behavior. No unrelated gameplay or transport suites were rerun.

## 2026-10-19: 0.4.9 artwork present during title dissolve

The user's dissolve screenshot showed missing Multiplayer art until the completed title. Replaced reliance on the half-second scan with MenuInitializer.Start/MainMenu.Start preparation, including inactive native menu children. Scoped label lookup to that menu and let its native parent control visibility. Title visibility now accounts for its active canvas/hierarchy and the profile-selection canvas.

Runtime evidence showed that early object creation alone did not populate the native dither destination: it could still contain the outgoing profile view. Added a targeted prefix at the Dither iterator's first resume, before its destination read, to force canvas geometry and render an incoming visible title. The hook skips the custom Multiplayer opening path. The artwork is now visible in the native destination and a captured dissolve frame; the latter was visually inspected.

Five focused title checks pass: inactive preparation, initial destination pixels (2,497 purple pixels in the artwork region), dissolve-frame artwork, returning destination after parent deactivation and exactly one button. The separate four-check artwork/raycast/opening-dissolve tour also passes on the final build. Both loader builds compile and installed hashes match; both mod-only packages pass Universal Modder lint without warnings. Watch v18 passes with isolated-local receipt vr_eb89291607d6. Original game assembly is unchanged. Only test-owned processes were stopped. README, changelog and docs/TITLE-TRANSITION-0.4.9.md document the fix and scope; unrelated gameplay/network tests were not rerun.

## 2026-10-19: 0.4.8 supplied title button artwork

Integrated the user's supplied purple MULTIPLAYER image from Downloads after confirming it matches the attachment. Source PNG is copied unchanged to assets/multiplayer-button.png, SHA256 078C543F6796B92859BA49FBE120F899960C21C8313240BF1D9C8645AAB3A2A5. Both loaders embed it. Sprite rectangle (9,183,473,127) excludes the large transparent margins. A 180x48 aspect-preserving Image replaces the plain title label above the copyright; the root Image carries the native button/raycast tag while a hidden child label retains the font reference used by other menus. Native hover feedback and existing dissolve/menu action remain.

Four focused runtime checks pass for sprite/transparency, native GraphicRaycaster center hit, menu opening and dissolve completion. The initial ray probe sampled the bottom pivot rather than the rectangle center; corrected the test coordinate. A startup capture initially showed the outgoing profile screen; the final tour waits for the native transition and renders the actual title. Final title artwork capture was visually inspected. Tests invoke components/raycast APIs rather than physical mouse input. Both installed hashes match builds, both packages pass Universal Modder lint without warnings and Watch v17 passes with isolated-local receipt vr_d4968c209d61. Original game assembly hash remains unchanged; the test-owned process is stopped. README, asset credits, changelog and docs/BUTTON-ART-0.4.8.md document the supplied artwork and scope. No gameplay or transport tests were rerun.

## 2026-10-19: 0.4.7 planned options UI

Added Player Options on the Multiplayer main page outside the room, and Lobby Options inside the room. Player labels cover name tag visibility/size, player color/icon and connection display. Lobby labels cover shared YTP, individual deaths, spectating, shared lives, revival, friendly item effects, map discovery and mid-run joining. All feature names are noninteractive static text under Coming Soon. No option values, persistence, network payloads or gameplay rules were added. Back/Escape preserves the appropriate parent and existing room. Room navigation hit rectangles were reduced to 28 pixels high to keep the added row separate.

The explicit options lab tour exercises actual menu component handlers. Six checks pass across page contents, navigation, unchanged player values, preserved hosting and both 800x600 layouts. Player/Lobby captures were visually inspected; physical input was not automated. Both loaders compile and installed hashes match. Both mod-only packages pass Universal Modder lint without warnings. Watch v16 passes with isolated-local receipt vr_79c6777b76b0. Original game assembly is unchanged and the test-owned process is stopped. README/changelog and docs/OPTIONS-0.4.7.md record the placeholder scope and anti-slop delivery gate. No gameplay or transport suites were rerun for this UI-only change.

## 2026-10-19: 0.4.6 native multiplayer elevators

Inspected ElevatorScreen, GameLoader, BaseGameManager, MainGameManager and PitstopGameManager. Client loading previously destroyed elevator screens and forced BeginPlay. Clients now create the native screen for each host destination and let its introduction, doors, StartDelay and ZoomExit run. Host screens finish normally too. The existing generation-scoped Begin packet is sent when the host's elevator starts; clients wait for their own validation and that cue, then exit automatically. Direct entry remains a fallback for destinations with no screen. No protocol fields changed. Host progression still does not wait for all peers; results panels and field-trip presentation are outside this change.

Focused cross-loader testing exposed duplicate Harmony installation on MelonLoader: its automatic pass and the mod's explicit pass both applied the elevator guard, causing the second invocation to cancel the first. Added HarmonyDontPatchAll and an idempotent explicit patch install during early loader initialization, with runtime Unity boot still late. The lab warning-screen advance also waits for a usable camera. Native timer changes used during diagnosis were removed; the game's timer remains intact. Unfinished client screen replacement clears the Singleton before instantiation, and disconnect removes the owned screen.

Final BepInEx host / MelonLoader client tour used an eight-second client validation delay and tested initial MainLevel_1 entry, restart into Pitstop and return to MainLevel_1. Eight checks pass: three native exit/BeginPlay sequences, validation ordering, screen cleanup, host progression, matching worlds and a single patch installation on each loader. Final logs contain no exceptions, rejections or mismatches. Both installed DLL hashes match builds; the original Assembly-CSharp hash is unchanged. Both mod-only packages pass Universal Modder lint without warnings. Watch v15 passes with isolated-local receipt vr_690b05c70782. Test-owned processes were stopped. README/changelog and docs/ELEVATORS-0.4.6.md record behavior and scope; unrelated gameplay suites were not rerun.

## 2026-10-19: 0.4.5 remaining sprite camera-order defect

User screenshots showed that 0.4.3's material change was insufficient. Perspective probes with one camera still passed, confirming the earlier fixture missed the relevant rendering path. Inspected GameCamera and loaded prefab settings: MainCamera mask 405332247 includes default layer 0 but excludes layer 9; BillboardCamera mask 512 selects layer 9 and preserves depth (Nothing / URP clearDepth false). Native NPC renderers use layer 9; remote visuals were left on default layer 0. This split allowed the later NPC camera pass to paint over a nearer remote player.

PlayerSpriteSheet now retains the native renderer layer alongside its material. RemotePlayerSprite applies that layer to its visual child. An isolated perspective camera-pass fixture uses the actual remote component, native Baldi sprite and native camera masks/clear behavior. It reproduces the defect with the same native material and old layer (near player matches 0/413 overlap pixels), then passes with the corrected layer (413/413). NPC-front is 735/735; transparent holes are 91/91. All five focused checks pass, including native routing. The fixture is not a full multiplayer gameplay session. Exploratory perspective-only probes were removed from the historical flat-camera test.

Both 0.4.5 loaders compile and installed hashes match. Both mod-only packages pass Universal Modder lint without warnings. Watch v14 passes with isolated-local receipt vr_3b1969a53bf6. Original game assembly remains unchanged. Only test-owned processes were stopped. README, changelog and docs/SPRITE-STACK-0.4.5.md explain the correction; historical 0.4.3 documentation now identifies its test limitation. No transport or unrelated gameplay suites were rerun.

## 2026-10-19: 0.4.4 native menu dissolve

Inspected cached game code for MainMenu.ActivateTransition and GlobalCam.Transition/Dither. The game captures the old menu, waits a frame for the destination and advances sixteen dither steps at 0.01666667-second intervals. Multiplayer's separate screen camera bypassed that path. Opening from the title now calls the actual native transition and temporarily routes the menu camera into its render target. The title camera pauses immediately so it cannot overwrite the destination; GlobalCam resumes it at transition end. Multiplayer then restores its ordinary screen target and pointer, keeping native title input blocked. Gameplay F5 is unchanged.

The first fixture exposed a title-camera overwrite: the captured destination was still the title. Pausing that camera and rendering the destination immediately corrected it. Final isolated BepInEx evidence passes all four focused checks: native start, progressively changed pixels, menu destination and restored target/pointer. Intermediate and completed textures were visually inspected and the dither capture is in docs. The test uses actual native transition textures; physical mouse input and unrelated suites were not rerun.

Both 0.4.4 loader builds compile and installed DLL hashes match. Both packages pass Universal Modder checks without warnings. Watch v13 passes with isolated-local receipt vr_462c6692ea4e. Original game assembly hash is unchanged; the test-owned process was stopped. README, changelog and docs/TRANSITION-0.4.4.md include the behavior, evidence, scope and anti-slop delivery gate.

## 2026-10-19: 0.4.3 player sprite overlap

The reported player/NPC overlap was reproduced with an isolated Unity GPU fixture using the supplied player artwork and the actual Baldi sprite. PlayerSprites used Sprites/Default, while Baldi, Playtime and Principal use SpriteStandard_NoBatching at the same sorting layer/order. Player sprites now clone the native NPC material, preserving native depth, transparency, lighting and billboard settings; artwork and animation crops are unchanged. No network or NPC AI code changed.

Four focused checks pass: reproduce the legacy player-front defect (3,841/4,158 matching overlap pixels), correct NPC-front and player-front rendering with the fix (3,090/3,090 each), and transparent holes revealing the character behind (837/837). The title-scene fixture amplifies test-only material color multipliers to compensate for absent level lighting; production keeps native settings. Rendered results were inspected. This is isolated GPU evidence, not a full two-player gameplay test.

Both 0.4.3 loaders compile and installed copies match build hashes. Both mod-only packages pass Universal Modder lint with no warnings. Watch v12 passes with isolated-local receipt vr_249276da735c. Original Assembly-CSharp hash remains unchanged. Only test-owned processes were stopped. README, changelog and docs/SPRITE-DEPTH-0.4.3.md document the fix and limits; transport and unrelated gameplay suites were not rerun.

## 2026-10-18: recon

Game root is the parent of this folder. No pre-existing loader or mod source found. BALDI.exe is a Windows x64 Unity 2020.3.49f1 player. `BALDI_Data/Managed/Assembly-CSharp.dll` SHA-256: `635b0d164a7050942077f3a7509ad2e4dc49a4d860e476ebe6906c7508dfa367`; MVID `7363135c-7fb5-4576-b9e8-fc6e927f2401`.

REA artifact evidence: `ev_2d6f0f6c945d96a4126e2f2b81f276c84e2945bd95e3cf15de394c90bbf16d88`. Selected-member evidence: `ev_11742cbafcaca73d23b793c3a880b8a64ae6b99185537b16474d6d3c80d048d0`. Static metadata coverage complete for selected types, no runtime behavior established by this inspection.

Route: loader API plus Harmony patches, two loader-specific entry assemblies and a common runtime. LAN TCP with bounded binary frames and per-peer queues; Unity changes stay on the main thread. Both loader distributions are development dependencies. Never activate both loaders in one game copy.

Initial restricted-network Git downloads failed. Retried with approved network access. Empty failed checkouts remain in `toolkits/universal-modder` and `toolkits/watch-skill`; complete checkouts use `-source` suffixes. No existing user files removed.

Existing .NET Framework compiler and .NET 8/10 runtimes found, no SDK. Use the framework compiler if compatible; inspect actual loader assemblies before choosing framework targets. Private decompilation uses local ILSpy 9.1.0.

## 2026-10-18: implementation and verification

Implemented original C# transport, bounded binary protocol, host/join overlay, roster, same-game/mod-version handshake, seed overrides, world hashes, 20 Hz player poses, 10 Hz world snapshots, host-validated interactions, activity completion, door states, NPC transforms, shared catches and host-driven scene loading. Separate loader entry assemblies compile against their own Harmony references. Wire protocol is version 2.

BepInEx 5.4.23.5 installed in the actual game root. Installer manifest and backup directory: `private/backups/20261018-140737-BepInEx/`. No save folder existed at the detected path in this environment. The separate `private/melon-lab/` uses hardlinks to original game files plus MelonLoader 0.7.3. Never edit those hardlinked bytes. Original game assembly SHA256 remains unchanged.

The first game launch could not create the normal AppData save path under the sandbox. Added explicit `-bt-lab` test mode: selected game save-path calls are redirected into each game copy's workspace, and preference writes in selected classes are suppressed. Normal launches do not use these lab patches. Native Windows Computer Use failed with an unavailable native pipe after retry/reset; Unity ScreenCapture provided inspectable game-only screenshots instead.

Fixed issues observed during real runs:

1. MelonLoader's legacy Harmony namespace required the explicit `HarmonyLib.Harmony` type.
2. ILSpy's update check stalled in the restricted network; used `--disable-updatecheck`.
3. A heartbeat could precede the client's Hello; client heartbeats now wait for Welcome.
4. Old socket threads could close a newly reused peer ID; teardown now checks the exact Link instance. Added a rapid-reconnection test.
5. GameReady is set after BeginPlay; initial world matching uses CoreGameManager.readyToStart, and the host sends a Begin message.
6. A pitstop needs CoreGameManager.nextLevel. Start packets now include next-level and run metadata; clients cancel old coroutines and let the host own transitions.
7. Activity results must reach the host before clients can collect unlocked notebooks. Added result requests and activity snapshot state, including inactive notebooks in the registry.
8. Room HUD position was moved away from the notebook counter, with explicit text/background contrast.

Fourteen transport checks passed. A BepInEx host plus MelonLoader client passed all eight live checks in `artifacts/tests/live-results.json`: connection, matching floor, poses both ways, notebook, door, activity result, NPC frames and matching pitstop transition. Two additional malformed-peer cases were rejected without disconnecting the valid player. The activity test exercised NoActivity completion, not every puzzle. Temporary capsule avatars were visually inspected in a real host screenshot.

Watch Skill 1.4.3 standard extras installed in `.venv/`. The default doctor initially tried to write outside the workspace; `WATCHSKILL_DATA_DIR` now scopes storage locally. Its automatic yt-dlp download stalled; a bounded direct official release download completed. The final doctor report says `ok: true`. Isolated local verifier receipt `vr_49765058530e` passed the 14-check build/report contract. Toolkit pins and configuration limits are in docs/TOOLKITS.md.

README distinguishes experimental implemented features from missing comprehensive game-system replication. Packaging includes only our DLL, README and license, with one ZIP per loader. Native click-through, actual separate-PC LAN testing, complete floor progression, all activity types and item/NPC-specific effects remain future work.

Final Watch Skill receipt: `vr_6d59441065de`, isolated-local pass. Final no-lab BepInEx startup loaded the mod and opened a room successfully. The final lobby screenshot is `docs/lobby-preview.png`. Game-side saves still report sandbox restrictions in that non-lab test; loader/mod startup is clean. All test-owned processes have been closed, port 27015 released, and local lobby defaults restored to Player/127.0.0.1/27015/12345. The installed DLL matches the release artifact, and the original game assembly hash is unchanged.

## 2026-10-18: 0.2.0 improvement pass

Reviewed current gameplay gaps and searched for multiplayer comparison ideas. Decisions and references are in docs/IDEAS.md. REA selected Pickup and ItemManager metadata evidence: `ev_a2ce60a95723c1e85cc6e9a33d8be98c2aefe4252c13df392bb497701747cf33`, bound to the same game assembly. No other mod code was copied.

Added shared initial free inventory pickups with a host-owned claim ledger, recipient grants and acknowledgements. The ledger prevents two owners, permits one outstanding request per player, rejects foreign/duplicate acknowledgements, and releases a declined grant. Empty-slot checks avoid overwriting personal items. Shops, swaps, dynamic drops and item effects remain separate. A disconnected unacknowledged grant stays consumed to avoid recreating an already delivered item.

Added G-key team location pings (eight-second duration, one per player, 0.75-second rate limit), round-trip latency, a host loading barrier, elapsed-time ready retries, clearer rejection reasons sent before socket close, and item-use blocking while the lobby is open. Moved these features into CoopFeatures.cs and the claim ledger into CoopState.cs. Protocol is now version 3; mod version 0.2.0. Packaging derives its version from Runtime.cs and retains older archives for rollback.

Twenty-two transport/claim checks and fourteen actual two-process game checks passed. The live test exercised a single shared Quarter, duplicate/full-inventory refusal, team ping delivery, latency and a deliberately three-second delayed client, in addition to the original checks. The host/client progressed into matching Pitstop scenes. Details are in docs/VERIFICATION-0.2.md. Watch Skill isolated-local receipt `vr_03798966ce4d` passed the new report/artifact contract. Lab test launches now skip saving the normal lobby preferences.

A fresh verification run then exposed a real placement mismatch: the game's LevelGenerator calls `PlaceItemInRoom(..., new System.Random())` for room item lists, so the normal seed does not fully determine item placement. Replaced local-item assumptions with an authoritative host catalog. Clients hide their initial free inventory pickups, reuse or create the host's placements/assets through EnvironmentController.CreateItem, and verify a separate catalog digest before Ready is accepted. Layout/canonical catalog ordering uses ordinal comparison. Protocol is now version 4.

Added an explicit regression perturbation: the client test removes one local item before reconciling the host catalog. The final fresh run passes fifteen live checks, including repaired item catalog equality, plus the existing twenty-two transport/claim checks. Four rejection cases deliver readable reasons, and the valid client remains connected. The inspected `docs/coop-0.2.png` shows the granted Quarter, G-key help and latency. Current verification is in docs/VERIFICATION-0.2.md and the final Watch report is docs/watch-0.2-final.json. Older 0.2 draft receipts remain historical and are not the current contract.

Final 0.2.0 Watch receipt: `vr_bab679c38ecb`, isolated-local pass. Both four-file mod packages passed Universal Modder lint with zero failures or warnings. Test-owned game processes are closed and the lobby defaults restored. The installed BepInEx DLL matches the current release artifact; the original game assembly hash is unchanged.

## 2026-10-18: 0.2.1 menu and lobby

User requested a Multiplayer entry above the Basically Games logo and a better lobby. Added a native TextMeshPro/StandardMenuButton using the installed game's font, canvas scaling and cursor underline. The title entry is parented to Copyright and aligned above the publisher name. The panel starts closed and the old offline top banner is removed.

Moved UI into MenuUi.cs. The centered cream modal has a dark heading, Host/Join tabs, only relevant fields, white input interiors, visible focus, a fixed four-player roster and separate back/leave actions. It fits 800x600 without scrolling. Opening the panel captures cursor state, hides and blocks the game cursor, then restores its previous hidden/click state on close. Fixed Connect's main-menu detection: MainMenu can coexist with an initialized CoreGameManager. Join no longer validates the host-only seed.

Both loaders build. A fresh run passes 22 transport/claim checks, 15 two-process game checks and four rejection probes, retaining the accepted client. The explicit UI lab invokes the actual menu button Press handler and tab/connect/close handlers, captures desktop and 800x600 states, and opens/leaves a separate port-27019 room. Native physical click-through is not claimed. UI captures, review scope and remaining hands-on checks are in docs/UI-0.2.1.md. Protocol stays at 4; all players need mod 0.2.1.

Watch Skill isolated-local contract v5 passed with receipt `vr_2edd3b68b2ac`. It checks the fresh regression reports, both builds and the six inspected UI captures. The installed BepInEx DLL matches the 0.2.1 artifact; the original Assembly-CSharp.dll hash is unchanged. Test-owned host/client/UI processes are closed. Packages include the UI screenshots and review document so the README preview links work after extraction.

## 2026-10-18: install Reverse Skill

User requested zhaoxuya520/reverse-skill. Installed its official Codex adapter with the system skill-installer helper into `~/.codex/skills/reverse-skill-router/`. Downloaded the complete repository under `toolkits/reverse-skill/` and configured the adapter's local resource path. Both are pinned to `cab634bd855fc287f6e420c1f36fd1a6b9245960`. Verified the router, routing config, .NET/C# specialist module, script entry points and license are present. No package bootstrap/analysis scripts were run. README and the toolkit record now include the new skill; game code and binaries are unchanged.

## 2026-10-18: 0.2.2 supplied player sprites

User supplied the Gemini/Photoroom transparent character sheet and requested it as the player sprite. Copied the original 720x1475 PNG without editing its bytes; SHA256 is `F573A3CC87E0EA6A7DA304C4B4DF7B02D228F592CEFF4384267DDFBDC7B71A56`. Both loader DLLs embed that same PNG resource. Source attribution is in assets/README.md and packaged as ARTWORK.md.

PlayerSprites.cs now loads sixteen frames once, excludes the sheet's left captions, maps four views, billboards toward the local player's camera, advances walking by traveled distance and restores idle after movement stops. Frame pivots align to the floor, remote names stay above the head, and scene changes/teleports reset motion. Pose yaw comes from the local camera base. Each remote avatar shares the sheet/material and has no collision primitive; resources are cleaned up at runtime destruction. Protocol stays at 4, mod version is 0.2.2.

Added opt-in lab sprite touring and four runtime checks alongside the original fifteen. Test facing is supplied explicitly in pose messages so game input cannot overwrite the test direction. Diagnostic capture initially returned black frames, then a stale name-menu texture. It now renders the live player camera into a temporary texture after the frame and restores camera/render targets. The inspected docs/player-sprite-0.2.2.png shows the supplied character in the school, with transparent background, no sheet captions and grounded feet. It is a world-camera render, without IMGUI HUD/lobby, rather than a native input recording.

The final fresh BepInEx/MelonLoader run passes all 19 live checks, 22 transport/claim checks and four rejection probes, retaining the accepted client. Watch Skill isolated-local contract v6 passes with receipt `vr_693d0e357a97`. The installed BepInEx DLL matches the final build and the original game assembly hash is unchanged. Test-owned game processes are closed. README, changelog, asset record, design notes and sprite verification are current.

## 2026-10-18: 0.3.0 focused synchronization

User requested a synchronized hiding countdown, quieter name labels, synchronized shops and Baldi chasing the closest player, with only necessary tests. REA inspected HappyBaldi/Baldi/chase/navigation/Pickup metadata against the unchanged game assembly, evidence `ev_b10d5c6bd4bccaf4a58ac1a43e86eade665f836ee95783aa2bfcfcd4c0baa778`; existing private decompilation supplied method details. The shop work was delegated under REA's staged-workflow instructions; no external messages were sent.

HidingSync.cs owns countdown start, every audio/animation beat and NPC release on the host. Clients suppress the independent game coroutine and apply repeated authoritative state without replaying duplicate beats. Normal Baldi chase navigation selects the nearest ready connected pose, expires stale positions and prevents host-only hearing from overriding the destination. Apple/praise/attack behavior is not retargeted. Remote catch checks now exclude stale poses and require a clear world line. Names use small shadowed text, aim/distance fading and world occlusion, replacing the large boxes.

ShopSync.cs/ShopPatches.cs provide host stock/prices, shared YTP, stock/fund reservations, buyer grants, delivery acknowledgements/refunds, native Johnny callbacks, map sharing, personal sticker/glue rewards and host restocking. A focused run exposed locally generated shop prices being used before the first current-level host snapshot. Reset snapshot state per level, block early interaction and validate quoted item/price before charging. The final isolated purchase cost is 300 YTP, with one refund, two refusals, no reservations and equal host/client balances of 9700. General client-only YTP effects, legacy StoreScreen and local visitor dialogue remain documented limits.

Both 0.3.0 loaders compile. Seven focused live checks pass: countdown alignment, eleven beats plus release, actual nearest-player navigation switching in both directions, stock replication, one purchase, a filled-inventory refund and exact-charge/duplicate protection. The 22 project-required transport/claim checks ran once after the wire change. No broad gameplay/sprite rerun, extra malformed-peer probe suite or name-style screenshot suite was performed. The forced lab pitstop transition logs transient ItemManager.Update teardown errors; this is recorded in docs/SYNC-0.3.md rather than claimed as clean general progression evidence. Names were source-reviewed. Protocol is now 5; everyone must use 0.3.0.

Watch Skill focused contract v7 passes, isolated-local receipt `vr_a3fc77924905`. The installed plugin matches the release artifact, the original game assembly hash is unchanged, and test-owned game processes are closed. README, changelog, shop notes and focused verification are current.

## 2026-10-18: 0.3.1 automatic entry and additional world sync

User requested random multiplayer seeds, host-led entry without waiting for every player, smoother Baldi and additional missing synchronization. REA selected metadata for BaseGameManager, ElevatorScreen, GameLoader, Window, SwingDoor and ITM_YTPs; evidence `ev_3d1752add3124a41f557a701bd7b8830da72795d27b56c009b315446bf89a72d`. Existing private decompilation supplied the native loading, restart, window and door method details. No additional agents were started for this change.

Host GameLoader initialization now chooses a random seed for each new run. Floors/restarts retain it; the legacy seed field is removed from normal UI. Host entry waits only for its own registry, while each client retains the host begin signal until its own layout/catalog acknowledgement. BeginPlay dismisses multiplayer elevator screens/coroutines. Slower clients can miss early countdown seconds. NPC replicas interpolate positions and yaw each render frame, with bounded timing and large-teleport snapping, instead of moving only on ten-Hz packet receipt.

Additional snapshots cover host window breaks/open state and swing-door open/lock state with native map/material/collision replay. Fresh nearby remote poses open unlocked swing doors on the host. An initial focused run exposed collocated swing-door components; hierarchy-derived identifiers now distinguish them and join layout hashing. Client window-breaking/locking item effects remain unsupported rather than granting arbitrary client authority.

Final BepInEx/MelonLoader flow run passes eight targeted checks, including an eight-second delayed client, per-frame NPC motion, break/lock/unlock replay and the native host RestartLevel path into the pitstop with both games automatically active and seed 139266 retained. No final game-code exceptions were found; the environment emits a cursor-confinement warning. The required 22 transport/claim checks passed after protocol 6 changed snapshots. The broad sprite/shop suites were not rerun. README, changelog, ideas and docs/SYNC-0.3.1.md record implemented scope and remaining gaps. Final text-only loading-message adjustment is compiled without another live run.

Both final loader builds are installed in their respective game copies and packaged as mod-only 0.3.1 ZIPs. Watch Skill focused contract v8 passes with isolated-local receipt `vr_49b709286646`. Test-owned processes are closed; the original Assembly-CSharp.dll hash remains unchanged.

## 2026-10-18: 0.4.0 NPC synchronization

User requested more necessary game synchronization, especially NPCs, based on actual game code. Applied REA's staged workflow and delegated the independent presentation and Principal questions; root handled spawning, Playtime, Bully, movement effects and integration. Selected managed evidence is `ev_a8c9e13c485f8421ba6244fe1f233271ba2a3991f18d6fc89a5d96fd994dbd45`, summary `ev_463732f162e25eb7b943bd163a7fc60ca626d47dea2e83d04cba7cc44ea023a7`, against the unchanged 0.14.5 assembly. Private decompilation supplied NPC, Looker, EnvironmentController, Playtime/Jumprope, Bully, GottaSweep, FirstPrize_Active, Principal, DetentionRoomFunction and Entity details.

Found independent client Looker callbacks and player-dependent native spawn scheduling could run despite the existing NPC.Update suppression. Clients now suppress Looker and native spawn queues, create matching prefabs in host order, and remove departed host entries. World snapshots carry semantic visibility/interaction, bounded animator layers/parameters and loaded game sprite references; replica animation events are disabled. Game assets are referenced in memory and never bundled.

Remote Playtime contact reserves one owner, starts native UI only there, validates count/hit/end reports against the owner/token and awards points once on the host. The host uses a separate remote state without a fake local PlayerManager. Bully contact commands run personal native theft or push, deduplicate tokens and hide on acknowledgement. Sweep and 1st Prize movement modifiers replay near remote players with stale-state/separation/teardown cleanup; 1st Prize's native charge routine accepts visible remote targets. Principal follows bounded guilt reports with host sight/sensitivity checks, remote chase and culprit-only detention, shared room locks/clocks/gauge and corrected snapshot timers. Native host-player detention clocks are shared too.

Focused lab development exposed test setup issues: placing Baldi with the stationary host ended the run, so explicit NPC lab mode suppresses test deaths; command file readers/writers collided, and atomic File.Replace was denied by Windows, so the final handoff uses exclusive access with next-frame retry; instantaneous movement attachment could occur between one-second diagnostic samples, so tests use the counter advanced only after real modifier attachment; detention samples are projected to a common UTC instant to avoid false drift from staggered diagnostics. These lab corrections do not change normal game rules. No physical input automation or natural multi-floor NPC playthrough is claimed. The remaining per-NPC limits are in docs/NPC-SYNC-0.4.md.

Additional targeted REA metadata for FirstPrize, FirstPrize_Active, Jumprope and DetentionRoomFunction is retained as `ev_e0d841409f8af78cc92873cf9a5e6aaa01c2073174f21051dab21050ae9e317a`, summary `ev_12de84fb1533ae649642b3c80c1b6944a16d14550a2f8035c80c4fc9f7be5842`; all selected types matched the installed assembly.

Final focused run passes all 13 NPC checks, with eight matching configured NPCs, seven after host despawn, one owner-only rope, 25 shared YTP once, one theft despite a duplicate command, force attachment/cleanup, visibility changes, culprit-only detention and corrected clocks. No exceptions appear in either final player log. The 22 required transport/claim checks passed after protocol 7 changes; no unrelated full gameplay/shop/sprite rerun was performed. Both loader builds compile and match their installed game-copy DLLs. Watch Skill v9 passes, isolated-local receipt `vr_1725df87eefd`. Mod-only 0.4.0 packages pass Universal Modder checks. Original game assembly hash remains unchanged; test-owned game processes are closed. README and the NPC scope matrix document remaining abilities and verification limits.

## 2026-10-19: 0.4.1 Baldi-style Multiplayer menu

User asked for multiplayer UI to be the Multiplayer menu like Baldi's own game. Kept the existing native Multiplayer entry above the copyright, replaced the IMGUI card/forms with native TMP text and StandardMenuButton choices, a white game screen, existing cursor artwork and native highlight/confirm sounds. First screen offers Host Game, Join Game and Back. Host/Join screens use inline editable values; the connected screen uses simple player rows, Back and Leave. Removed the top gameplay banner. Text entry, masked codes, numeric port filtering, keyboard focus and back navigation share the actual menu handlers. Gameplay input blocking remains in force while the menu is open. No new art assets or agents were used.

NativeMenu.cs renders the actual UI on a dedicated game-menu camera and handles pointer hit testing against its GraphicRaycaster, preserving the cursor artwork's hotspot offset. Initial direct screenshot attempts encountered a stale game render target or black backbuffer. Final evidence renders the menu camera after the frame and restores render state. The 800x600 preview uses an explicit render target and equivalent CanvasScaler factor, not an assertion that the hidden window resized. Source was edited with apply_patch. Anti-slop during mode follows the existing user choice; its evidence-backed gate is in docs/UI-0.4.1.md.

Both 0.4.1 loaders compile without warnings. The isolated BepInEx component-handler tour covers all menu/field buttons, text/Backspace, port filtering, masked code, Join fields, invalid port, actual hosting, Back preserving the room, Leave closing it and small-layout bounds. Native menu, Host, Join, room and true 800x600 captures were inspected. Physical mouse/controller input was not automated. No transport/NPC/shop/sprite regression suite was rerun for this UI-only change; protocol remains 7.

Final UI results are 11 passed, 0 failed. Watch Skill v10 passes with isolated-local receipt `vr_dbd31ed32ee9`. Both installed DLLs match the release builds; mod-only packages pass Universal Modder checks. The game assembly is unchanged. Test-owned processes are closed, previous plugin DLLs are preserved under private/backups, and the completed delivery gate plus screenshot evidence are in docs/UI-0.4.1.md.

## 2026-10-19: 0.4.2 native aspect frame

User supplied main-menu and Multiplayer screenshots showing that the custom menu lacked the game's black aspect borders. Inspected the existing private GlobalCam.UpdateResolution and UiToRealScreenPosition code plus the retained native menu hierarchy. Native reference area is 480x360, with Border rectangles outside +/-240 horizontal and +/-180 vertical. The 0.4.1 canvas instead used a 640x360 reference and a stretched white background.

Changed NativeMenu to the native 480x360 Expand reference, a fixed white area and a full black surrounding frame. Pointer clamping uses that area; the existing menu raycaster maps button clicks and ignores outer-margin clicks. Capture scaling uses the same reference. A dedicated aspect tour renders 1920x1080, 800x600, 2560x1080 and 600x800 and checks white-frame bounds, black/white pixels, real button-center rays and outer-margin rays. All eight frame/input checks pass. Wide and classic captures were visually inspected: 240-pixel side bars at 1920x1080 and no bars at 800x600. No physical mouse/window-resize automation is claimed, and unrelated gameplay/network suites were not rerun.

Both 0.4.2 loaders compile and are installed in the respective game copies; mod-only packages pass Universal Modder checks. Watch Skill v11 passes with receipt `vr_c3af42139ae5`. Original game assembly hash is unchanged. Only the new aspect-test process was closed. The player's pre-existing BALDI process was deliberately left running and needs a restart to load the new DLL; the previously installed DLL is preserved in private/backups. README, changelog and docs/ASPECT-0.4.2.md record the fix and verification.
