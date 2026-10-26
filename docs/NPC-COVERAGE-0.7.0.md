# NPC and item synchronization in 0.7.0

0.7.0 adds ability sync for the six named NPCs that 0.5.0 only presented (Beans, Arts and Crafters, Mrs. Pomp, The Test, Dr. Reflex, Chalkles). It also adds a host relay for client item effects and a redesigned spectate screen.

Each feature lives in its own file with its own two packet codes and its own section of the world snapshot (`src/NpcFeatures.cs` dispatches them). Everything follows the same rules:
- The host runs the real NPC.
- A remote player affected by it is sent a command, and their own game applies the native effect to their own player exactly once.
- Clients can only report things the host can check (gaze, arrival, clicks, item use). The host validates every report: epoch, ready, alive, not in a locker, a fresh pose, plausible distance and a one-time token.
- Every effect is released on disconnect, elimination and floor change.

Protocol is 12.

| NPC | Now synchronized | Known limits |
| --- | --- | --- |
| Beans | Targets and spits at visible remote players (host line of sight). The host resolves gum hits against remote poses. The owner gets the native gum (slowdown, overlay, gauge, scissors cut, expiry) once per hit. Clients see the gum fly. | 2.5-unit hit radius against a pose sampled every 50 ms. Beans' voice lines are local. |
| Arts and Crafters | Remote trigger boxes, client gaze reports validated by host sight, jealousy, flee and chase states. The attack is owned by the touched player, who gets the native spin and echoes. The paired teleport moves Baldi on the host and the owner on their own game. | Other players don't hear the remote attack loop. After a remote encounter ends early, he disappears for the floor. |
| Mrs. Pomp | Remote catches assign a native classroom appointment. The owner sees the real map icon, popup and countdown. The host validates arrival from the owner's position in the room and pays the native reward once. Late players are chased and dragged on their own game. | The catch is a 4.5-unit distance check. Her voice is local to the host and the owner. |
| The Test | Each client reports its gaze using the native looker rule, validated by the host. The host combines all watchers for freeze, pressure, flee and explode. Blindness (gauge, audio and the native speed-up) is shown on every player's HUD. Freeze time scale and lights are mirrored. | Everyone gets the blind gauge, matching the host's native `Blind()`. The facing check uses yaw only. |
| Dr. Reflex | Spots remote players using the native `Looker.Raycast`. The host runs the real reflex test, with clicks from the owner validated by token and sequence. YTP reward to the owner. On fail: the office trip and the hammer hunt, with the native squish on the owner. | Reflex audio isn't replicated. Other players don't see the remote squish. |
| Chalkles | The host's chalkboards are used on every machine. Remote players count toward room charge. The charge amount appears on every replica. The attack locks the room's doors on every machine, and owners acknowledge it once. | Partial math-machine progress by a remote player doesn't reduce charge. With teammates in two board rooms, he goes to whichever board someone entered most recently. |

## Client item effects

A client still uses its item natively. The use is also reported to the host, which checks it (token, rate limit, origin within 10 units, live-object cap) and runs the native effect against the real NPCs and world.

| Item | Host effect |
| --- | --- |
| BSODA, Diet BSODA, nana peel, chalk eraser cloud, WD-40 | Spawned natively on the host |
| Whistle | The host's Principals respond |
| Alarm clock | Lures Baldi to the clock |
| Scissors | Stun 1st Prize |
| Apple | Fed to Baldi |
| Swing-door lock, detention key, coin door, tape | Applied on the host and mirrored to every client |

Clients' sprays, peels and clouds ignore replica NPCs, so nothing is applied twice. Other clients see inert copies of host item objects.

- **Stay local:** Zesty, boots, teleporter, invisibility, nametag, reach extender, grappling hook, map, YTPs, sticker pack and glue stick.
- **Not supported:** portal poster and GameLock keys.
- **Teammates:** never hit by each other's sprays or peels; "friendly item effects" is still a planned lobby option.
- **Rejected uses:** if the host rejects a use, the item is still spent.

## Baldi

- **Invisible teammates:** Baldi's nearest-player chase now ignores teammates hidden by a chalk cloud, matching the game's own sight rules.
- **Alarm clock:** temporarily replaces the chase target while Baldi goes to the clock.

## Spectate screen

The spectate screen uses the game's own font.
- **Header card:** "SPECTATING <name>" with the teammate's colour and a recoloured portrait.
- **Roster:** every player marked alive, caught or hiding; the watched teammate is highlighted.
- **Hint:** the switch controls.
- **Hiding:** a teammate in a locker gets a dark "<name> is hiding" screen instead of plain black.
- **Waiting:** "waiting for a teammate".
- **Switching:** a short dither dissolve.
- **Visibility:** hidden while alive, at game over, while paused, and while the menu or the F2 console is open.

## Verification

Run `scripts/lab-suite.ps1`. Each feature has a single-process tour against real native components, using simulated teammates. Final merged build: transport 22/22. Eleven single-process tours, 184 checks, all passing:
- lobby 32, spectator 16, Hide & Seek audio 7, spectate screen 18
- Beans 14, Arts and Crafters 16, Mrs. Pomp 17, The Test 15, Dr. Reflex 16, Chalkles 15
- client items 18

Two-process tests: LAN join 13/13, NPC sync 13/13 and core co-op 14/14. No exceptions in any log.

These tours use simulated teammates and real packets. A real multi-player session is still needed to judge feel, timing over latency and natural encounters.
