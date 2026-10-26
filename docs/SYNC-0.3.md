# Focused synchronization verification, 0.3.0

Scope: the user's hiding countdown, distracting names, Johnny shop synchronization and nearest-player Baldi targeting requests. Both BepInEx and MelonLoader builds compile. Protocol is 5 and every participant needs mod 0.3.0.

The host owns countdown progression, uses its NPC time scale and countdown sticker duration, broadcasts each count, and periodically repeats the current state. Clients suppress the original autonomous HappyBaldi coroutine, play each received beat once and release NPCs on the host's finish state. The host waits for accepted players to be ready before counting. A host-selected nearest player supplies Baldi's navigation destination during normal chase states. Built-in host-only hearing cannot override that destination. The selection uses current accepted poses, excludes stale/disconnected players and respects apple/praise/attack states. Catching a remote player also checks world occlusion.

Name labels are source-reviewed: 13px base text, no panel, a black shadow, distance/aim fading, 35-unit range and world occlusion. They use the actual local player camera and do not show when the lobby is open. This reversible styling change did not receive a separate visual regression suite.

The seven focused live checks in `artifacts/tests/sync-results.json` pass:

1. Multiple distinct countdown beats match on both game processes.
2. Both processes receive all eleven audio events (ten numbers plus the release call) and the host finish state.
3. Baldi's actual navigation state/destination changes to the closer remote player, then host, then remote player again.
4. Both processes have the same real pitstop shop stock and the client receives host replicas.
5. One purchase delivers once and its reservation clears.
6. A deliberately filled inventory rejects delivery, refunds the exact price and returns stock.
7. Insufficient funds and a repeated shelf request are rejected; host/client wallets and recorded charge match the final quoted price.

The first focused run found a price mismatch: the client selected its locally generated price before receiving the new level's first host shop snapshot. Snapshot counters are now reset per level, interactions wait for the current snapshot, and purchase requests quote the item and price. The host rejects stale quotes before charging. The final run has a 300-YTP purchase, one refund, two rejected requests, no remaining reservation and matching balances of 9700 from the isolated test wallet of 10000.

The project-required 22 transport/claim checks passed once after the wire change. The older full gameplay/sprite suite was not rerun. No extra malformed-peer probes or native mouse/keyboard recording were performed. Lab movement and the pitstop transition use game methods; the forced transition logs transient game ItemManager.Update errors during teardown, so this is not an assertion that all normal progression paths are error-free. The actual countdown, navigation destination, stock and purchase results are recorded in the focused report. Sticker/glue reward variants, map purchases, restocking, special Baldi states and separate-PC play were not independently exercised by this focused run.

Run from `MultiplayerMod`:

```powershell
./scripts/launch-lab.ps1 -FocusedSync
python tests/verify-sync.py
```

This mode requires `-bt-lab`, redirects saves into the workspace, and uses the two test-owned loader copies. Its movement, seeded wallet and deliberate inventory changes do not run in normal play. See SHOP-SYNC.md for shop semantics and remaining limits.

Game metadata was inspected with REA against assembly SHA256 `635B0D164A7050942077F3A7509AD2E4DC49A4D860E476EBE6906C7508DFA367`, selected-member evidence `ev_b10d5c6bd4bccaf4a58ac1a43e86eade665f836ee95783aa2bfcfcd4c0baa778`. Existing private decompilation supplied HappyBaldi, Baldi chase/navigation and shop callback details. Game code and private decompilation are not included in releases.

Watch Skill focused report/artifact contract v7 passes with isolated-local receipt `vr_a3fc77924905`. This validates the recorded reports and build presence, not additional gameplay. The installed BepInEx plugin matches its artifact; the original game assembly hash remains unchanged. Test-owned game processes are closed.
