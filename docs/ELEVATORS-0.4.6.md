# Multiplayer elevator transitions, 0.4.6

The native ElevatorScreen.Initialize queues its zoom introduction and closing doors. Once loading is ready, StartGame opens the doors, runs StartDelay and ZoomExit, calls BaseGameManager.BeginPlay, and destroys the screen. MainGameManager uses these screens for restart/floor changes, while PitstopGameManager can reuse one for its next destination.

The mod previously removed these screens and called BeginPlay directly. Clients now instantiate the game's elevator prefab when loading a host destination and retain it through the native exit sequence. The host's native screen is also allowed to finish normally. If no screen exists, automatic direct entry remains a fallback.

The host sends the existing generation-scoped Begin notification when its elevator starts opening. A client waits for its own matching layout/item validation and that host notification, then exits automatically. A client whose native screen became ready before validation is retried once it is allowed to start; duplicate starts are blocked. The native animation and timer routines remain intact. Environment gameplay remains inactive until native BeginPlay. Replacing an unfinished client screen clears the old Singleton immediately before creating its replacement; disconnect also removes the owned screen.

MelonLoader's automatic patch pass is disabled for this assembly because the mod explicitly installs its Harmony patches once during loader initialization. Unity runtime objects are still created later, when they are available. Installation is idempotent. This prevents the elevator-start guard from running twice and cancelling its own permitted start.

Host progression remains authoritative and does not wait for every client. Load speeds can differ, so these are native local transitions following the host's destination/start, rather than identical animation frames across machines. This change does not replicate the host's elevator results panel or field-trip presentation. Clients still need no separate ready button.

Focused verification uses a real BepInEx host and MelonLoader client, an eight-second client validation delay, initial entry, restart through the pitstop and return to the floor. The explicit lab flag is `-bt-elevator-tour`; the live evidence checker is `tests/elevator-live.py`. Results are stored in artifacts/tests/elevator-results.json. Physical menu clicks and unrelated NPC/shop/network suites are outside this check.

All eight checks passed: initial entry, restart/pitstop entry, return to the floor, validation before every exit, final native screen cleanup, host progression, matching worlds and one elevator patch installation in each loader. Native BeginPlay followed each client's accepted exit sequence after approximately 1.17, 1.22 and 1.32 seconds respectively in this run. The tested destinations were MainLevel_1, Pitstop and MainLevel_1 after restart. Both loader builds compile and their installed hashes match.
