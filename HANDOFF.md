# Baldi Together handoff

Updated 2026-10-27. Start here when picking the project up on a new PC. A new session should be able to continue from this file, `AGENTS.md`, `README.md` and `MODLOG.md` alone.

## Where things stand

- **Version 0.9.1**, protocol 14, exact version match required. Released as a GitHub pre-release: https://github.com/multiplayereverything/baldi-plus-coop/releases/tag/v0.9.1
- Target: Baldi's Basics Plus **0.14.5** (Steam, app 1275890), Windows x64, Unity 2020.3.49f1.
- Two builds from one source tree: BepInEx 5.4.23.5 (x64) and MelonLoader 0.7.3. They play together; each game copy uses one loader.
- Online play: Steam lobbies and P2P (the game's own Steamworks.NET) or LAN. No global relay; it was removed at the user's request.
- Work was **paused** on 2026-10-26 at the user's request. Nothing was left running.

### Done in 0.9.1

- Glue stick priced per player (1000 + 500 per glue stick owned, sold out at 4).
- Shop purchases confirmed after the 12 s deadline are settled for 120 s (refund and restock, or sale).
- Pomp fix: a Mrs. Pomp appointment that reached a client before its Pomp replica existed was dropped. The client now retries it for 5 s. This was the long-standing "encounter 44/45".
- Lab: auto-join retries, failure evidence capture (`scripts/save-failure.ps1` to `private/failures/`), `scripts/shop-test.ps1`, `launch-lab.ps1 -NoBuild` for A/B runs of prebuilt DLLs.

### Open

1. **Alt+Tab test not done.** The user approved a visible test; no keys were sent. Two windows from the same folder reached the same level, but desktop control of the `baldi.exe` windows was declined. Steps are in MODLOG (2026-10-26 entry, "Alt+Tab").
2. **Soak timing limits unverified.** The two-floor soak's playability checks pass, but its timing limits failed while other games loaded the CPU. 0.9.0 fails the same way under the same load. Rerun `launch-lab.ps1 -Soak` plus `tests/verify-perf.py 700` on a quiet PC. Quiet-PC baseline: about 0.2 ms mod cost per frame.
3. **Never tested with real players:** two different Steam accounts, friends on separate PCs, four players.
4. Same-folder setups (two copies from one game folder) can race for `BepInEx.cfg` at startup. Separate lab copies avoid it.

### Next steps

1. On a quiet PC: rerun the soak, then the full `scripts/lab-suite.ps1`.
2. The Alt+Tab test, with the user's OK each time; it needs visible windows and real keystrokes.
3. A two-account Steam session, then a playtest with friends. Ask testers for F2 `save` reports and `Player.log`.
4. Every version: tagged GitHub release (test builds as pre-releases), CHANGELOG/README/MODLOG updated, `um publish check <release folder> --game ../BALDI_Data` passing.

## Dev setup on a new PC

1. **Game:** install Baldi's Basics Plus from Steam (0.14.5). The usual path is `C:\Program Files (x86)\Steam\steamapps\common\Baldi's Basics Plus`.
2. **Repo:** clone this repository into the game folder as `MultiplayerMod`:
   `git clone https://github.com/multiplayereverything/baldi-plus-coop "<game folder>\MultiplayerMod"`
   The scripts find the game by walking up to `BALDI.exe`; elsewhere, set `BT_GAME_ROOT` to the game folder. Commit as `multiplayereverything <347356261+multiplayereverything@users.noreply.github.com>` so no personal email reaches the public history.
3. **Loader dependencies** (git-ignored, under `MultiplayerMod/deps/`):
   - `deps/BepInEx-5.4.23.5/`: extract `BepInEx_win_x64_5.4.23.5.zip` (from the BepInEx GitHub releases). The build reads `BepInEx/core/BepInEx.dll` and `0Harmony.dll`.
   - `deps/MelonLoader-0.7.3/`: extract `MelonLoader.x64.zip` 0.7.3 (from the MelonLoader GitHub releases). The build reads `MelonLoader/net472/MelonLoader.dll` and `0Harmony.dll`.
4. **Compiler:** the .NET Framework 4 C# compiler at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (included with Windows). No SDK is needed.
5. **Build:** `powershell -ExecutionPolicy Bypass -File scripts/build.ps1` writes `artifacts/BepInEx/` and `artifacts/MelonLoader/`. Run `scripts/test.ps1` after transport changes (26 checks plus the no-keepalive mutant). `scripts/package.ps1` makes the release ZIPs in `artifacts/release/`.
6. **Install for play and single-process tours:** install BepInEx 5.4.23.5 x64 in the game folder, then copy `artifacts/BepInEx/BaldiTogether.BepInEx.dll` to `BepInEx/plugins/BaldiTogether/`. `scripts/install.ps1` does the same.
7. **MelonLoader lab copy** (needed by every two-process test): `MultiplayerMod/private/melon-lab/` is a second game copy with MelonLoader 0.7.3 installed. On the original PC it uses hardlinks to the game files; a plain copy of the game folder also works. Install MelonLoader 0.7.3 into it. `launch-lab.ps1` copies the MelonLoader DLL in on each launch. Never edit hardlinked game bytes.
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
