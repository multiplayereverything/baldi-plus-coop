# Baldi's Basics Plus Multiplayer

**Baldi Together 0.9.0:** experimental co-op for Baldi's Basics Plus **0.14.5**, over your LAN or online through Steam. Windows x64 / Unity 2020.3.49f1. BepInEx and MelonLoader builds share the same network implementation and can play together. Use **one loader per game copy**. Everyone must update to 0.9.0 (protocol 14). Older ZIPs are retained for rollback.

## New in 0.9.0

- **Play online over Steam.** MULTIPLAYER now has **STEAM GAME**:
  - **HOST ON STEAM** opens a room, friends only or public.
  - **Friends join** from **FIND STEAM GAMES**, from your **INVITE FRIENDS** invite, or with **Join Game** on your Steam profile.
  - **No setup:** no IP addresses, port forwarding, VPN or extra server. It uses the Steam support already inside Baldi's Basics Plus, through Valve's relay network.
  - **What everyone needs:** the game started from Steam, signed in, on the same mod version.
- **Fix: a host that froze for 15 seconds lost its players.** The connection only counted as alive while the game loop sent something. A host stuck for 15 s (a long load, a hitch, possibly Alt+Tab) looked gone, and its clients migrated. Your log showed exactly this. The same happened to a frozen client. Links now keep themselves alive on the network thread. If the host goes quiet, the client says "The host isn't responding" and waits up to a minute.
- **Fix: a bad connection could drop a player.** A connection was dropped when 512 packets waited for the game, or 128 waited to be sent. These limits are now by size, so short hitches and laggy internet links no longer kick anyone.
- **Tests:**
  - a bad-network test through a TCP chaos proxy: 25 s host freeze, 10 s outage and cut link, plus a mutant run without the fix that must fail;
  - a Steam tour: socket transport, lobby, invite presence and menu pages.
  - See [Steam online play and link health](docs/ONLINE-0.9.0.md).

## New in 0.8.3

- **The Hall Monitor is now a random NPC.** He joins about half of multiplayer floors, like the game's own random NPCs. The roll comes from the run seed and the floor, so every player gets the same answer.
- **His poster matches the game's character posters:** a framed portrait, a white description card and a name plate, with text in the game's own font. It hangs in the character-poster rooms when he is on the floor.
- **Tests:** the Hall Monitor tour has 25 checks: his poster built on a real wall, his poster among the others in the floor's character-poster room (with a screenshot), and the spawn roll.

## New in 0.8.2

- **Fix: some runs threw the host back to the menu.** On some seeds two soda machines sit at exactly the same spot (seed 2101520539 has one at 95,0,65). The host's level check rejected the whole run and disconnected. Machines are now numbered in a stable order, so host and clients still match.
- **Fix: stuck elevator after a failed load.** When a run ended while its elevator screen was still up, that screen stayed behind and threw an error every frame. It is now removed when the mod returns to the menu.
- **Alt+Tab:** the lab now has focus tests (`scripts/focus-test.ps1` and `scripts/focus-same-folder-test.ps1`). In them the host survived losing focus, a 25 s minimize, and a second instance from the same folder. Real Alt+Tab key presses can't be scripted against a background window, so that case isn't fully covered.
- **Tests:** a new `compat-fail-tour` starts through the real Hide & Seek button and forces a failed level check. A two-process co-op check also passes on the failing seed.

## New in 0.8.1

- **Only the host starts a run.** A connected player who isn't the host can no longer start a game from the title menu. Pressing Hide & Seek New or Continue, a challenge, Field Trip, Endless or the tutorial now shows "Only the host can start a run" instead. Previously the elevator opened over the title menu and got stuck there. The player joins the host's run automatically when it starts.
- **Tests:** the two-process LAN test now has the joined client press the real New and Continue buttons. Other menu buttons are unaffected.

## New in 0.8.0

- **New NPC: the Hall Monitor.** He appears only in multiplayer, patrolling the halls with a clipboard.
  - **Running:** run where he can see you and he writes you up. The mark lasts 25 s, with a countdown gauge on your HUD.
  - **Whistle:** if two marked players get within 1.5 tiles of each other, he blows his whistle, and for 10 s Baldi hunts that pair instead of whoever is closest.
  - **How he works:** he is built from the game's own Principal parts and spawns through the floor's own NPC list.
  - **Sync:** he is host-authoritative, and clients see his sprite and hear his voice and whistle.
- **Details:** see [the Hall Monitor](docs/HALL-MONITOR-0.8.0.md).
- **Tests:** a 22-check lab tour, plus two checks in the two-process NPC test. Protocol 13: everyone must update.

## New in 0.7.1

- **Performance:** clients spend about a tenth as much time per frame on multiplayer work (about 0.14 ms instead of 1.4–3.3 ms).
  - Clients no longer search the whole scene for the elevator screen every frame, or for Happy Baldi during the countdown.
  - Title-screen checks are cached.
- **Long-session soak:** two players on floor 1, the Pitstop and floor 2 with every NPC active stayed in sync with no errors. The mod's cost stayed under 0.3 ms per frame on both sides, and memory stayed flat within each floor.
- **Details:** see [performance and playability](docs/PERFORMANCE-0.7.1.md). Run `scripts/launch-lab.ps1 -Soak` with `tests/verify-perf.py` to repeat it; the lab suite includes it.

## New in 0.7.0

- **Every named NPC now works with remote players.**
  - **Beans** spits gum at teammates.
  - **Arts and Crafters** reacts to teammates' gaze and teleports them.
  - **Mrs. Pomp** gives teammates their own classroom appointments.
  - **The Test** is driven by everyone's gaze.
  - **Dr. Reflex** tests and hammers teammates and pays the YTP reward.
  - **Chalkles** uses the same boards on every machine and charges for teammates in the room.
- **Teammates' items now affect the real NPCs.** BSODA, nana peels, chalk cloud, WD-40, whistle, alarm clock, scissors on 1st Prize, apples for Baldi, and the swing-door lock, detention key, coin door and tape.
- **New spectate screen:** a "SPECTATING" card with the teammate's colour and portrait, a team roster (alive, caught or hiding), a dark "is hiding" screen for lockers, a waiting state and a short dissolve when switching.
- **Baldi:** he ignores teammates hidden by a chalk cloud.
- **Details:** limits and verification are in [NPC coverage 0.7.0](docs/NPC-COVERAGE-0.7.0.md); `scripts/lab-suite.ps1` tests everything.

## New in 0.6.3

- **Ping volume:** the ping now follows the game's sound-effect volume.
- **Coded rooms:** selecting one on FIND LAN GAMES reliably opens the code field.
- **Tests:** a new automated lab suite covers sounds, colours, the spectator HUD and LAN discovery, including a real two-game LAN join. See [the test guide](docs/TESTS-0.6.3.md).

## New in 0.6.2

- **Spectating:** switching teammate plays a quick paper whoosh.
- **FIND LAN GAMES:** a computer beep plays when a new room appears.

## New in 0.6.1

- **Team sounds:**
  - **G-ping:** rings a desk bell at the marker, heard in 3D.
  - **Join and leave:** players joining or leaving the room play rising or falling xylophone notes.
  - **Caught:** a teammate being caught plays a ruler slap.
- **Volume:** the sounds follow the game's sound-effect volume and are levelled to match the game's own sounds.
- **Origin:** generated with Stable Audio 3 small-sfx; see `ARTWORK.md` for prompts and credits.

## New in 0.6.0

- **FIND LAN GAMES:** open rooms on your network appear automatically. Select one to join; rooms with a code open the Join form on the code field.
- **Player colours:** choose from eight colours in PLAYER OPTIONS, with a preview. Teammates see your sprite recoloured, while skin, hair and eyes keep the original art.
- **Spectator HUD:** while you spectate, your HUD shows the watched teammate's inventory, selected item and stamina.
- See [lobby features and limits](docs/LOBBY-0.6.0.md). LAN discovery uses UDP port 27016 on the local network only.

## New in 0.5.4

- **Bug-fix pass.** Fixes include:
  - the Principal freezing after a remote chase;
  - Playtime staying at run speed;
  - an invisible Bully;
  - Baldi ignoring noises while everyone hides;
  - clients being unable to restock Johnny;
  - ghost players after network stalls;
  - kicks and solo forks after host migration;
  - lost pickups and shop refunds;
  - F2 console and menu input leaking into the game.
- See the [full fix list and limits](docs/FIXES-0.5.4.md).
- These fixes are checked by build, transport tests and code review against the original game. They have not yet been confirmed in a real session.

## New in 0.5.3

- Remote Baldi captures use his original Looker raycast, including its native layer mask and ignored transforms. The previous all-layer check could incorrectly block capture on a collider the original game ignores, including while the host spectates.
- F2 `contacts` and saved reports show local entity interaction locks/layer, invincibility, locker state, Baldi state, peer eligibility, distance, native visibility and all-layer blockers. See [capture investigation and limits](docs/CONTACT-0.5.3.md).

## New in 0.5.2

- Host-authoritative soda-machine stock and weighted rewards; the buyer consumes the quarter and receives the item once. Empty machines update for everyone. [Details and delivery limits](docs/VENDING-0.5.2.md).
- Native elevator results show the host's earned YTPs, power multiplier, sticker bonus, total and time, including while the next floor loads. Rewards are not granted twice. [Results](docs/RESULTS-0.5.2.md).
- Connection artwork uses the same visibility and opacity as its player name.
- Countdown release starts the native NPC/event queues once per environment even when an NPC already exists. Spawn clearance checks exposed living teammates and excludes locker occupants and spectators. [Countdown](docs/COUNTDOWN-0.5.2.md).
- **F2** opens a debug console with `help`, `status`, `peers`, `npcs`, `clear` and `save`. It shows countdown/audio, pending spawns, loading barriers and peer state; `save` writes a report under `UserData/BaldiTogether/logs`. [Console](docs/DEBUG-CONSOLE-0.5.2.md).

## New in 0.5.1

- Intercept native Baldi capture before its final attack state, so individual elimination leaves him chasing survivors.
- Also intercept the manager's game-over entry before it stops round coroutines. Single-player and the multiplayer team-wipe ending retain their native behavior.

See [Capture regression](docs/CAPTURE-0.5.1.md).

## New in 0.5.0

- Individual capture: surviving teammates keep playing. Caught players spectate living teammates only, using Left/Right or Tab to switch; no free camera or mid-round revival. A hidden teammate's view is concealed. A team wipe uses the native ending; the next floor/attempt resets elimination.
- A living remote teammate can activate the native elevator while the host spectates, with host-checked position, failure quota and exit conditions. Elevator state follows the host.
- Host migration: a selected teammate takes over after host departure. During a run, the current floor reloads with the same seed and caught players remain eliminated. Floor progress restarts; this is not a seamless world-state transfer. LAN endpoints must be reachable; failed reconnects time out. Arbitrary network partitions have no external fencing service.
- Compatibility page lists accepted players' exact mod version and game-build match. Mismatched versions/builds are rejected before joining. Other installed mods are not automatically compared.
- Your supplied connection artwork appears beside player names. Lobby quality uses measured round-trip time; old samples turn gray after eight seconds. Clients measure their host link; other clients are marked Via host rather than assigned a fabricated peer-to-peer ping.
- Bully collision/obstacle state, remote sight/guilt and spawn safety; Cloudy Copter wind regions/forces; sprite child activation such as Chalkles' form changes. Eliminated players are excluded from remote NPC interactions.

### NPC synchronization checklist

All cataloged NPCs share host spawning/despawning, position/facing, interpolated motion, semantic visibility, animator state and sprite state. That foundation does not implement every NPC ability. The table covers the installed game's named NPCs; the [full coverage document](docs/NPC-COVERAGE-0.5.0.md) lists code evidence, additional actor classes and outstanding work.

| NPC | Implemented beyond shared presentation (0.7.0) | Remaining limits |
| --- | --- | --- |
| Baldi | Nearest living teammate pursuit, host catches, shared countdown, alarm-clock lure and apples from teammates, ignores cloud-hidden teammates | Challenge variants not validated |
| Playtime | Remote pursuit, owner-only rope, result/reward/cooldown, scissors via rope reports | Natural multi-player encounters |
| It's a Bully | Theft ownership/deduplication, physical blocking, remote sight/guilt, spawn buffer | Lost theft-ack recovery and broader encounter testing |
| Principal of the Thing | Remote guilt/sight/pursuit, owner detention, locks/timers | More item-specific rules and simultaneous detention testing |
| 1st Prize | Remote charge targets, hug force, teammates' scissors stun | High-speed collision timing over latency |
| Gotta Sweep | Shared sweeping force and cleanup | Natural multi-player collision testing |
| Cloudy Copter | Wind region, direction/speed, native force/cleanup | Wind audio bounds and squish visuals |
| Chalkles | Host boards on every machine, remote room charge, mirrored charge, door-locking attack | Partial math-machine progress by teammates |
| Arts and Crafters | Remote triggers, validated gaze/jealousy, owner attack, paired Baldi/owner teleport | Attack loop audio for other players |
| Beans | Remote targeting, host-resolved gum hits, native gum on the owner, visible gum flight | Hit radius is pose-based |
| Mrs. Pomp | Per-player appointments with native UI, validated arrival reward, late chase and drag | Distance-based catch |
| The Test | Combined validated gaze, freeze/pressure/flee/explode, blind gauge, mirrored time scale | Blind gauge on every player |
| Dr. Reflex | Remote sight, native test with validated owner clicks, host-paid reward, office trip and hammer squish | Reflex audio is local |
| Hall Monitor (mod NPC, 0.8.0; random from 0.8.3) | Host marks running players (local guilt and remote rule reports), whistle focuses Baldi on a marked pair, replica sprite/voice/whistle cues, native HUD gauge | One per floor; real-session tuning |

Student, StudentCrowd, Balder and development TestNPC actors need their scenario-specific behavior checked separately. Johnny uses shop synchronization; field-trip/event actors and projectiles need dedicated records. See [0.5.0 behavior and verification](docs/MULTIPLAYER-0.5.0.md).

## New in 0.4.13

- Multiplayer uses the supplied `Game Sound Preview/NewAudio/BAL_HideSeek.wav` for Baldi's Hide & Seek intro. Both loader DLLs embed the clip.
- Single-player retains the original audio. Other clips, countdown and native playback routing remain unchanged.

See [Multiplayer intro audio](docs/HIDESEEK-AUDIO-0.4.13.md).

## New in 0.4.12

- Entering a hideable locker hides your remote player sprite and name tag on other machines; exiting restores the sprite at your current position.
- Locker visibility travels with each player pose, including host relay to other clients. Level resets clear the hiding flag. All players must update for protocol 8.

See [Locker visibility](docs/LOCKER-VISIBILITY-0.4.12.md).

## New in 0.4.11

- Native dissolve throughout Multiplayer navigation: Host Game, Join Game, Player Options, Lobby Options, internal Back, Host/Join confirmation and Leave/Cancel.
- Text fields still edit immediately. Options remain Coming Soon labels; connection and validation behavior are preserved.

See [Multiplayer page transitions](docs/PAGE-TRANSITIONS-0.4.11.md).

## New in 0.4.10

- Back from the Multiplayer root/room page to the title uses the native dither dissolve, including the purple title artwork. Esc at that level uses the same path.
- Returning from a connected room keeps the connection open. Back between internal option/form pages and Back to gameplay retain their existing behavior.

See [Back transition verification](docs/BACK-TRANSITION-0.4.10.md).

## New in 0.4.9

- Prepare the Multiplayer artwork during native menu initialization, including while its menu is inactive, so it participates in the title's dissolve.
- Let the native menu parent control visibility when returning from other screens, instead of hiding the button until the next scan.
- Title visibility also checks the actual canvas, avoiding the outgoing profile page being mistaken for the title.
- Refresh the incoming title's canvas and camera immediately before the native dither reads its destination, so the artwork is present in the transition texture. See [transition evidence](docs/TITLE-TRANSITION-0.4.9.md).

## New in 0.4.8

- The title-screen Multiplayer button uses the supplied purple 3D lettering above the Basically Games copyright.
- The original PNG is embedded unchanged; a sprite rectangle excludes its transparent padding. The native button action and dither opening transition are preserved.

See [button artwork preview and verification](docs/BUTTON-ART-0.4.8.md).

## New in 0.4.7

- **Player Options** appears on the Multiplayer main page, outside the lobby: name tag visibility, name tag size, player color, player icon and connection display.
- **Lobby Options** appears inside the room: shared YTP, individual player deaths, spectate after capture, shared lives, player revival, friendly item effects, shared map discovery and mid-run joining.
- These pages contain feature names under **COMING SOON**. They have no toggles or saved values and do not change gameplay. Individual deaths and spectating are planned features, not implemented by this update.

See [options UI scope and previews](docs/OPTIONS-0.4.7.md).

## New in 0.4.6

- Clients show the native elevator introduction, doors and zoom-out when joining the host's run, restarting and moving between floors/pitstops.
- The host's elevator start releases clients automatically after their own level validation. Clients play the native exit sequence before entering gameplay, with no extra ready button.
- The host's native elevator animations also finish normally instead of being removed by the mod. See [elevator transition scope and verification](docs/ELEVATORS-0.4.6.md).
- Eight focused checks pass across a BepInEx host and MelonLoader client, including initial entry, restart/pitstop entry and return to the floor with delayed client validation.

## New in 0.4.5

- Remote player sprites join the native NPC billboard rendering layer. This fixes the camera-order problem that still allowed a farther NPC to draw over a nearer player after 0.4.3's material change.
- Verification now covers the separate world and billboard camera passes, using the game's camera masks and an actual remote-player component. See [camera-layer verification](docs/SPRITE-STACK-0.4.5.md).

## New in 0.4.4

- Opening Multiplayer from the title screen uses the game's native dither dissolve, with the same 16-step pattern and timing as its other menus.
- Menu input waits for the dissolve to finish; the pointer returns afterward.
- Four focused runtime checks pass; both loaders compile. See [transition evidence](docs/TRANSITION-0.4.4.md).

## New in 0.4.3

- Remote players use the game's native NPC billboard material. The supplied character artwork and animation frames are preserved. Version 0.4.5 also corrects the rendering layer; the material change alone did not fix gameplay overlap.
- Four focused GPU checks reproduce the old overlap defect and verify both depth orders and transparent areas with the fix. Both loaders compile.

See [sprite depth verification](docs/SPRITE-DEPTH-0.4.3.md). Restart the game to load the updated plugin.

## New in 0.4.2

- Restore the game's 480x360 (4:3) menu frame. Widescreen displays show black side borders; narrower displays get top/bottom borders when needed.
- Keep menu text, pointer position and clickable areas aligned with that frame when resizing.
- Eight focused rendering/input checks pass across widescreen, 4:3, ultrawide and portrait layouts; both loaders compile.

See the [framed menu preview](docs/multiplayer-menu-0.4.2.png) and [aspect-ratio verification](docs/ASPECT-0.4.2.md). Restart an already-running game to load this update.

## New in 0.4.1

- Multiplayer opens a plain white game menu with **HOST GAME**, **JOIN GAME** and **BACK**.
- The game font, cursor artwork, underline highlights, menu button components and sounds replace the old cream lobby card.
- Host/Join options use simple inline fields; the room shows player names and Back/Leave choices. The top gameplay banner is removed.
- Click a field to edit it. Tab changes fields; Enter finishes editing. Up/Down or Tab selects menu choices, Enter confirms, and Esc goes back. F5 closes the menu.

See the [menu preview](docs/multiplayer-menu-0.4.1.png) and [UI verification](docs/UI-0.4.1.md).

## New in 0.4.0

- NPCs spawn and despawn from the host's ordered prefab catalog. Clients no longer run a separate NPC spawner or sight detector that can diverge from host AI.
- NPC visibility, interaction state, animator states/parameters and sprite changes follow the host. This covers poses and hiding for characters such as Bully, Arts and Crafters, Playtime, Beans, Dr. Reflex and the Principal; their individual abilities still need separate support.
- Playtime pursues visible remote players. Contact starts the native rope game only for its owner; results return to the host for cooldown and a single team YTP award. Disconnects, cancellation and timeouts release the reservation.
- It's a Bully can take an eligible item from a remote player's personal inventory. Commands are deduplicated; host hiding follows the recipient's acknowledgement.
- Gotta Sweep and 1st Prize apply host movement forces to nearby remote players. Modifiers expire on stale snapshots, separation, scene changes and disconnect. 1st Prize's native charging routine can target visible remote players.
- The Principal observes remote rule-breaking with host line-of-sight and sensitivity checks, chases offenders and sends only the culprit to detention. Room locks, clocks and the culprit's gauge synchronize; timers receive host corrections. Native host-player detention clocks are also shared.

See [NPC synchronization scope and verification](docs/NPC-SYNC-0.4.md). This extends the existing NPCs; it does not increase the normal game's NPC count.

## New in 0.3.1

- Each new multiplayer run gets a random host seed. Floors and restarts retain that run's shared seed.
- Starting the host's level loads everyone automatically. The host begins once its own level is ready; each client enters after its own layout/item validation, without a separate ready action. Version 0.4.6 restores native elevator presentation. Slower clients can miss the opening seconds.
- Client NPC positions and facing interpolate every rendered frame between host snapshots. Large teleports snap instead of sliding across the map. Host Baldi movement and catch decisions remain authoritative.
- Host window breaks and swing-door open/lock states replicate, including map/collision changes. Nearby remote players open unlocked swing doors on the host. Client item effects that break windows or lock doors are still outside this support.

See [focused loading and world synchronization checks](docs/SYNC-0.3.1.md).

## New in 0.3.0

- The host controls Happy Baldi's hiding countdown: both games play the same number, counting animation and release phase. Clients do not start independent countdowns.
- Baldi chases the nearest connected player, updating the target as players move. Stale positions and disconnected players are excluded; apple/praise/attack states keep their normal behavior. Remote catches require a clear line through the world.
- Player names are small text with a subtle shadow, shown only when looking toward a nearby teammate. They fade with distance and no longer use large boxes or show through walls.
- Johnny's in-world shop synchronizes stock, prices, sale/SOLD labels and a shared team YTP wallet. Purchases reserve stock and charge once; an inventory-fill race refunds the price and returns stock. Changed item/price quotes and duplicate purchases are rejected.
- Shelf items and sticker/glue rewards go to the buyer, purchased maps are shared, and restocking runs on the host. Native host awards fund the shared wallet; client copies of those awards are ignored.

See [synchronization verification](docs/SYNC-0.3.md) and [shop behavior and limits](docs/SHOP-SYNC.md). Seven targeted live checks passed; the old full gameplay/sprite suite was not rerun for this change.

## New in 0.2.2

- Remote players use your supplied green character sheet instead of capsules.
- Sixteen transparent frames provide front, back, left and right views. The sprite faces the camera, with the correct view chosen from the player's facing direction.
- Movement advances the walking cycle; standing still returns to the idle pose. Teleports and scene changes reset motion.
- The original PNG is embedded in both loader DLLs, so no separate image installation is needed. Direction captions along the sheet's left edge are excluded from the sprite rectangles.

See the [in-game player preview](docs/player-sprite-0.2.2.png) and [sprite verification notes](docs/SPRITES-0.2.2.md).

## New in 0.2.1

- **MULTIPLAYER** appears above the Basically Games copyright on the main menu, using the game's own font and cursor. The offline top banner is removed.
- A centered lobby replaces the corner panel. Separate **Host a room** and **Join a room** tabs show the fields needed for each action, with no scrolling.
- The player roster, loading state, connection status, leave action and keyboard focus are clearer. The modal scales to the window and blocks the game's menu cursor while open.
- Hosting works when the main menu has already initialized its game manager. Joining no longer requires a valid unused level seed.

Screenshots: [main menu](docs/menu-0.2.1.png), [host lobby](docs/lobby-0.2.1.png), [800x600 layout](docs/lobby-small-0.2.1.png). See [UI verification](docs/UI-0.2.1.md) for the scope of the checks.

## New in 0.2.0

- Free inventory pickups now belong to the team world: the host grants each one once, and taking it removes it for everyone. Make an empty inventory slot first. Full-inventory requests do not overwrite a carried item.
- Press **G** while looking at a location to mark it for teammates for eight seconds. Each player has one marker; new pings replace their previous marker.
- The original all-player loading barrier was replaced by automatic host-led entry in 0.3.1.
- Randomized free item layouts are reconciled from the host before readiness is accepted, so identical seeds do not have to reproduce every item placement.
- Connection round-trip time appears in the lobby and the client's game HUD.
- Wrong room codes, mismatched versions and late joins report their reason before disconnecting.
- Opening the lobby also blocks item use, so panel input cannot accidentally activate an item.

See `CHANGELOG.md` and `docs/IDEAS.md` for the improvement record and next candidates.

## Play

Install a loader and the mod first (see [Install](#install-either-build-elsewhere)). Launch the game normally and select **MULTIPLAYER** above the Basically Games copyright, or press **F5**. Each friend needs their own identical game version and a copy of this mod.

1. Host: choose **HOST GAME**, enter a player name, TCP port (default 27015) and optional room code, then select **HOST**.
2. Friends: choose **JOIN GAME**, enter the host computer's LAN address, the same port and room code, then select **JOIN**. `127.0.0.1` only connects to your own computer.
3. Wait for everyone to appear in the roster. Close the panel, then start a **new Hide & Seek run** using the normal game menu. A random seed is shared automatically. Clients load and enter the host's level without pressing ready; local generation and compatibility checks still take time.
4. **Online over Steam:** start the game from Steam. The host picks **STEAM GAME**, then **HOST ON STEAM**. **ROOM: FRIENDS ONLY / PUBLIC** sets who can find the room. **INVITE FRIENDS** opens Steam's invite list. Friends accept the invite, choose **Join Game** on the host's Steam profile, or pick the room on **FIND STEAM GAMES**. Nobody types an address.
5. **G** pings the location you are aiming at while alive. **F5** opens the lobby during play. **Leave game** ends your session; when a host leaves, remaining peers attempt migration. Join before the run begins; ordinary late joining is unavailable. Caught players use Left/Right or Tab to spectate surviving teammates until the next round.

LAN games use direct TCP on a trusted network, and the optional room code is sent in plaintext. Steam games go through Steam's own relay, and a Steam room has no host migration: when the host leaves, the room ends. Windows may require you to allow the game on your private network. Multiplayer runs do not create resumable run saves.

## Features and limits

- Up to four players: host plus three clients. Three local game processes have been exercised across both loaders for migration.
- Animated directional player sprites with player names and interpolated positions, using the supplied character sheet.
- Shared seed and a generated-layout hash check before gameplay messages are accepted.
- Host-validated notebook pickups, activity completion results and standard door clicks; shared door lock/open states.
- Host-controlled, single-owner free inventory pickups, with duplicate-request protection and delivery acknowledgement. Existing inventory slots remain personal.
- Team pings, measured connection latency and automatic entry after local compatibility checks.
- Host-driven NPC spawning, animation/visibility state and per-frame movement interpolation, with individual Baldi catches. Baldi selects the nearest eligible living player.
- Remote-player Playtime rounds, Bully theft, Sweep/1st Prize forces, and Principal chase/detention. See the NPC scope document for limits.
- Host window break state and swing-door open/lock state, with remote-player proximity opening.
- Host-controlled Johnny shop stock, prices, shared YTP, purchase delivery/refunds and restocking.
- Host-driven scene changes with next-level, lives, attempts and mode state.
- Teammate-only spectating until the next round, native team wipe and remote elevator activation while the host is spectating.
- Controlled LAN host migration, exact-version compatibility display and measured connection indicators.

Teammates' item effects on NPCs and the world are relayed to the host for the items listed in docs/NPC-COVERAGE-0.7.0.md. Inventory transfers, repeatable/non-inventory rewards outside Johnny's shop, item swaps, dynamic drops, puzzle presentation, field trips and random events are not comprehensively synchronized. The old alternate StoreScreen interface and forwarding client-only YTP effects remain unsupported. Shop visitor dialogue remains local. Activities are solved locally and their completion result is shared. Free pickups and purchases require an empty inventory slot. If a recipient disconnects during an unacknowledged delivery, stock stays consumed and a purchase remains spent to prevent duplication. All players share the supplied character artwork; names identify teammates. This is an early co-op build, not complete replication of every game system. Challenge/endless/custom content and combinations with other mods have not been validated.

## Install either build elsewhere

Install the matching official loader first: [BepInEx 5.4.23.5 Windows x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) or [MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3). Download the matching mod ZIP from the [Releases page](https://github.com/multiplayereverything/baldi-plus-coop/releases) (or `artifacts/release/` when you build it yourself with `scripts/build.ps1` and `scripts/package.ps1`) and extract it into the game root. Loader binaries and game files are not included in the mod ZIPs.

- BepInEx plugin: `BepInEx/plugins/BaldiTogether/BaldiTogether.BepInEx.dll`.
- MelonLoader mod: `Mods/BaldiTogether.MelonLoader.dll`.

To remove the mod, close the game and remove its DLL. Loader removal is separate: follow that loader's instructions and preserve any other installed mods. The local installer backs up overwritten loader files and existing saves under `private/backups/` before installation. Lobby name/address/port settings are in `UserData/BaldiTogether.ini`; room codes are not saved. Any legacy saved Seed value is ignored for normal multiplayer runs.

## Troubleshooting

For Steam rooms, everyone must start the game from Steam and be signed in; a room on another mod version shows as "other version" on FIND STEAM GAMES. If joining fails, confirm that the host has opened a room, everyone has the same game and mod versions, the room code matches, and the port is reachable on the LAN. Return to the menu before reconnecting. A layout mismatch closes the incompatible connection; use identical game settings and mod sets. Read `BepInEx/LogOutput.log` or `MelonLoader/Latest.log`. F5 closes the lobby if it is covering the game.

## Build and verify

From this folder in PowerShell:

```powershell
./scripts/build.ps1
./scripts/test.ps1
./scripts/watch.ps1 verify run tests/watch-contract-v24.json --dir .
./scripts/lab-suite.ps1
```

`lab-suite.ps1` needs the game closed. It runs the lobby, spectator and Hide & Seek audio tours and the two-process LAN join test, stopping only its own processes; see [docs/TESTS-0.6.3.md](docs/TESTS-0.6.3.md).

The build uses the existing Windows .NET Framework compiler, installed game assemblies, and local loader references in `deps/`. No .NET SDK is required. `scripts/install.ps1 -Loader BepInEx` installs locally; use a separate game copy for MelonLoader. `scripts/package.ps1` creates the two mod-only ZIPs.

Development-only `-bt-lab` redirects game file saves to the workspace and suppresses registry preference writes in selected game classes. `-bt-diagnostics` captures game screenshots and state files. These flags are for repeatable local tests, not normal play. `scripts/launch-lab.ps1 -TestActions` launches the existing two-process test lab. Its actions invoke game methods, so they do not establish a native mouse/keyboard click-through.

Use `scripts/launch-lab.ps1 -TestActions -SpriteTour` and `python tests/verify-live.py --sprites` for the sprite regression run. It adds four checks for both players' loaded sprites, four view directions, walking frames and idle poses. Gameplay evidence captures render the live player camera to a temporary texture after the frame, then restore its render target. These world captures omit the IMGUI lobby/HUD and are distinct from the earlier UI screenshots.

With `-bt-lab -bt-diagnostics`, the optional `-bt-ui-tour` records menu, Host, Join, invalid-port, open-room and 800x600 states using the same UI handlers. Its test room uses port 27019. It does not inject physical input.

## Progress

- [x] Individual captures, teammate-only spectator views, no mid-round revives and native team wipe.
- [x] Version compatibility page and supplied connection-quality artwork with measured/stale samples.
- [x] LAN successor selection/reconnect and controlled same-floor migration reload with retained elimination.
- [x] Expand Bully physical/contact/spawn behavior, Cloudy wind and NPC child-sprite state; list all named NPC ability gaps.

- [x] Replace the lobby card with a Baldi-style Multiplayer menu, native text buttons and inline fields in 0.4.1.

- [x] Host-authoritative NPC catalog, animation and visibility replication.
- [x] Remote-player Playtime rounds, Bully theft, Sweep/1st Prize forces and Principal detention.
- [x] Pass 13 focused NPC checks in two game processes and 22 required transport/claim checks for 0.4.0.

- [x] Random seed for each new run; preserve it through floor changes and restarts.
- [x] Remove the group loading barrier and automatically enter clients after local validation.
- [x] Interpolate NPC motion every frame and synchronize host window/swing-door state.
- [x] Pass eight focused entry/world/reload checks and the 22 required transport/claim checks in 0.3.1.

- [x] Synchronize the host countdown, release phase and counting animation.
- [x] Retarget Baldi to the nearest player and reduce name label distraction.
- [x] Synchronize Johnny's shop stock, wallet and purchases with quote checks and refunds.
- [x] Pass seven focused live checks; compile both loaders and retain the required transport checks after protocol changes.

- [x] Embed the supplied original PNG in both 0.2.2 loader builds.
- [x] Replace capsules with sixteen-frame, directional animated player sprites.
- [x] Inspect an in-game world capture for transparency, placement and artwork.
- [x] Pass 19 live checks, including loaded sprites, four directions, walking and idle, plus the 22 transport/claim checks.

- [x] Add the native Multiplayer menu entry above the Basically Games copyright.
- [x] Center and simplify the lobby with separate Host/Join tabs and a readable roster.
- [x] Build both 0.2.1 loaders; repeat 22 transport checks and 15 live game checks.

- [x] Identified the installed game: Unity 2020.3.49f1, Mono, Windows x64.
- [x] Inspected Assembly-CSharp.dll using REA; selected player, seed, notebook, door and NPC APIs exist.
- [x] Downloaded the requested Anti Slop, Universal Modder, REA and Watch Skill toolkit sources under `toolkits/`.
- [x] Obtained official BepInEx 5.4.23.5 and MelonLoader 0.7.3 dependencies locally.
- [x] Implement bounded LAN transport, handshake, lobby and player replication.
- [x] Implement same-seed generation and shared notebook, activity and door interactions.
- [x] Compile both 0.2.0 loader builds; 22 transport, protocol and item-claim checks pass.
- [x] Install BepInEx in the game root and verify both loaders in separate game copies.
- [x] Verify a BepInEx host and MelonLoader client loading matching levels and exchanging player/world updates.
- [x] Pass 15 live checks including shared items, duplicate/full-inventory refusal, team pings, latency, deliberately delayed loading, host item-catalog reconciliation and scene transitions.
- [ ] Complete native UI click-through and broad multi-floor gameplay testing.
- [ ] Implement comprehensive synchronization of the remaining game systems listed above.

Current behavior and verification scope are in [docs/MULTIPLAYER-0.5.0.md](docs/MULTIPLAYER-0.5.0.md), with [NPC coverage](docs/NPC-COVERAGE-0.5.0.md). Earlier sprite and UI verification are in [docs/SPRITES-0.2.2.md](docs/SPRITES-0.2.2.md) and [docs/UI-0.2.1.md](docs/UI-0.2.1.md). `MODLOG.md` tracks development.

## Workspace

`src/`: original mod code. `scripts/`: build/install tools. `tests/`: network verification. `docs/`: design and evidence. `deps/`, `private/` and `toolkits/` are local development material and must not be redistributed with the mod. Game assemblies and decompiled code are private inspection inputs only.

## Requested tools

- [Anti Slop](https://github.com/miqdadbadjuber/anti-slop): during-development mode selected by the user.
- [Universal Modder](https://github.com/rehan-remade/universal-modder): Unity modding workflow, field notes and verification.
- [REA](https://github.com/morluto/rea): connected managed artifact/member inspection.
- [Watch Skill](https://github.com/oxbshw/watch-skill): installed locally with standard extras, dependency checks and isolated verification contracts. See `docs/TOOLKITS.md`.
- [Reverse Skill](https://github.com/zhaoxuya520/reverse-skill): installed as the Codex `reverse-skill-router` skill, linked to the complete local pack under `toolkits/reverse-skill/`, including its .NET/C# workflow.

Built with OpenAI Codex assistance. Uses BepInEx, MelonLoader and Harmony. Player artwork was supplied by the user as a Gemini/Photoroom PNG; see `assets/README.md` in the source workspace or `ARTWORK.md` in the release. No game assets or fonts are bundled. Not affiliated with Basically Games. Original mod source is MIT licensed; artwork and third-party tools retain their respective rights.
