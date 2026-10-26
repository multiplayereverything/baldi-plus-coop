# NPC synchronization in 0.4.0

The installed 0.14.5 game code was inspected with REA and existing private decompilation. Assembly SHA256 remains `635B0D164A7050942077F3A7509AD2E4DC49A4D860E476EBE6906C7508DFA367`. Selected managed evidence: `ev_a8c9e13c485f8421ba6244fe1f233271ba2a3991f18d6fc89a5d96fd994dbd45` (summary `ev_463732f162e25eb7b943bd163a7fc60ca626d47dea2e83d04cba7cc44ea023a7`). No game assemblies or decompiled sources ship with the mod.

## Scope

| System | Synchronized | Remaining limits |
| --- | --- | --- |
| NPC catalog | Host prefab names, character identities, ordered spawn/despawn, poses | Requires matching loaded game prefabs; custom mods not validated |
| Presentation | Entity visibility/interaction, animation layers/parameters/timing, sprite references | Local culling is retained; general audio, echo transforms and projectiles are separate |
| Playtime | Remote pursuit, one owner per rope round, native owner UI, count/hit/end reports, host cooldown and team reward | Local minigame reports are trusted within bounded round ownership; remote scissors/item effects remain separate |
| It's a Bully | Remote contact, native personal inventory theft or no-item push, acknowledgement, host hiding, duplicate-command protection | Personal inventory isn't continuously mirrored; an uncertain acknowledgement can time out; collider/obstacle hiding is not mirrored by generic presentation |
| Gotta Sweep | Host movement addend/multiplier applied to nearby remote players and cleaned up afterward | Client contact uses replicated position and clear world line, so latency affects contact timing |
| 1st Prize | Native charge targeting for visible remote players and replicated hug movement force | Full natural high-speed collisions and wire-cut item effects need broader play testing |
| Principal | Native guilt reports, host sight/sensitivity, remote chase, culprit teleport/gauge, room locks/clocks, host timer correction | Rule reports trust the owning client; special item effects and simultaneous mixed host/remote detention need broader testing |
| Beans, Arts and Crafters, Dr. Reflex and others | Generic position, visibility and available animation/sprite state | Gum/projectiles, Crafters teleport/echo attack and special minigame gameplay are not implemented by presentation replication |

## Game code findings

- `NPC.Update` is only one AI entry point: `Looker.Update` independently dispatches sight callbacks. Both are blocked on replicas.
- `EnvironmentController.SpawnNPCs` queues spawn times based on the local player. Replicas now use host snapshot records and `SpawnNPC` directly instead.
- `Playtime.StartJumprope` creates an owner camera/UI object, while `Jumprope.End` awards points locally. The host reserves a remote round without constructing host UI; the owner runs the native game and the host issues the single reward.
- `Bully.StealItem` operates on a personal ItemManager. Host commands target one owner and carry an epoch/token; duplicate commands cannot remove another item.
- `GottaSweep.VirtualUpdate` and `FirstPrize_Active` write MovementModifiers, not just transforms. Replicas apply those forces to their local player and remove them on separation, stale state and teardown.
- `Principal.SendToDetention` and `DetentionRoomFunction` assume a local offender. The remote path uses host sight/navigation and room locks, then teleports only the culprit. Snapshot timers correct client clocks and gauges.
- `Entity.SetVisible` is reference counted. Presentation replay updates semantic counters only when needed, avoiding visibility drift from repeated packets. Replica animation events are disabled so mirrored poses cannot run extra gameplay effects.

## Focused verification

Run `scripts/launch-lab.ps1 -NpcSync`, followed by `python tests/verify-npcs.py`. The isolated BepInEx host and MelonLoader client use a fixed lab seed. Lab methods configure native prefabs, position players, hold navigators, drive rope count callbacks and provide a controlled sweep modifier. Test deaths are suppressed only under this explicit lab mode. These are real two-process game/network checks, with programmatic setup rather than physical input or a full natural multi-floor playthrough.

Checks cover canonical catalogs and despawn, animation correction, remote 1st Prize charge selection, owner-only rope UI, one reward, one theft despite duplicate commands, sweep modifier application/removal, Bully show/hide, remote-only detention and corrected detention time. Full shop, sprite and unrelated malformed-peer suites are not repeated for this change. The required transport/claim suite still runs after wire changes.

Final result: all 13 focused NPC checks and 22 transport/claim checks pass, and both loaders compile. Host and client match eight configured NPCs, then seven after host despawn. Playtime starts one local rope only on the remote owner and awards 25 shared YTP once. Bully steals once despite a deliberately duplicated command. Sweep modifiers attach and are removed. The Principal detains only the remote offender; the stationary host remains outside detention. Clock comparison accounts for independent diagnostic timestamps and matches within 0.4 seconds. Neither final player log contains a game or lab exception. This verifies the controlled scenarios above, rather than every natural NPC encounter.
