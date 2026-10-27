# Baldi Together development

Before starting work, read `HANDOFF.md` (current state, open items and dev setup on a new PC) and this project's `README.md`. On the original PC an older, longer `../handoff.md` also exists in the game folder; it is not in git. Use the handoff for current state, active files, failed attempts and next steps, and the README for supported behavior and known limitations. Keep the handoff current when those details change.

## Code graph (use it first)

This project has a Graphify knowledge graph in `graphify-out/`. It covers all code and docs: 98 named communities. `TogetherRuntime` is a partial class spread over most of `src/`, so plain text search misses how features actually connect. Use the graph for every code question.

- **Before reading or changing code,** read `graphify-out/GRAPH_REPORT.md` for the community map, god nodes and known weak spots.
- **To find related code,** query the graph before grepping:
  - `graphify query "<question>"` for broad context;
  - `graphify path "A" "B"` to see how two parts connect;
  - `graphify explain "Symbol"` to see what a class touches.
- **Before changing a feature,** run `graphify explain` on it and check which other communities depend on it. Watch especially for cross-feature reach-ins to private state, for example `ItemsSync` editing `HidingSync.chaseTargets`.
- **After changing code,** run `graphify update .` (code only, local, no LLM) so the graph stays current. Use `/graphify . --update` when docs changed.
- **If `graphify-out/` is missing,** rebuild it with `/graphify .`. `.graphifyignore` excludes media and old test contracts; `graphify-out/` is git-ignored.

Keep README.md and MODLOG.md current with implemented features, evidence and limitations. Build both loaders with `scripts/build.ps1`; run `scripts/test.ps1` after transport changes. Never distribute game assemblies, private decompilation, lab copies or dependency/toolkit caches. Each game copy uses exactly one loader.

Requested toolkits are checked out in `toolkits/anti-slop`, `toolkits/universal-modder-source`, `toolkits/rea`, and `toolkits/watch-skill-source`. Read their applicable skills when using them. Anti-slop during-development mode was selected for the initial session; future sessions follow its mode selection rules. REA is already connected in the initial session. Watch Skill is installed locally with standard extras; `scripts/watch.ps1` confines storage to `private/watch-data`. Native Windows Computer Use was unavailable during the initial session; do not claim UI click-through from source review.

For new runtime evidence launch only test-owned processes with `-bt-lab -bt-diagnostics`; optional `-bt-host`, `-bt-join 127.0.0.1`, `-bt-auto-start` automate the LAN test. Lab mode redirects game save paths into the workspace. Never modify hardlinked game files in `private/melon-lab`: their bytes are shared with the installed game.
