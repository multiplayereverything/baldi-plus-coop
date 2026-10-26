# Lab test suite, 0.6.3

Run everything with `./scripts/lab-suite.ps1` from `MultiplayerMod` with the game closed. The suite does the following:

1. Builds both loaders.
2. Runs the 22 transport checks.
3. Runs three single-process tours on the BepInEx copy.
4. Runs the two-process LAN join test.

It stops only the processes it started and exits non-zero on any failure. Each tour can also be run alone:

```powershell
./scripts/run-tour.ps1 -Tour lobby-tour -Result lobby-results.json
./scripts/launch-lab.ps1 -LanTour; .venv/Scripts/python.exe tests/verify-lobby.py lan
```

These are automated checks against the real game code with simulated teammates where needed. They do not replace listening to the sounds or a real multiplayer session.

## Lobby tour (`-bt-lobby-tour`, one process)

Runs synthetic peers through the real packet handlers and the native HUD, audio and menu components.

| Check | What it proves |
| --- | --- |
| `sounds-all-six-loaded` | All six embedded sounds decode |
| `sounds-mono-44k-short-effect` | Each is mono 44.1 kHz, at most 1.25 s, an Effect with no subtitle |
| `sounds-levelled-to-game` | Peaks at or below -5.5 dBFS, with either ~-18 dB RMS or the -6 dBFS peak cap |
| `sounds-play-through-native-sfx` | Room cues play through the game's own SFX AudioManager |
| `sounds-native-sfx-mixer-group` | Ping audio uses the game's SFX mixer group (and so the volume setting) |
| `colour-clothing-takes-target-hue` | Clothing pixels take each colour's hue |
| `colour-skin-hair-eyes-unchanged` | Skin, hair and white pixels are untouched for all eight colours |
| `colour-eight-sheets-sixteen-frames` | Every colour builds a full 16-frame sheet |
| `colour-picker-cycles-labels-and-previews` | COLOR cycles through all eight colours, updating the label and preview |
| `lan-rejects-malformed-beacons` | Garbage, bad room ids and bad ports are ignored |
| `lan-lists-room` | A valid beacon lists name, port, players and the code flag |
| `lan-prefers-lan-address-over-loopback` | The same room seen over loopback keeps its LAN address |
| `lan-found-sound-once-per-room` | The found cue plays once per room, not on every beacon |
| `lan-marks-other-versions` | Rooms on other versions are marked incompatible |
| `lan-page-shows-rooms` | The FIND LAN GAMES page builds a row per room |
| `lan-code-room-opens-join-form` | Selecting a coded room opens Join on the code field with the address filled in |
| `lan-real-beacon-loopback-roundtrip` | The host's real UDP beacon is received and parsed |
| `join-accepts-colour-from-identity` | A joining player's colour arrives in the handshake |
| `join-sound-on-host` | The join cue plays on the host |
| `leave-sound-on-host` | The leave cue plays on disconnect |
| `lan-beacon-stops-during-run` | The beacon stops once a run starts |
| `hud-packet-stored-from-client` | A client's HUD packet is parsed and stored |
| `avatar-renders-synced-colour` | Teammate avatars render their synced colour sheet |
| `ping-plays-3d-at-marker` | Ping plays as a 3D source on the marker through the SFX mixer |
| `caught-sound-on-capture` | The caught cue plays on a Baldi capture |
| `spectator-hud-shows-first-teammate` | The spectator HUD shows the teammate's slots, icons, empty slot and stamina |
| `switch-sound-on-teammate-change` | The switch cue plays when the watched teammate changes |
| `spectator-hud-follows-switch` | The HUD follows the newly watched teammate |
| `no-switch-sound-without-change` | No switch cue without a change |
| `spectator-hud-hides-stale-data` | The HUD hides when the teammate's data is over 3 s old |
| `caught-sound-once-per-remote-capture` | A repeated RoundState doesn't replay the caught cue |
| `own-hud-restored-after-spectating` | The player's own items and stamina return after the round |

## LAN join test (`-LanTour`, two processes)

The BepInEx host (colour red) opens a normal room. The MelonLoader client (colour blue) is not given an address: it opens FIND LAN GAMES, waits for the room, selects it and joins. `tests/verify-lobby.py lan` then checks both sides.

| Side | Checks |
| --- | --- |
| Client | `lan-host-listed-on-page`, `lan-found-sound-on-client`, `lan-page-row-built`, `lan-joined-without-typing-address`, `roster-carries-host-colour`, `hud-data-from-host`, `host-avatar-in-host-colour` |
| Host | client colour from LAN join, client avatar in client colour, HUD data from client, join sound played, beacon stopped once the run began |

## Spectator tour fixture fix

`baldi-keeps-moving` failed on the current build and on the untouched 0.5.3 baseline (built from the baseline commit), so it was not a regression. Two problems in the fixture caused it:

- **Unwalkable target.** The simulated survivor was placed 30 units north of the player. On current random seeds that point is not walkable, so Baldi's path failed and he wandered with no destination.
- **Wait too short.** Baldi moves in slaps on an anger-based timer (2.9 s after a capture here), so a fixed 2.5 s wait missed his first slap.

The fixture now uses the level spawn point and waits for the first slap, up to 8 s. It passes 16/16.
