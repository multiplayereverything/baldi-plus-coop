# Bug-fix pass, 0.5.4

A source review of the network, migration, shop, hiding, NPC, spectator and menu code. Each fix below was checked against the installed game's original code where game behaviour was involved. Protocol stays 10; the exact mod version changes, so everyone must update.

## Gameplay

- **Principal froze after chasing a remote player.** The mod's remote chase held navigation priority 63 and never released it. The game's state machine rejects lower-priority states, so wandering and detention navigation were ignored afterwards. The chase now releases priority on exit, as native `DrReflex_Chasing` does.
- **Playtime kept running at chase speed** after a remote player escaped her. Native `PlayerLost` only fires for the host's own player. The mod now applies the same turn-around and walking speed when it loses a remote target.
- **Bully could become permanently invisible.** A late theft reply hid him again after a timeout or detention had already hidden him. Visibility is reference-counted, so his next spawn stayed invisible. Replies now hide him only while he is still active.
- **Baldi was deaf for the whole chase on the host.** Noise and item sounds were always blocked once he was chasing, even with every player hidden. With no exposed teammate to chase, he now hears normally.
- **Clients could not restock Johnny.** The distance check measured to the store component, which the game places at the level origin rather than the room. It now measures to the shop room's middle.
- **Clients ran `BeginSpoopMode` twice**, doubling the ambience timer. Both release paths now share the once-per-environment guard.
- **Eliminated spectators could start the countdown** on the next floor after a migration. That request is now ignored.

## Network and migration

- **Ghost peers.** When the network event queue was full, the "disconnected" event itself was discarded. A client could then stay "connected" to a dead host, or the host could keep a ghost player. Only data packets now count against the cap.
- **Kicked right after migration.** Migrating more than 90 s into a floor tripped the level-loading timeout before the new host restarted the floor. Migration now resets that timer.
- **Solo fork.** If only the successor lost its link to a still-running host, it waited 18 s and then restarted the floor alone. It now disconnects with an explanation when nobody follows.
- **Strangers during migration.** A new player could join the room mid-migration, taking a slot and counting toward the reconnect quorum. Normal joins are now refused until migration finishes.
- **Lost pickups.** A shared-item grant that took longer than 10 s was settled as delivered. A later "inventory full" reply then deleted the item for everyone. Grants now settle only on an ack or a disconnect.
- **Lost shop refunds.** A purchase grant that reached a client before its world was ready, or for an unknown shelf, was dropped silently or disconnected the client. It is now acknowledged as failed so the host refunds.
- **World snapshot failures** skipped the rest of the host's frame, including catches and team-wipe checks. They are now contained, and an over-size floor disconnects with a clear message.
- **NPC edge cases.** A despawned NPC left in a pending round threw every frame. An NPC with too many animators or sprites broke world sync. Long floors could hit the 4,096-command token limit and disconnect. None of these happens now.
- **Late RoundState** could re-lock the spectator camera during game over or loading.
- **Host shared-item claim leak.** If the game's own pickup threw an error, the host could not take shared items for the rest of the floor.

## Menu and F2 console

- While the menu or console is open, item-slot keys, the scroll wheel, pause and the map key no longer reach the game.
- After a scene change the console keeps blocking the title-screen cursor.
- Log lines arriving mid-frame no longer cause IMGUI layout errors.
- A failing status refresh no longer aborts the frame.

## Robustness and performance

- Harmony patches install per class. A single missing game method is logged instead of silently disabling every later patch.
- The `-bt-lab` check is cached instead of allocating on each call, about 20 times per frame.
- `Animator.parameters` is read once per animator instead of once per synchronized parameter.

## Verification and limits

Both loaders compile and all 22 transport/claim checks pass. No game-process fixtures or two-player sessions were run for this pass. These fixes come from code review against the original game code and need real-session confirmation.

Not changed in this pass:

- **Glue sticks.** Glue-stick stock and price still follow the host's own sticker count rather than the buyer's. Fixing this needs a protocol change.
- **Late shop acks.** A failure ack arriving after the host's 12 s timeout is still not refunded.
