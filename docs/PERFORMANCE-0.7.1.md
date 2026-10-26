# Performance and playability, 0.7.1

## How it was measured

- **Finding per-frame code with Graphify.** The mod's code graph (`graphify-out/`) was used to find everything that runs each frame: the runtime `Update` (42 systems), `OnGUI`, the remote sprite update, and Harmony patches on 15 of the game's own per-frame methods (for example every NPC's `Update`). Walking the graph's call edges from those entry points reached 704 methods. Their bodies were scanned for scene searches, component searches, reflection, LINQ and string building.
- **Lab profiler (`-bt-lab -bt-profile`, `src/PerfProbe.cs`).** It times each mod per-frame method and writes 10-second windows: frames per second, frame-time p50/p95/max, the mod's own milliseconds per frame, gen-0 garbage collections per minute, managed heap and Unity-allocated memory.
- **Soak (`scripts/launch-lab.ps1 -Soak`).** A BepInEx host and a MelonLoader client play real floors with every NPC active.
  - Every 3 s both players teleport to random tiles; every 15 s they team-ping; every 20 s they use a BSODA.
  - The host goes floor 1 → Pitstop → floor 2.
  - Baldi's catches are suppressed so the session can run for several minutes.
  - `tests/verify-perf.py` checks the result, and `scripts/lab-suite.ps1` includes it.

The lab windows are hidden and uncapped (hundreds of FPS), so per-second costs are higher than in normal capped play. Per-frame cost is the comparable number.

## What was slow and what changed

| Problem (found by the profiler) | Fix |
| --- | --- |
| The client searched the whole scene for the elevator screen every frame for the entire floor: about 440 ms per second, more than everything else combined. | Use the game's own `ElevatorScreen` singleton and cache the two reflection lookups. |
| The client searched for HappyBaldi every frame during each countdown: about 280 ms per second for roughly 30 s per floor. | Search once per floor. |
| `TitleVisible` did a scene search and a component lookup on every call, many times per frame on the title screen. | Cache it once per frame. |
| Lab hooks re-read the command line every frame (an allocation each time) in lab runs. | Cache the flags once (`Lab.HasArg`). |

## Results (lab soak, two players, all NPCs, three loads)

| | Before | After |
| --- | --- | --- |
| Client: mod cost per frame | 1.4–3.3 ms | ~0.14 ms (worst window 0.34 ms) |
| Client: frames per second (uncapped) | ~390 | ~950 |
| Host: mod cost per frame | ~0.19 ms | ~0.19 ms (worst window 0.24 ms) |
| Frame time p95 during play | under 4.4 ms | under 3.4 ms |
| Managed heap drift within a floor | flat | flat (about −0.5 MB) |
| Desync, rejects or exceptions over floor 1 → Pitstop → floor 2 | none | none |

At 60 FPS the mod uses about 1–2% of the frame budget on either side.

## Known

- **Occasional 50–90 ms frames:** these appear on both sides even on the title screen. They come from the game's own garbage collection (about 100 gen-0 collections a minute on the title screen, where the mod does almost nothing), not from mod work.
- **Load hitch:** a floor load costs a one-off hitch of up to about 0.3 s.
- **Memory between floors:** native memory steps up with the size of each floor (282 MB on floor 1, 317 MB on floor 2) and stays flat within a floor.
- **Not tested here:** the soak runs both games on one PC over loopback. Real Wi-Fi latency and four-player sessions are not covered.
