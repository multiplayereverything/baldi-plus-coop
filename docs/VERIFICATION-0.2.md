# 0.2.0 verification, 2026-10-18

Both loader builds compile against the installed game and their respective loader/Harmony references. The final binaries are installed in the working BepInEx copy and the private MelonLoader lab. The original game assembly is unchanged.

## Automated checks

Twenty-two transport/protocol/supply-claim checks pass. The additional checks cover delivering a rejection reason before closing, graceful close, simultaneous item claims, one pending grant per player, acknowledgement ownership, duplicate acknowledgement, committed item ownership and returning a declined grant.

Fifteen live checks pass using a BepInEx host and MelonLoader client in two actual Unity game processes. See `artifacts/tests/live-results.json` for observed state samples.

| Live check | Result |
| --- | --- |
| Cross-loader connection | PASS |
| Matching generated level | PASS |
| Player poses in both directions | PASS |
| Shared notebook | PASS |
| Shared standard door | PASS |
| Shared activity result | PASS |
| Host NPC transforms applied by client | PASS |
| Matching transition into Pitstop | PASS |
| One shared item delivered and consumed in both worlds | PASS |
| Client ping received in both processes | PASS |
| Round-trip latency samples | PASS |
| Host loading barrier with deliberately delayed client | PASS |
| Duplicate item collection refused | PASS |
| Full-inventory pickup refused | PASS |
| Host item catalog repairs a deliberately altered client layout | PASS |

The final run delivered a Quarter at `Pickup:37,5,185:`. Its recipient's inventory increased once; retrying the same pickup did not add another. A full test inventory refused another item and the previous test inventory was then restored. The client was delayed three seconds before requesting the host item catalog; the host reported its loading barrier during that delay. The test also removed one local item before reconciliation. The client applied the host's thirteen-item catalog, matched its digest and then became ready. Both processes received the same team location ping. The activity test still uses NoActivity rather than solving every puzzle type. Test runs can explicitly invoke the scene-transition method to avoid waiting indefinitely for a random NPC encounter.

The floor-layout hash remains `7CCC9F75545503486DB86E5FC0318970682EAB790CD5D20C2DB46880F4A4D2A0`. The tested Pitstop hash is `6BB33A04240B7711CBAA476A647A80006B45AAC38E25208D98742EF2DAE5CECF`. A separate supply-catalog digest verifies the host's item IDs and assets; the final initial catalog matched at `A9FCC546CD5A1D0290635B212FD21DD416000A45817DE0E038734839771AFA00`. Catalogs may vary between runs because the game has unseeded item placement.

Four malformed/incompatible connection cases are exercised by `tests/reject-live.py`: packet before handshake, wrong protocol, incorrect room code and late join. Each receives a readable reason followed by a closed connection. The accepted game client remains connected.

Earlier Watch Skill receipts are retained in `docs/watch-0.2.json`. The final report is `docs/watch-0.2-final.json`, using `tests/watch-contract-v4.json` and the isolated-local verifier. It verifies the named report fields and artifacts; it is not an independent judge of every gameplay mechanic. The game HUD screenshot `docs/coop-0.2.png` was inspected: it shows the received Quarter, the G-key hint and measured latency without covering notebook counts.

## Review and limits

Anti Slop review: Hard Gate PASS for the documented experimental scope and reviewed handlers; Purpose Gate PASS for game-context UI; Liveliness PASS at ENERGY 1 / RHYTHM 1 / MOTION 1; Craftsmanship PASS for the tested states. Existing measured cream/dark/green text pairings are retained. Orange marks a temporary team location. There is no decorative motion. The lobby now reports round-trip timing and the game HUD documents G. Native UI input remains unverified because the Windows helper was unavailable; tests invoke game methods.

Only two local processes over loopback were tested. Broad separate-computer LAN testing, four-player gameplay, all puzzle types, full runs, shop purchases, item effects, inventory transfers and NPC-specific abilities remain outside this result. Shared supplies are initial free, non-repeatable items that enter an inventory. They require an empty slot and do not implement swaps or dynamic drops. An unacknowledged delivery remains consumed when the recipient disappears; mid-run reconnect is unavailable.
