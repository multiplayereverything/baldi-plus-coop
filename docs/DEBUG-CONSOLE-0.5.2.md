# F2 debug console

Press **F2** to open or close the console. Escape closes it before the lobby handles Escape. Multiplayer keeps running: the console does not pause the school or the network. Its input ownership blocks local movement, looking, item use and menu clicks through the same guards used by the multiplayer menu. Closing restores the previous cursor state.

The live header refreshes twice a second and shows the scene, generation, role, readiness, environment activation, configured / queued / active NPC counts, hiding countdown step and sequence, running coroutine and finished flags, locker and elimination counts, Happy Baldi activation / spawn-on-finish flag, audio queue activity and time scale. The command field receives focus once when opened, so you can type immediately. The log includes mod status messages and Unity messages, warnings and exceptions. It retains at most 400 entries truncated after 1,800 message characters under a thread-safe lock; it does not call Unity APIs from the threaded log callback.

Commands:

| Command | Purpose |
| --- | --- |
| `help` | List commands. |
| `status` | Print the live header into the log. |
| `peers` | Show version, readiness, pose age, locker state, elimination and position for each player. |
| `npcs` | Show up to 64 loaded NPCs, positions and native behavior states. |
| `clear` | Clear the console buffer. |
| `save` | Save status, players, NPCs and the log as a local text report. |

Reports go to `UserData/BaldiTogether/logs`. Lab reports use `MultiplayerMod/private/debug-reports`. Report timestamps are UTC. Reports contain player names and positions; share them deliberately. No reports are uploaded. No arbitrary code execution or gameplay-changing commands are provided.

For a missing-NPC/countdown report, use `save` on the host and affected client while the problem is present. The NPC count and Happy Baldi flags distinguish an unstarted native spawn sequence from a client replication problem. Audio queue state helps identify a countdown blocked waiting for native queued audio.

Focused lab flag: `-bt-lab -bt-console-tour`. The tour checks bounded storage, useful status fields, console input ownership flag, unchanged time scale, report writing, cursor restoration and clearing. It invokes the same handlers as the UI; it is not physical keyboard or mouse automation.
