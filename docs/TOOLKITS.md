# Toolkit record

The requested repositories are local development dependencies, excluded from release packages.

| Toolkit | Pinned checkout | Used for |
| --- | --- | --- |
| Anti Slop | `439a2ef5b9dd0ebe74e8113ff43f5928d0fe2a6a` | During-development UI and copy checks, contrast measurements, delivery review |
| Universal Modder | `6a8865ae171b406f50892c7f46fc5f45655269eb` | Knowledge-base search, Unity engine scan, modding workflow and field journal |
| REA | `c12d9eda87aa5a022ee7e18ea96d8d03bfd12e96` | Connected managed assembly and member inspection; evidence IDs in MODLOG |
| Watch Skill | `f1317c8fe64744a606c31867b05fbbe3144268c6` | Local standard-extra installation, dependency diagnostics, isolated verification contracts |
| Reverse Skill | `cab634bd855fc287f6e420c1f36fd1a6b9245960` | Installed Codex router and full local specialist pack; available for future analysis |

Reverse Skill's official Codex adapter is installed at `~/.codex/skills/reverse-skill-router/SKILL.md`. Its local installation note points to this project's complete `toolkits/reverse-skill/` checkout, preserving the router's rules, references, scripts and specialist modules. The adapter and package use the same pinned commit. Resource presence was checked, including the .NET/C# module. This installation did not execute the package's bootstrap or analysis scripts.

Watch Skill 1.4.3 is installed under `.venv/`. `scripts/watch.ps1` sets its workspace-local data directory. The checked-in `.mcp.json` is an example for clients that read project MCP JSON. Codex uses TOML configuration; the CLI is already usable here, but adding a JSON file does not connect new MCP tools to this active session. No global agent configuration was replaced.

Run `scripts/watch.ps1 verify run tests/watch-contract-v7.json --dir .` from this folder to verify the current focused synchronization report, required transport report and both builds. Older contracts remain historical. These contracts check files and component reports; they do not prove all gameplay works. Visual inspection and live game evidence are recorded separately.

Universal Modder found no Baldi field note. Its scanner reported Unity Mono 2020.3.49f1, x64 and no anti-cheat indicators. The installed game reports version 0.14.5 in MelonLoader's startup log. The scan can mistake toolkit contents for game features when invoked from this development folder, so loader availability was also checked directly in the game root.

The Windows Computer Use helper returned `native pipe is unavailable` after retry and reset. Runtime screenshots were captured by the game's Unity ScreenCapture API and inspected directly. No native UI click-through is claimed.
