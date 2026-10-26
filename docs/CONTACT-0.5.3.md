# Second-floor contact investigation, 0.5.3

Reported behavior: Baldi failed to catch a player on floor two, then capture worked after entering and leaving a locker. One player was spectating; the affected player's host/client role was uncertain.

## Original code

Baldi_Chase.OnStateTriggerStay requires a valid Player collision, then asks Baldi's Looker.Raycast whether the target is sighted. If the player is not invincible, an apple is consumed or CaughtPlayer runs. Entity interaction disables are counted; SetInteractionState updates the entity's physics layer. HideableLocker.Clicked acquires interaction/freeze locks, and its CameraReset releases them on exit. A normal locker cycle balances its own locks; it is not a general reset of all invulnerability.

Remote teammates have no native player collider on the host, so the mod checks their position and dispatches capture centrally. Its previous ClearWorldLine used all physics layers, unlike the original Looker mask. A collider on an excluded native sight layer therefore blocked multiplayer capture even when the native raycast returned visible. This discrepancy is confirmed in runtime fixtures on both floors.

## Change

Remote capture now calls the original Baldi Looker.Raycast through a reusable, collider-free target at the teammate's authoritative position. The target uses the native player entity target layer rather than a spectating host's current non-interacting layer. Native layer filtering, ignored transforms and obstruction handling are preserved. The existing distance, ready/fresh-pose, locker and elimination conditions remain. Local host capture still follows native trigger processing.

No interaction locks are forcibly cleared. Doing so could break legitimate locker, spectator, teleport or other item effects. Remote apple/invincibility synchronization is separate outstanding work; this patch does not implement it.

## Diagnostics and evidence limits

F2 `contacts` reports local layer, interaction lock count, interaction-disabled/frozen/invincible/tagged/invisible/locker state, deathSent, Baldi state and sight mask. Each peer includes ready/pose age, elimination/locker status, distance, native sight and named colliders detected by the former all-layer path. `save` includes this section. Gather reports from both host and client if the exact reported scenario recurs.

The isolated `-bt-lab -bt-diagnostics -bt-contact-tour` fixture loads floors one and two. An excluded-layer collider reproduces the old false obstruction; an included-layer collider still blocks native sight. With the host spectating, a locker-flagged teammate remains protected and an exposed teammate is captured without a locker cycle after the fix. The blocked baseline passed 10/12, failing capture on both floors. This is a native-component/handler fixture with synthetic remote peers, not the user's original session or a two-machine encounter.

The exact reason entering and leaving a locker changed the user's session is not established. This patch fixes the confirmed raycast discrepancy and adds evidence to distinguish it from an interaction-lock, stale pose, locker flag or native state problem.
