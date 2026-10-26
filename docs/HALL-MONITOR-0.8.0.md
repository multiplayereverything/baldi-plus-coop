# The Hall Monitor (0.8.0)

A new co-op NPC that only appears in multiplayer. He patrols the halls with his clipboard.

- **Running:** if he sees you running, he writes you up ("No running in the halls!"). The mark lasts 25 seconds and shows as a countdown gauge on your HUD with his face.
- **Whistle:** if two marked players come within 1.5 tiles of each other, he blows his whistle ("Hey! You two! I'm telling!"). For the next 10 seconds Baldi hunts that pair instead of whoever is closest. Both marks clear, and he can't whistle again for 10 seconds.
- **How to beat him:** walk past him, or split up after one of you gets written up.

**When he appears (0.8.3):** on about half of multiplayer floors that have Baldi.
- The roll uses the run seed and the floor (`HallMonitorSpawnPatch.Rolls`, `SpawnChance = 0.5`), so the host and every client always agree.
- He never appears in single player, and nothing about single player changes.

## How he is built (game code he uses)

| Part | Native code |
| --- | --- |
| Body | A copy of the game's own Principal prefab (Navigator, Looker, Entity, propagated AudioManager, billboard sprite). The `Principal` component is swapped for `HallMonitor : NPC`, and every reference to it (`Looker.npc`, `Navigator.npc`) is repointed. The copy lives under an inactive, persistent holder. |
| Spawning | When the floor's roll picks him, added to the floor's own `npcsToSpawn` list (a postfix on `LevelBuilder.AddNpcsFromPreviousLevels`) on floors that have Baldi. The generator picks his hall spawn tile, places his wall poster, and `EnvironmentController.SpawnNPCs` spawns him after the countdown like any NPC. Host and clients generate from the same seed with the same mod, so their NPC lists match. |
| Behaviour | Native `NpcState`s: `HallMonitor_Patrol` walks between hall tiles with `NavigationState_TargetPosition`, and doors open through `NpcState.DoorHit`. `HallMonitor_Busy` stands still with `NavigationState_DoNothing` and counts down in NPC time (`Time.deltaTime * TimeScale`). |
| Sight | His Looker's own range and field of view, with a clear world line. |
| Running | The host player's native `PlayerManager` guilt (`ruleBreak == "Running"`). Remote players are judged from the rule reports they already send for the Principal. |
| Voice and sounds | `SoundObject`s with subtitles, played through his native AudioManager, so they are 3D and carry subtitles. |
| Whistle | `EnvironmentController.MakeNoise` at the pair. In multiplayer Baldi hunts the nearest player and ignores noises while he has a target, so `NearestBaldiPeer` (`HidingSync.cs`) narrows his choice to the whistled pair for `FocusSeconds`. |
| HUD | The native `HudGauge` (the detention gauge), counting down your own mark. |
| Poster | A `PosterObject` in the native character-poster layout. The base image has a framed portrait, an empty card and a name plate, measured from the game's posters. It reuses the native poster's two text boxes (name plate and description card, Comic 18 and Comic 12) with his text, so `TextTextureGenerator` renders it like any other character. It hangs in the character-poster rooms when he is on the floor. |

## Multiplayer

- **Host-authoritative:** the host makes every decision in `HallMonitorSync.cs`.
- **Clients:** they hold a replica built from the same prefab, found by name in `ReplicaTemplate`. The replica's sprite (patrol, writing or whistle) arrives through the normal NPC presentation sync.
- **World snapshot section:** it carries mark and whistle sequence numbers, the whistled pair and the live marks. From it, clients play the write-up and whistle cues once each, show the notice and drive their own gauge.
- **Joining mid-floor:** the first snapshot primes the sequence numbers, so a late joiner never hears old cues.
- **Protocol:** 13 (the world snapshot gained a section). Everyone must update to 0.8.0.

## Assets

- **Sprites:** the patrol, writing and whistle sprites (256x512) and the poster are drawn by `scripts/draw-hall-monitor.py` (Pillow and NumPy, procedural shading), so no game artwork is used.
- **Sounds:** the whistle and scribble were generated with Stable Audio 3 small-sfx. The two voice lines were spoken by Windows speech synthesis with the pitch raised.
- **Credits:** see `assets/README.md`.

## Tests

- **`hall-monitor-tour` (22 checks, single process, in `scripts/lab-suite.ps1`):**
  - in the native spawn list on a hall tile, spawned by the native spawner, components wired, poster ready;
  - patrols on his own;
  - ignores eliminated, locker, walking and behind-the-wall players;
  - marks a running remote player after the sight time, and marks the host's own running player;
  - a repeat sighting refreshes quietly;
  - native HUD gauge;
  - whistles only on a marked pair, points Baldi at the pair, cooldown;
  - marks expire and clear on disconnect;
  - world section cues play once and malformed sections are rejected;
  - the replica template resolves;
  - reset clears everything, and single player is unchanged.
  - It also saves `artifacts/tests/hall-monitor-patrol.png` and `hall-monitor-whistle.png` from the player's camera.
- **Two-process NPC sync (`launch-lab -NpcSync`, `tests/verify-npcs.py`, BepInEx host and MelonLoader client):**
  - `hall_monitor_replica`: both sides have his own body and sprite.
  - `hall_monitor_marks_client`: the client really runs in his sight; the host marks it; the client gets the cue, the local mark and the HUD gauge.

## Limits

- **One per floor:** only one Hall Monitor per floor is supported.
- **Running reports:** remote running uses the 5-per-second rule reports, so a very short sprint can be missed.
- **Lab only so far:** the whistle's Baldi focus and the sound levels were checked in the lab, not yet in a real session.
- **Lab switches:** `-bt-hall-monitor` forces him onto every floor and `-bt-no-hall-monitor` keeps him off. Both work only in lab mode.
