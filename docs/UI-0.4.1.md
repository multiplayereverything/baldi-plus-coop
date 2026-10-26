# Multiplayer menu, 0.4.1

Direction: the user asked for the Multiplayer menu to look like Baldi's own menus. The implementation uses a white screen, the existing game font and cursor artwork, native StandardMenuButton underline behavior and native highlight/confirm sounds. Host Game, Join Game and Back form the first screen. Connection fields and the player list use the same plain text treatment. No new artwork, logos or web-style panels were created.

Previews: [menu](multiplayer-menu-0.4.1.png), [host](multiplayer-host-0.4.1.png), [join](multiplayer-join-0.4.1.png), [800x600](multiplayer-small-0.4.1.png).

Click a value to edit it. Tab changes fields; Enter finishes editing. Outside editing, Up/Down or Tab/Shift+Tab selects a choice and Enter activates it. Esc finishes editing or goes back, and F5 closes the menu. Opening the menu continues to block gameplay movement, clicking and item use. The old top gameplay banner is removed.

## Verification scope

Both loader builds compile. An isolated BepInEx game runs the `-bt-lab -bt-diagnostics -bt-ui-tour` component-handler tour. Its 11 checks cover the initial menu/font, Host fields, text editing with Backspace, numeric port filtering, password masking, Join fields, invalid-port validation, a real open host room, Back preserving that room, Leave closing it, and small-layout bounds.

All menu and field buttons are exercised through their actual StandardMenuButton callbacks. This is a recorded component-handler tour; physical mouse and controller input are not automated. Keyboard input wiring is source reviewed and the same text-editing method is called by the tour. The screenshots render the actual menu camera and TMP objects after the frame, rather than a mockup. Because this game can keep a stale 640x360 render target and produce black backbuffer captures, evidence captures render that camera explicitly. The 800x600 preview uses the same menu at an explicit render-target size and the corresponding CanvasScaler expansion factor; it does not claim the hidden application window changed resolution.

The source changes are UI only. Transport, NPC, shop and sprite regression suites were not repeated for this change. Protocol remains 7; the strict mod-version handshake still requires everyone to use 0.4.1.

## Antislop delivery gate

During-development mode follows the user's earlier choice. The design read before implementation was the requested Baldi menu identity: native pixel lettering, plain white background, text choices and underline highlights. Dials: ENERGY 2, RHYTHM 2, MOTION 1. The cursor and occasional green editing value supply the accent; type and spacing establish the hierarchy.

| Check | Status and evidence |
| --- | --- |
| R-02 | PASS: visible menu copy contains no em dashes. |
| R-03 | PASS: small-preview text rectangles are checked against the target bounds and inspected visually. |
| R-17 / R-18 | PASS: player rows come from the live roster; there are no fabricated statistics or testimonials. |
| R-23 | PASS: the requested menu reuses existing game assets; no artwork is generated or bundled. |
| R-24 / R-26 | PASS: Host, Join, Back, Leave and field controls have real callbacks exercised by the tour. |
| R-25 | PASS: black text on white is 21:1; the editing accent #006600 is above 7:1. |
| R-27 | PASS: connecting, optional-empty, validation-error and connected-room states are defined. |
| R-28 | PASS: no unrelated FAQ or filler sections. |
| R-32 | PASS: keyboard focus has the native underline/caret; Tab, arrows, Enter and Esc are wired. Text editing and field callbacks are exercised. |
| R-33 | PASS: source was edited with apply_patch. |
| R-34 | PASS: a single game-matching white theme, with no theme toggle to break. |
| R-35 | PASS: both builds run through compilation, the game records a complete component-handler tour, and menu renders are inspected. This does not assert physical input automation. |
| R-36 / R-38 | PASS: copy describes real local connection behavior and the live roster. |
| R-37 | PASS: the user supplied the Baldi menu direction before implementation. |
| Purpose gate | PASS: white background, game font, cursor and underline each serve the requested game identity; no ornamental cards or animations. |
| Liveliness | PASS: explicit dials, a clear heading, deliberate spacing, familiar hand cursor, green edit accent and repeated native type. |
| C-1 through C-5 | PASS: purposeful game styling, live callbacks, content-driven screens, scaled layout, keyboard wiring and recorded evidence. |
| R-05 / R-11 / R-15 / R-16 | PASS: game text menus with specific Host/Join/Back actions; no generic website template, pill controls or marketing copy. |
| R-20 / R-21 / R-29 / R-30 / R-31 | PASS: game-specific assets, requested light palette and one documented reason for each major visual choice. |
