# Baldi Together handoff

Updated 2026-10-27. Start here when picking the project up on a new PC. A new session should be able to continue from this file, `AGENTS.md`, `README.md` and `MODLOG.md` alone.

## Where things stand

- **Version 0.9.2**, protocol 15, exact version match required. Released as a GitHub pre-release: https://github.com/multiplayereverything/baldi-plus-coop/releases/tag/v0.9.2
- Target: Baldi's Basics Plus **0.14.5** (Steam, app 1275890), Windows x64, Unity 2020.3.49f1.
- Two builds from one source tree: BepInEx 5.4.23.5 (x64) and MelonLoader 0.7.3. They play together; each game copy uses one loader.
- Online play: Steam lobbies and P2P (the game's own Steamworks.NET) or LAN. No global relay; it was removed at the user's request.
- Second PC (2026-10-27): the repo lives outside the game folder (`Desktop/AIStuff/ai-work-pc-games/Baldi Multipplayer Mod`); see the dev setup below.

### Done in 0.9.2

- Repo outside the game folder: `scripts/paths.ps1`, `-bt-workspace`, `-bt-shared`.
- Steam: only members of the host's lobby can connect (`steam-lobby-gate`, with a mutant).
- Random events:
  - client worlds never start their own event timers;
  - `timeLimitChallenge` is in the run state;
  - the host sends announce, begin and end (`Op.EventState`); clients mirror Fog, Mystery Room, Snap and TimeOut.
  - Test: `launch-lab.ps1 -EventSync` with `tests/verify-events.py`, and the `-NoEventSync` mutant; both are in the lab suite.
- Lockdown doors are in the host door sync. Levers and other world buttons are forwarded to the host (interaction kind 4).
- The Pitstop's field-trip hub pick is seeded from the run seed and the floor.
- Lab copies start minimized as small titled windows, at low priority and quality (`scripts/lab-window.ps1`). The soak runs hidden with the game's own settings.
- Quiet-PC soak: 12/12, worst mod cost 0.15 to 0.21 ms per frame.

### Open

1. **Alt+Tab test not done.** It needs visible windows and keystrokes on the user's desktop. The user approved it once, but desktop control of `baldi.exe` was declined. Ask before running it.
2. **Never tested with real players:** two different Steam accounts, friends on separate PCs, four players.
3. **Events still host-only on clients:** Flood (water, whirlpools), Party (balloons, party pickup), Gravity (flippers, flipped view), BalderDash and Student Shuffle. Their NPCs and doors replicate, but each player's own visuals do not. The private research note `private/notes/events-modes-research.md` has the per-event details (decompiled line numbers, not for the public repo).
4. **Lever forwarding not proven in a lab run.** Floor 1 of the lab seed has no host-owned lever; the event lab presses one when the floor has it.
5. **Challenges and Endless:**
   - a challenge win makes the host quit, which disconnects everyone;
   - Endless repeats notebooks, but the mod's notebook model is one-time;
   - Endless item respawns are not in the shared catalog.
   - Neither mode has a lab start path yet.
6. Same-folder setups (two copies from one game folder) can race for `BepInEx.cfg` at startup. Separate lab copies avoid it.

### Next steps

1. Challenges and Endless: a lab start path per mode, then win and results broadcast, a notebook respawn op, and host item respawns.
2. Mirror the remaining events' local effects (Flood, Party, Gravity), with host-owned positions.
3. A two-account Steam session, then a playtest with friends. Ask testers for F2 `save` reports and `Player.log`.
4. Every version: tagged GitHub release (test builds as pre-releases); CHANGELOG, README, MODLOG and this file updated; `um publish check <release folder> --game <game>/BALDI_Data` passing.

## Dev setup on a new PC

1. **Game:** install Baldi's Basics Plus from Steam (0.14.5). The usual path is `C:\Program Files (x86)\Steam\steamapps\common\Baldi's Basics Plus`.
2. **Repo:** clone this repository anywhere. On the second PC it lives in `Desktop\AIStuff\ai-work-pc-games\Baldi Multipplayer Mod`, outside the game folder (the user's choice); inside the game folder as `MultiplayerMod` also works.
   - `scripts/paths.ps1` finds the game: `BT_GAME_ROOT`, else a folder above the repo holding `BALDI.exe`, else the default Steam path. The three Python tests that read game files use the same order.
   - Lab launches pass `-bt-workspace <repo>` to the BepInEx host and `-bt-shared <repo>` to the MelonLoader client, so lab results, logs, saves and command files land in the repo wherever it is. Without them the mod uses `<game>\MultiplayerMod` as before.
   - Commit as `multiplayereverything <347356261+multiplayereverything@users.noreply.github.com>` so no personal email reaches the public history.
3. **Loader dependencies** (git-ignored, under `MultiplayerMod/deps/`):
   - `deps/BepInEx-5.4.23.5/`: extract `BepInEx_win_x64_5.4.23.5.zip` (from the BepInEx GitHub releases). The build reads `BepInEx/core/BepInEx.dll` and `0Harmony.dll`.
   - `deps/MelonLoader-0.7.3/`: extract `MelonLoader.x64.zip` 0.7.3 (from the MelonLoader GitHub releases). The build reads `MelonLoader/net472/MelonLoader.dll` and `0Harmony.dll`.
4. **Compiler:** the .NET Framework 4 C# compiler at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (included with Windows). No SDK is needed.
5. **Build:** `powershell -ExecutionPolicy Bypass -File scripts/build.ps1` writes `artifacts/BepInEx/` and `artifacts/MelonLoader/`. Run `scripts/test.ps1` after transport changes (26 checks plus the no-keepalive mutant). `scripts/package.ps1` makes the release ZIPs in `artifacts/release/`.
6. **Install for play and single-process tours:** install BepInEx 5.4.23.5 x64 in the game folder, then copy `artifacts/BepInEx/BaldiTogether.BepInEx.dll` to `BepInEx/plugins/BaldiTogether/`. `scripts/install.ps1` does the same.
7. **MelonLoader lab copy** (needed by every two-process test): `private/melon-lab/` in the repo is a second game copy with MelonLoader 0.7.3 installed. On the original PC it uses hardlinks to the game files; on the second PC it is a plain copy (`robocopy <game> private\melon-lab /E /XD BepInEx /XF winhttp.dll doorstop_config.ini`, then copy `deps/MelonLoader-0.7.3/*` into it). `launch-lab.ps1` copies the MelonLoader DLL in on each launch. Never edit hardlinked game bytes.
8. **Python:** the tests use only the standard library (Python 3.10 or newer). `scripts/lab-suite.ps1` expects `MultiplayerMod/.venv/Scripts/python.exe`; create it with `python -m venv .venv`.
9. **Tools used by sessions:**
   - `graphify`: rebuild the code graph with `/graphify .`, update it with `graphify update .`; `graphify-out/` is git-ignored.
   - `um`: Universal Modder CLI, used for the release lint.
   - `gh`: GitHub releases.

## Tests and lab scripts

All lab launches use `-bt-lab -bt-diagnostics`. Stop only the PIDs a script recorded; never stop the user's own game.

- `scripts/lab-suite.ps1`: everything: build, transport, every single-process tour, then the two-process LAN, NPC, core, encounter, soak, link and shop tests.
- `scripts/run-tour.ps1 -Tour <name>-tour -Result <name>-results.json`: one single-process tour (add `-Steam` for `steam-tour`).
- `scripts/launch-lab.ps1 -Encounter|-Soak|-NpcSync|-TestActions|-LanTour ...`: BepInEx host plus MelonLoader client. Verify with the matching `tests/verify-*.py`. Options: `-Chaos <profile>`, `-StallHost`, `-NoKeepAlive`, `-NoBuild`, `-Visible`.
- `scripts/link-test.ps1`, `scripts/shop-test.ps1` (`-LateAck`, `-Mutant late-ack|glue`): focused tests with mutation twins.
- `scripts/focus-test.ps1`, `scripts/focus-same-folder-test.ps1`: focus and minimize checks.
- Failed attempts keep their evidence in `private/failures/<name>-<time>/`.

## Rules

- Never commit or distribute game files, decompiled code, lab copies, `deps/` or `toolkits/`. `.gitignore` covers them.
- Never put the Discord bot token or `discord.json` in any commit or message.
- The public repo holds clean snapshots only. The original PC's local git history contains personal emails and was never pushed.
