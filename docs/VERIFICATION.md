# Verification, 2026-10-18

## Build and transport

Both loader assemblies compile using the Windows .NET Framework compiler. Fourteen checks in `tests/TransportTests.cs` pass: distinct peer IDs, maximum frame transfer, reply routing, independent disconnects, fragmented header/body reassembly, oversize and empty-frame rejection, healthy-peer survival, host shutdown, UTF-8 text, invalid text lengths, NaN rejection, four-player capacity, and rapid reconnection with reused peer IDs.

The final BepInEx DLL also passed a no-lab startup smoke check: plugin loaded, Harmony startup completed, and a room opened. Its installed SHA-256 matches the packaged build: `6E626D7AB89AD3CF769F1FA13C63A8788F901BE07F2EB1D1034EA460F311B22E`. MelonLoader build SHA-256: `B9E7FF748A3D098C1CF8E62B1ED2581CBD0032B0653F8102157E19275EC164AF`. Original Assembly-CSharp.dll SHA-256 remains unchanged. Both mod-only package folders passed Universal Modder's lint with three files, zero failures and zero warnings each. All test-owned game processes were stopped after verification.

Watch Skill ran the build/report contract in a separate local verifier process. The first 14-check receipt is `vr_49765058530e`; the final receipt is saved in `docs/watch-final.json`. This proves the specified artifact/report checks, not comprehensive game behavior. Watch Skill's final doctor report is `docs/watch-doctor.json` (`ok: true`), with optional capabilities reported separately.

## Two real game processes

`artifacts/tests/live-results.json` records eight observed checks from the BepInEx host and MelonLoader client:

| Check | Result | Evidence |
| --- | --- | --- |
| Cross-loader connection | PASS | Both rosters reported two accepted players |
| Same generated level | PASS | Both reported ready and the same epoch/layout hash |
| Player replication | PASS | Both reported a remote pose |
| Notebook | PASS | Client invoked Notebook.Clicked, host accepted it, both counts became one |
| Standard door | PASS | Host accepted the client's click and client observed an opening |
| Activity completion | PASS | Host accepted a NoActivity completion result and both completed counts became two |
| NPC replication | PASS | Client applied host NPC frames |
| Shared scene transition | PASS | Both loaded Pitstop at epoch two with the same layout hash |

Initial floor hash: `7CCC9F75545503486DB86E5FC0318970682EAB790CD5D20C2DB46880F4A4D2A0`. Pitstop hash: `6BB33A04240B7711CBAA476A647A80006B45AAC38E25208D98742EF2DAE5CECF`.

Two additional live checks rejected a packet before handshake and an incorrect protocol version. The valid client stayed connected. Logs and game screenshots are retained under `private/`; the test lab redirects file saves to workspace-local profiles. Both loader startup logs identify the mod, and MelonLoader identifies game version 0.14.5.

The automated interaction test teleports the local test player near the target and calls the game's public interaction methods. It validates request routing and replicated state. It does not exercise physical mouse picking, solve a MathMachine puzzle, or validate every activity type. A shared death led to a synchronized restart/pitstop transition; complete multi-floor runs remain untested.

## Visual and Anti Slop review

The host screenshot was inspected: the named blue remote capsule appeared in the game and the notebook counter showed 1/4. The lobby screenshot was inspected with its roster and connection instructions. The final small HUD adjustment moves room status to the top center so it does not cover notebook counts, and uses opaque dark fields with white text for names/status.

- Hard Gate PASS within the experimental build's stated scope: real state/error copy, bounded scrolling panel, functioning handlers for Host/Join/Disconnect/Close, no fabricated feature claims. UI handlers were inspected because native Windows input was unavailable. Native mouse/keyboard click-through is still a release limitation.
- Purpose Gate PASS: cream paper, green actions and existing font serve the game's schoolroom context; status/roster content determines layout. See DESIGN.md.
- Liveliness PASS: ENERGY 1 / RHYTHM 1 / MOTION 1, one primary connection action, grouped fields and a schoolroom palette; no decorative animation.
- Craftsmanship PASS for reviewed code and captured states: relevant controls, explicit empty/connecting/error messages, field/button focus styles, no generic filler sections. Broad UI and gameplay coverage remains open in README progress.

Measured text contrast using Anti Slop's checker: white/green 6.63:1, dark text/cream 18.11:1, white/dark 16.99:1, yellow/dark focus 15.82:1. Narrow-window scaling, Tab/Shift+Tab/Enter/Space/F5/Escape handlers are implemented; native input verification is not claimed.

## Practical limits

The tested topology is two local processes over loopback. Three/four-player in-game behavior and real separate-computer LAN performance are untested. The transport capacity was checked independently. Remaining synchronization gaps are listed in README. No release was uploaded or published to another service.
