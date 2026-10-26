# NPC synchronization coverage in 0.5.0

This checklist is based on the installed Baldi's Basics Plus 0.14.5 managed game code, rather than assuming every character on the community wiki appears in this version. The installed `Assembly-CSharp.dll` SHA256 is `635B0D164A7050942077F3A7509AD2E4DC49A4D860E476EBE6906C7508DFA367`. Source inspection establishes how the native abilities work; it does not establish a successful multiplayer encounter. No game code or assets are included in the release.

The [community character index](https://baldis-basics-in-education-and-learning.fandom.com/wiki/Character) was supplied as a reference. Its page could not be retrieved in this environment. The 13 non-null identities below come from the installed `Character` enum; names in parentheses are the installed class names.

## Shared foundation

Every NPC in the host environment catalog has ordered prefab spawning/despawning, host positions/facing, interpolated client movement, semantic visibility, interaction state, animator states/parameters, and sprite references. Version 0.5.0 additionally mirrors sprite child-object activation; this captures native state switches such as Chalkles' chalk/flying forms. Renderer culling stays local. This foundation alone does not reproduce a character's gameplay ability.

| Character | Implemented multiplayer behavior | Required synchronization still outstanding |
| --- | --- | --- |
| Baldi (`Baldi`) | Nearest eligible teammate pursuit, host catch decisions, synchronized hide-and-seek opening, interpolated motion | Comprehensive sound/hearing attribution from all remote item effects; special challenge variants need validation |
| Playtime (`Playtime`) | Remote pursuit, one owner per jump-rope round, owner-only native minigame, jump/hit/end reports, host reward/cooldown | Scissors and all item interruptions; natural simultaneous encounters need broader play testing |
| Arts and Crafters (`ArtsAndCrafters`) | Shared foundation and sprite child activation | Remote-player gaze/jealousy selection, attack ownership, echo transforms, paired player/Baldi teleport and cleanup |
| It's a Bully (`Bully`) | Remote contact, owner inventory theft/no-item push, duplicate protection, host hide acknowledgement; physical collider/obstacle visibility, remote sight taunt/guilt, spawn buffer includes remote teammates | Uncertain theft acknowledgement recovery; wider natural encounters with multiple contenders; taunt audio propagation is not added here |
| Principal of the Thing (`Principal`) | Remote rule reports, host sight/sensitivity and pursuit, culprit-only detention, shared locks/clocks, corrected timers | Full item-specific guilt interactions and natural simultaneous mixed local/remote detentions |
| 1st Prize (`FirstPrize`) | Remote visible target selection and native charge destination; replicated hug force | Wire-cut/scissors effects and high-speed collision timing across latency |
| Gotta Sweep (`GottaSweep`) | Host movement with replicated sweeping force and separation/stale-state cleanup | Broad natural crowd collision testing |
| Cloudy Copter (`Cumulo`) | Host wind-region active state, position, collider size, direction and speed; native client belt triggers apply/remove the wind force | Remote-aware destination choice is not required for its native hallway selection; wind audio/volumetric audio bounds and squish graphics are not fully mirrored |
| Chalkles (`ChalkFace`) | Shared foundation, including chalk/flying child activation; normal host door locks use shared world state | Canonical chalkboard spawn locations, remote room occupancy/charge, charge shader amount and complete laughter lifecycle |
| Beans (`Beans`) | Shared foundation | Remote target selection, gum projectile spawn/flight/hit ownership, gum movement/UI effect and expiration |
| Mrs. Pomp (`NoLateTeacher`) | Shared foundation and active sprite changes | Per-player appointment/classroom assignment, timer/map popup, on-time reward, remote chase/drag and release |
| The Test (`LookAtGuy`) | Shared foundation | Each player's gaze report, freeze authority, head pressure, blindness/fog/gauge, world time-scale effects and respawn timing |
| Dr. Reflex (`DrReflex`) | Shared foundation | Remote target/test ownership, owner-only test UI/result, hammer hit/squish duration and cleanup, remote hearing |

Eliminated players are excluded from remote NPC pursuit, contact rounds and Principal eligibility. Locker occupants are also excluded from remote sight targeting and Baldi catch decisions. Their local character cannot start Playtime or Bully interactions through the mod; they cannot win a rope reward after elimination. A rope round owned by an eliminated/disconnected player is canceled.

## Other installed character and actor classes

The assembly also contains `Student`, `StudentCrowd_Leader`, `Balder_Entity`, and `TestNPC` subclasses beyond the 13 named enum identities. They receive only the shared foundation if present in the host NPC catalog. Their special scenario behavior is not claimed supported. `TestNPC` is a development class, not a promise of a normally spawning character.

Johnny's shop uses separate shop synchronization rather than NPC pursuit. Shop stock, purchases and wallet behavior are described in the README/shop documentation; visitor dialogue remains local. Balloon/event actors, field-trip actors, generated crowd members, projectiles and menu portraits are not automatically covered by the NPC catalog. They require separate records and ownership rules if their gameplay affects teammates.

## Native code evidence and implementation

- `Bully.SetComponents` toggles colliders and `NavMeshObstacle` in addition to reference-counted `Entity.SetVisible`. Client initialization calls `Hide`, so copying only semantic visibility left a visible replica physically nonblocking. The new replica path mirrors the physical components without incrementing visibility counters. `Bully.Spawn` only checks native local players; a host postfix also rejects a spawn inside a recent living remote teammate's native buffer.
- `Bully_Active.PlayerSighted/PlayerInSight` taunt and set bullying guilt. Host remote sight now drives the corresponding behavior; native theft remains recipient-owned and token-deduplicated.
- `Cumulo.Blow/UpdateWind/StopBlowing` manipulate a detached `BeltManager`. NPC transforms alone cannot synchronize this region. The new bounded presentation record copies that region and uses native `BeltManager` triggers and `OnDisable` cleanup rather than approximating wind contact by distance. Replica despawn also destroys the detached wind manager.
- `ChalkFace.Activate/Teleport/Cancelled` toggle child GameObjects. The new per-sprite active state copies those forms while leaving renderer culling local. It does not run Chalkles' charge, room occupancy, or attack on clients.
- `Beans.Spit`, `ArtsAndCrafters.TeleportPlayer`, `NoLateTeacher.PlayerCaught/Drag`, `LookAtGuy.Blind/FreezeNPCs`, and `DrReflex.StartTest/Hammer` all perform gameplay work beyond pose/animation. Those abilities are explicitly pending above.

## Verification boundary

The existing 0.4 NPC lab exercises canonical catalogs/despawn, animation correction, remote 1st Prize target selection, owner-only Playtime and single reward, deduplicated Bully theft, Sweep force cleanup, Bully show/hide, and remote Principal detention. Version 0.5.0 adds `bullyPhysicalMatches` and `cloudWindActive` diagnostics for focused checks.

The opt-in `-bt-lab -bt-diagnostics -bt-npc-expansion-tour` lab loads native Bully, Cloudy Copter and Chalkles prefabs. It serializes the actual presentation record, changes local state, then reads the record back into native components. Fourteen checks cover complete record consumption, Bully physical show/hide, Chalkles child forms, Cloudy wind region/direction/speed, native trigger force and disable cleanup, remote spawn buffer and eliminated/locker-player target exclusion. This is a single-process serializer/native-component test, not a claim of a natural two-player encounter. Results are written to `artifacts/tests/npc-expansion-results.json`. Current-run results are recorded in the release validation log; implementation is not a claim that every row or every natural encounter passed a live test.
