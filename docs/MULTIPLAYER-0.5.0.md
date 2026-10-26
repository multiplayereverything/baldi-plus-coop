# Multiplayer rules and recovery — 0.5.0

## Capture and spectating

The host decides each player's capture separately. A caught player becomes eliminated for the rest of the round. Their avatar/name tag disappear; movement, items, pickups, shop actions and world interactions are blocked. Native NPC eligibility ignores eliminated peers. The local entity is hidden/tagged/frozen with owned counters that are released on reset.

Spectating follows the eye-level position and facing of a living, recently updated teammate. Left/Right or Tab changes teammate. There is no free camera, enemy selection or look-around input. The watched player's body is hidden only for that viewer. If the teammate is inside a locker, or no eligible teammate has a recent pose, the view is concealed. The personal inventory/stamina HUD is hidden while spectating; it does not pretend to show the watched teammate's inventory.

Elimination cannot be cleared by a later pose or an alive round-state update. A true floor/attempt change clears it; migration's controlled reload retains it. A team wipe invokes the native ending on all peers. No revive feature is present.

Living remote teammates can activate elevators while the host spectates. The host validates their current-generation, recent pose inside the native elevator trigger, then applies the game's failure quota or final-exit requirements. Elevator states are shared; clients cannot independently consume failures or finish a floor.

## Host migration

The host shares a room roster with stable player identities and advertised IPv4 listener addresses. On graceful Leave or detected transport loss, one fixed successor—the lowest eligible client ID—takes over. Remaining clients reconnect to that successor, validate room/version/game identity and receive newly assigned socket IDs. Reconnect/listener retries are bounded to 18 seconds; an unavailable selected successor does not trigger competing fallback hosts.

An active floor is restarted through the native reload path with the same scene and seed. Generated floor progress, NPC activity and shop/item-world state start again; this is not a complete live-state snapshot. Captured players remain captured, even if the new host is one of them. The old host leaves the roster. In the lobby, the room simply resumes with the remaining players.

LAN addresses and the selected host's listening port must be reachable. No relay or NAT traversal is provided. This mechanism does not fence an original host that stays running across an arbitrary network partition; it is not a quorum-based distributed service. A failed recovery reports a timeout rather than claiming the room recovered.

## Compatibility and connection quality

The Compatibility page shows each accepted player's mod version and whether both the exact version and installed game assembly hash match. The handshake rejects mismatches before joining. Other installed plugins and their settings are not automatically compared. Migration reconnects perform the same version/game checks.

The supplied five-level connection artwork appears beside lobby names and visible in-game name tags. Signal bars use measured RTT: up to 70/100/150/200 ms, then above 200 ms. Green/amber/red distinguish the quality range; unavailable or older-than-eight-second samples appear gray. A client directly measures only its host link; other clients show Via host, without inventing peer-to-peer measurements. Local is a local-player label, not a measured zero-latency network claim.

## NPCs

See [NPC synchronization coverage](NPC-COVERAGE-0.5.0.md) and the README checklist for every named installed NPC and required ability work. The Bully, Cloudy Copter and child-sprite changes in this release do not imply every NPC ability is complete.

## Verification scope

Focused native-component checks cover individual capture, locked spectator targeting, hidden locker/no-target views, disabled interaction, no mid-round revival, round reset and native team wipe. NPC checks cover actual serialized Bully components, Cloudy wind triggers/cleanup and Chalkles child activation. Compatibility checks cover native page transitions, build mismatches, measured thresholds, stale samples and the supplied artwork. Those fixtures invoke actual component handlers rather than physical mouse/keyboard input.

Live multi-process migration checks use both BepInEx and MelonLoader, with an original host, a promoted host and a reconnecting client. Recovery is checked separately from the single-process spectator/NPC fixtures. Exact current-run results are recorded in MODLOG; no WAN/partition or exhaustive natural NPC encounter coverage is claimed.

Current release checks: 12 spectator, 16 compatibility UI/quality, 14 NPC expansion, 15 remote elevator, 22 transport/claims, four live handshake rejections, ten lobby-migration role checks and twelve active-floor checks each for graceful departure and unannounced TCP loss: 117 passed, zero failed. Graceful recovery promotes BepInEx; unannounced-loss recovery promotes MelonLoader. Each active-floor run retains a captured replacement host and a living reconnected client, matches seed/scene/layout, and resumes native play in the next generation. UI captures were visually inspected. These are focused fixtures, not a broad multiplayer play-through.

The UI keeps the native white menu, game font and dither. Anti-slop during-development delivery gate: ENERGY 1, RHYTHM 1, MOTION 1. No unrelated visual effects were added.
