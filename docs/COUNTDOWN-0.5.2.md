# Locker countdown and spawning, 0.5.2

The installed game's HappyBaldi.SpawnWait waits for its countdown audio to finish, then calls BeginSpoopMode, SpawnNPCs and StartEventTimers. The multiplayer release now follows those calls once per environment; an existing NPC no longer suppresses the remaining queue. It waits for the native manager/environment to exist before announcing completion.

The original EnvironmentController.NPCSpawner checks only local player zero. Its radius and both-axis clearance can keep NPCs queued indefinitely around a stationary player in a locker. Multiplayer applies the same native buffer geometry to every exposed living teammate, ignoring locker occupants and spectators. The public SpawnNPCs entry initializes the original pending/tile lists and starts this shared spawner, avoiding a patch on a tiny iterator factory that Mono can inline. Native SpawnNPC and anger-on-spawn still perform actual creation.

F2 `status` reports the countdown step, queued audio, configured NPC count and pending spawn count; `npcs` lists actual actors. A nonzero queue can still be legitimate while exposed players block native spawn positions. No forced spawn timeout bypasses their clearance.

The isolated locker lab exercises both first and second floors with the native locker active through the real countdown. It deliberately places all spawn cells inside the stationary player's native buffer, checks an exposed teammate still blocks there, and verifies the hidden host does not prevent the queue completing. This fixture is not a reproduction from the user's original save.
