# Changelog

## 0.9.3, 2026-10-27 (local build, not uploaded yet)

- Challenges: winning a challenge (Grapple, Speedy, Stealthy) shows the win screen in every player's game. Before, only the host saw it, and the host's return to the menu dropped everyone mid-floor with "Disconnected from host". Teammates now treat the host leaving after a win as the end of the run.
- Endless:
  - notebooks can be collected again after they come back. The co-op notebook check counted a notebook as gone forever once collected;
  - notebook reset timers and item respawns are the host's. A teammate's game skipped neither and picked its own random notebook and item, so the floors drifted apart. Teammates now get the host's resets and respawned items;
  - when the whole team is caught, every player sees the Endless results screen (score and high score). Before, only the host did, and the others were left on a black screen.
- The host's map and inventory challenge settings now reach every player.
- Lab: `launch-lab.ps1 -Scene <scene>` starts any scene (challenges, Endless) instead of floor 1, and `-ModeSync` with `tests/verify-modes.py` checks a challenge win (5 checks) or an Endless run (8 checks). The `-NoModeSync` and `-ClientResets` mutants must fail them. All four runs are in the lab suite.
- Protocol 16: everyone must update to 0.9.3.

## 0.9.2, 2026-10-27

- Steam rooms only accept players who are in the host's Steam lobby. Anyone who knew the host's Steam ID could open a connection to it before. A caller who is not yet listed gets 5 s for the lobby list to catch up, then is turned away.
- Random events stay with the host. A client's game no longer starts its own event timers, which run on their own random schedule (a challenge scene starts them as it loads). The host's time-limit challenge setting now reaches every player, so TimeOut matches.
- Random events reach everyone: the host's event jingle and Baldi TV intro play for every player. Fog, the Mystery Room, the broken ruler and the after-hours TimeOut (rule break, dimming lights, music) now happen in every player's game, starting and ending with the host's. Other events stay with the host; their NPCs and doors already appear for everyone.
- Lockdown doors (the event's walls and the lever-held doors on some floors) open and shut for everyone, and a teammate's lever press reaches the host.
- Field trips: the Pitstop's trip room is picked from the run seed, so every player gets the same Pitstop. Before, two players could get different trips and the floor would fail to match.
- Lab:
  - the repo can live outside the game folder (`scripts/paths.ps1`, `-bt-workspace`, `-bt-shared`);
  - lab copies start minimized as small titled windows that restore at the right edge, at low priority and quality (`scripts/lab-window.ps1`, `-bt-lab-light`);
  - new checks: `steam-lobby-gate` (with the `-bt-lab-no-lobby-gate` mutant) and `client_events_host_only` (with the `-ClientEvents` mutant).
- Protocol 15: everyone must update to 0.9.2.

## 0.9.1, 2026-10-26

- Fix: glue sticks now have a price per player, as in the game: 1000 YTPs plus 500 for each glue stick that player already owns, sold out once they own 4. The shared shelf used to show the host's price and stock to everyone.
- Fix: a shop purchase whose confirmation arrived after the host's 12 s deadline was never settled. If the item couldn't be placed, the team lost its YTPs and the item. Late confirmations are now honoured for 2 minutes: a failure refunds and puts the item back, and a success completes the sale.
- Fix: when Mrs. Pomp caught a client right after she spawned, the appointment could reach the client before its copy of Pomp existed. The client dropped it silently: no appointment, no reward, and the host gave up after its timer. The client now holds the appointment for up to 5 s until Pomp appears.
- Lab:
  - two-process tests no longer stall when the client reaches the title before the host opens its room: auto-join now retries;
  - failed lab attempts keep their logs and reports in `private/failures/`;
  - new `scripts/shop-test.ps1` (plain, late acks, and two mutants), also run by the lab suite.
- Protocol stays 14; everyone must update to 0.9.1.

## 0.9.0, 2026-10-26

- Online play through Steam:
  - **STEAM GAME** has HOST ON STEAM (friends-only or public room), FIND STEAM GAMES, INVITE FRIENDS, and Join Game from the host's Steam profile or an invite.
  - It uses the game's own Steamworks and Valve's relay: no addresses, port forwarding or extra server.
  - No host migration in Steam rooms.
- Fix: a host whose game loop stalled for 15 s (loading, a hitch, possibly Alt+Tab) was treated as gone, and clients migrated. A stalled client was dropped the same way.
  - Links now send keepalives from the network thread.
  - Clients show "The host isn't responding" and wait up to 60 s.
- Fix: links were dropped at 512 packets waiting for the game or 128 waiting to send. Both limits are now by size (8 MB and 4 MB).
- Removed the unreleased global relay mode (ONLINE GAME).
- New tests:
  - a seeded TCP chaos proxy (`tests/tcp-chaos.py`) and `scripts/link-test.ps1`: host stall, outage and cut over `bad_wifi`, with a keepalive-off mutant that must fail;
  - `steam-tour` (16 checks);
  - transport keepalive checks with a mutation twin.
- Protocol 14: everyone must update to 0.9.0.

## 0.8.3, 2026-10-20

- The Hall Monitor spawns on about half of multiplayer floors, from a roll on the run seed and the floor that every player computes the same way. He used to appear on every floor.
- His poster now uses the native character-poster layout: framed portrait, description card and name plate, with the game's poster text boxes and font. It appears in the character-poster rooms whenever he is on the floor.
- Lab switches `-bt-hall-monitor` and `-bt-no-hall-monitor`; the NPC sync test always includes him. The Hall Monitor tour checks his poster in the floor's character-poster room (25 checks). Protocol stays 13; everyone must update to 0.8.3.

## 0.8.2, 2026-10-20

- Fix: a run no longer fails its level check when two soda machines share a position (for example seed 2101520539). Such a failure used to send the host back to the menu. Repeats are numbered in stable hierarchy order on every machine, and purchases use the registered key.
- Fix: a run that ends during loading no longer leaves its elevator screen behind, which threw an error in `ElevatorScreen.Update` every frame.
- New lab tools: `compat-fail-tour`, `scripts/focus-test.ps1`, `scripts/focus-same-folder-test.ps1`, plus `launch-lab -Visible`/`-Seed` and `run-tour -ExtraArgs`. Protocol stays 13; everyone must update to 0.8.2.

## 0.8.1, 2026-10-20

- Fix: only the host can start a run. A connected client pressing a title-menu run button (Hide & Seek New or Continue, challenges, Field Trip, Endless, tutorial) used to get an elevator screen stuck over the title menu. Its GameLoader calls were refused, but the button still opened the elevator. The whole press is now refused with a notice, and the client still joins the host's run.
- The LAN two-process test covers it (15 checks). Protocol stays 13; everyone must update to 0.8.1.

## 0.8.0, 2026-10-20

- New multiplayer-only NPC, the Hall Monitor:
  - He patrols the halls and writes up players he sees running. The mark lasts 25 s and shows as a HUD gauge.
  - When two marked players are within 1.5 tiles, he whistles, and Baldi hunts that pair for 10 s.
  - He is built from the native Principal prefab, spawns through the floor's own NPC list, and is placed on the character-poster walls.
- Generated artwork (procedural sprites and a poster) and sounds (Stable Audio 3 whistle and scribble, two synthesised voice lines).
- New `hall-monitor-tour` (22 checks); the two-process NPC test checks his replica and a real cross-loader mark. Protocol 13: everyone must update.

## 0.7.1, 2026-10-20

- Performance: the client uses the native ElevatorScreen singleton instead of a per-frame scene search, caches HappyBaldi per floor, caches TitleVisible per frame, and caches command-line flags. Client mod cost per frame fell from 1.4-3.3 ms to about 0.14 ms in the lab soak.
- New lab profiler (`-bt-profile`), two-player soak (`launch-lab -Soak`) and `tests/verify-perf.py` in the lab suite. Protocol stays 12; everyone must update to 0.7.1.

## 0.7.0, 2026-10-20

- Ability sync for Beans, Arts and Crafters, Mrs. Pomp, The Test, Dr. Reflex and Chalkles with remote players (host-authoritative, owner-applied native effects, validated client reports).
- Host relay for teammates' item effects on NPCs and the world (sprays, peels, cloud, WD-40, whistle, alarm clock, scissors, apple, door/coin/tape acceptors).
- Redesigned native spectate screen with roster, colour portrait, hiding and waiting states, and a short dissolve.
- Baldi's chase ignores cloud-hidden teammates. One failing feature no longer stops the others.
- Lab suite covers every feature (184 tour checks plus two-process tests). Protocol 12: everyone must update.

## 0.6.3, 2026-10-20

- Fix: the 3D ping sound now uses the game's sound-effect mixer, so it follows the SFX volume. It previously had no mixer group.
- Fix: selecting a coded room on FIND LAN GAMES opens the Join form even while a menu dissolve is still running.
- New automated lab suite (`scripts/lab-suite.ps1`): a lobby tour with 32 checks, a two-process LAN join test with 13 checks, and the existing spectator and audio tours. The spectator tour's Baldi-movement fixture is fixed. See docs/TESTS-0.6.3.md.
- Protocol stays 11; everyone must update to 0.6.3.

## 0.6.2, 2026-10-20

- Two more generated sounds: a paper whoosh when a spectator switches teammate, and a computer beep when FIND LAN GAMES lists a new room.
- Protocol stays 11; everyone must update to 0.6.2.

## 0.6.1, 2026-10-20

- New team sounds generated with Stable Audio 3 small-sfx: a desk-bell ping heard at the marker in 3D, xylophone chimes when a player joins or leaves the room, and a ruler slap when a teammate is caught. They play through the game's own sound-effect volume.
- Protocol stays 11, but the exact mod version changed, so everyone must update.

## 0.6.0, 2026-10-20

- FIND LAN GAMES lists open rooms on your network; select one to join it. Hosts broadcast a UDP beacon on port 27016, and room codes are never broadcast.
- Player Options has a working COLOR choice with eight colours. Teammates see your sprite recoloured, and the colour survives host migration.
- While spectating, your HUD shows the watched teammate's items, selected item and stamina.
- Protocol 11: everyone must update.

## 0.5.4, 2026-10-20

- Bug-fix pass from a source review. The fixes are listed in docs/FIXES-0.5.4.md.
- Gameplay fixes:
  - The Principal no longer freezes after a remote chase.
  - Playtime slows down after losing a remote target.
  - The Bully can no longer become permanently invisible.
  - Baldi hears noises when nobody is exposed.
  - Clients can restock Johnny.
  - Clients start the ambience timer only once.
- Network fixes:
  - No more ghost peers when the event queue is full.
  - Clients are not kicked straight after a host migration.
  - A successor that nobody follows disconnects instead of forking a solo game.
  - New players cannot join mid-migration.
  - Pickup grants and shop refunds are kept.
  - World-sync failures are contained.
- The F2 console and menu block item, pause and map input.
- Harmony patches install per class.
- Protocol remains 10, but the exact mod version changed, so everyone must update.

## 0.5.3, 2026-10-19

- Use Baldi's native sight raycast for remote capture instead of an all-layer obstruction check. Keep locker/elimination/ready/fresh-pose gates and wall occlusion.
- Add F2 `contacts` and capture-state details to saved reports. Protocol remains 10; exact-version matching still requires everyone to update.

## 0.5.2, 2026-10-19

- Synchronize soda-machine stock, buyer-only rewards and quarter consumption with request/receipt deduplication.
- Replicate native elevator results and preserve their animation across next-floor loading.
- Fade connection indicators with player names.
- Release countdown NPC/event queues once per environment; apply native spawn buffers to exposed living teammates while excluding locker occupants and spectators.
- Add the F2 read-only debug console and saved diagnostic reports. Protocol increases to 10; everyone must update.

## 0.5.1, 2026-10-19

- Handle individual capture at Baldi.CaughtPlayer before it enters Baldi_Attack; continue pursuit of survivors.
- Guard BaseGameManager.EndGame before its StopAllCoroutines side effect. Preserve the native ending on team wipe. Protocol remains 9.

## 0.5.0, 2026-10-19

- Individual elimination, teammate-only spectating, no revival before the next round and native team-wipe ending.
- LAN host migration with a fixed successor, identity remapping and bounded reconnects; active floors reload with the same seed and retained eliminations.
- Exact mod/game compatibility screen and supplied connection indicator artwork with measured/stale RTT states.
- Expand Bully collision/contact/spawn synchronization, Cloudy Copter wind forces and NPC sprite-child activation. Document every named installed NPC's remaining ability gaps.
- Protocol increases to 9; all players must update.

## 0.4.13, 2026-10-19

- Replace BAL_HideSeek with the user's new WAV only while multiplayer is connected.
- Embed the supplied clip in both loaders; clone sound metadata for replacement playback without editing original game assets. Protocol remains 8.

## 0.4.12, 2026-10-19

- Replicate locker occupancy with player poses and hide the remote sprite/name tag while inside.
- Restore remote visibility on exit and clear locker state on level reset. Native locker camera, interaction and sounds remain in control.
- Protocol increases to 8; everyone must update.

## 0.4.11, 2026-10-19

- Use the native dissolve for Multiplayer page buttons, internal Back, connection confirmation and Leave/Cancel.
- Capture the outgoing custom menu and render the destination into the native transition target. Restore the camera and pointer afterward; block navigation during the dissolve.
- Preserve immediate text editing and existing options placeholders. Protocol remains 7.

## 0.4.10, 2026-10-19

- Add the native dissolve when Back/Esc leaves the Multiplayer root or connected room for the title.
- Capture the actual outgoing Multiplayer camera for the old frame; keep the purple artwork in the incoming title capture.
- Preserve the room connection, camera targets and existing gameplay/internal-page navigation. Protocol remains 7.

## 0.4.9, 2026-10-19

- Initialize the title's Multiplayer artwork before native transition captures, rather than relying on the periodic menu scan.
- Preserve the button's own active state while its native parent is hidden, so returning transitions include it.
- Determine title visibility from its canvas as well as its controller. Protocol remains 7.
- Build the incoming title's canvas and render it before the native dither destination read; preserve the custom Multiplayer opening path.

## 0.4.8, 2026-10-19

- Replace the title-screen plain Multiplayer label with the user's transparent purple 3D lettering.
- Keep native cursor hit testing, hover feedback and the existing opening dissolve. Both loaders embed the original PNG; protocol remains 7.

## 0.4.7, 2026-10-19

- Add a Player Options placeholder page outside the lobby and a separate Lobby Options placeholder page inside the room.
- List planned appearance, shared YTP, individual deaths, spectating, shared lives, revival, friendly items, map discovery and mid-run joining options.
- Feature names are static, marked Coming Soon. Only page navigation is functional; gameplay and network rules are unchanged. Protocol remains 7.

## 0.4.6, 2026-10-19

- Restore native elevator introductions and exit animations on clients for initial entry, restarts and later destinations.
- Let native screens finish their own BeginPlay/destruction sequence. Automatic direct entry remains a fallback when no elevator exists.
- Signal the host's elevator start through the existing generation-scoped Begin message; clients wait for matching layout/item validation and then exit automatically. Protocol remains 7.
- Prevent MelonLoader's automatic Harmony pass from duplicating the mod's explicit patch installation and cancelling an allowed elevator start.

## 0.4.5, 2026-10-19

- Copy the native NPC renderer layer onto remote player visuals, putting players and NPCs in the same billboard camera pass.
- Correct the remaining gameplay overlap missed by the earlier single-camera material test. Keep the native material and supplied artwork.
- Add a focused camera-pass regression fixture using native camera masks, perspective projection and the actual remote-player component. Protocol remains 7.

## 0.4.4, 2026-10-19

- Open the title-screen Multiplayer menu through GlobalCam's native Dither transition at 0.01666667 seconds per step.
- Route the menu into the native transition destination, then restore its normal camera target and pointer. Menu clicks wait until the transition ends.
- Four focused runtime checks pass; both loaders compile. Protocol remains 7.

## 0.4.3, 2026-10-19

- Use the native NPC billboard material for remote player sprites, fixing overlap with other character sprites and matching native depth/lighting behavior.
- Preserve the supplied sprite sheet, transparent areas and sixteen animation frames.
- Four focused GPU checks cover the old regression, both depth orders and transparency. Both loaders compile; protocol remains 7.

## 0.4.2, 2026-10-19

- Match the native 480x360 menu safe area and black aspect-ratio borders.
- Keep pointer clamping and raycast targets aligned with the framed menu.
- Eight focused aspect/render/input checks pass; both loaders compile. Protocol remains 7.

## 0.4.1, 2026-10-19

- Replace the overlay card with a plain white Multiplayer game menu: Host Game, Join Game and Back.
- Use the existing game font, cursor artwork, StandardMenuButton underline behavior and menu sounds.
- Add inline editable fields, keyboard navigation, a simple player list and Back/Leave choices.
- Remove the top gameplay banner. Multiplayer synchronization and protocol 7 remain unchanged.

## 0.4.0, 2026-10-18

- Synchronize NPC spawn/despawn catalogs, semantic visibility, interaction, animation states/parameters and sprite changes.
- Suppress independent client NPC spawning and sight callbacks; the host owns NPC AI.
- Support remote Playtime pursuit and native jump-rope rounds, with owner-only UI and one host reward.
- Support acknowledged, duplicate-safe Bully theft from remote inventory.
- Replicate Sweep and 1st Prize movement forces; 1st Prize can charge remote players.
- Support Principal remote guilt detection, chase and culprit-only detention, with shared locks, clocks and corrected timers.
- Protocol 7 / 0.4.0 requires every player to update. NPC projectiles and other special mechanics remain incomplete.
- Both loaders compile; 13 focused NPC checks and 22 required transport/claim checks pass.

## 0.3.1, 2026-10-18

- Random host seed for every new run, retained through floors and restarts.
- Host starts without waiting for slower clients; clients enter automatically after their own validation, with no separate elevator ready step.
- Interpolate client NPC positions and yaw each frame; snap large teleports.
- Synchronize host window breaks and swing-door open/lock states; open unlocked swing doors for nearby remote players.
- Protocol 6 requires all peers to update. General client item effects remain unsupported.
- Both loaders compile; eight focused live checks and 22 required transport/claim checks pass.

## 0.3.0, 2026-10-18

- Host-controlled hiding countdown audio, counting animation and release; clients cannot start independent countdowns.
- Baldi targets the closest player, switches as distances change, ignores stale/disconnected poses and preserves special behavior states. Remote catches require clear world visibility.
- Smaller name text with a shadow, directional/distance fading and world occlusion; remove the large boxes.
- Synchronize Johnny's in-world stock, prices, sales/SOLD labels, shared YTP, buyer grants, map ownership and host restocking.
- Reserve stock and funds atomically; refund a failed delivery, reject duplicate purchases and validate quoted stock/price before charging.
- Wait for the current host shop snapshot to avoid local generated-price races.
- Protocol 5 and mod 0.3.0: everyone must update.
- Both loaders compile; seven focused live checks and the required 22 transport/claim checks pass. No full gameplay/sprite suite rerun.

## 0.2.2, 2026-10-18

- Replace remote capsules with the user's supplied transparent character sheet, embedded in both loader DLLs.
- Add front/back/left/right view selection and distance-based walking animation, with idle poses and teleport handling.
- Crop all sixteen frames at runtime, excluding the sheet's direction captions and preserving its original PNG bytes.
- Use the local player's camera for sprite billboarding and name labels.
- Capture diagnostic screenshots after rendering, avoiding the small render target sometimes left active during generation.
- Add an opt-in lab sprite tour and checks for sprites, all four directions, walking frames and idle poses.
- Keep protocol version 4; all participants need mod version 0.2.2.
- Both builds compile; 22 transport/claim checks, 19 live checks and four rejection probes pass.

## 0.2.1, 2026-10-18

- Add a native MULTIPLAYER menu button above the Basically Games copyright, with the game's font and cursor highlight.
- Replace the corner lobby with a centered, dimmed modal and separate Host/Join tabs.
- Show relevant form fields, roster rows, connection/loading status and distinct close/leave actions without a scroll bar.
- Preserve cursor state, hide and block the game menu cursor while the panel is open, and retain Tab/Shift+Tab/Enter/Space/F5/Escape controls.
- Permit hosting/joining from an active main menu even when its game manager is initialized; validate the level seed only for hosts.
- Keep protocol version 4. All room participants still need the same mod version, now 0.2.1.

## 0.2.0, 2026-10-18

- Shared free inventory pickups with host ownership, delivery acknowledgement, duplicate protection and empty-slot checks.
- G-key location pings: eight-second markers, one per player, host rate limiting.
- Measured round-trip latency in the lobby and client HUD.
- Host waits for accepted players to finish matching the level before play begins.
- Final rejection messages explain incorrect room codes, version mismatches and late joins.
- Item use is blocked while the multiplayer panel is open.
- Ready retries use elapsed time rather than frame-number coincidence.
- Reconcile randomized free item placement from the host before clients report ready.
- Protocol version 4. Everyone in a room must upgrade to 0.2.0.
- Twenty-two transport/claim checks and fifteen two-process game checks pass.

## 0.1.0, 2026-10-18

Initial experimental LAN build: BepInEx and MelonLoader entry points, host/join overlay, seed matching, remote avatars, notebooks, activity results, standard doors, NPC positions and host-owned scene transitions. Fourteen transport checks and eight live checks passed.
