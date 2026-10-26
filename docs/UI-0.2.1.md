# UI verification, 0.2.1

The user's screenshots place Multiplayer above the Basically Games copyright. The native TextMeshPro label is a child of the copyright label, aligned above its publisher name. It inherits the canvas scaling, uses the installed game's font, and registers a StandardMenuButton with the game's cursor. No game font or artwork is included in the package.

The offline lobby starts closed and the old offline top banner is gone. F5 remains available. The cream modal is centered over a dimmed game view. Host and Join show different second fields (seed or address), share port/code fields, and have one primary connection action. The connected roster replaces the form. Closing restores the prior cursor lock/visibility and menu cursor hidden/click state. The menu's initialized CoreGameManager no longer causes a false in-run refusal.

Actual Unity screenshots were inspected for the title menu, Host, Join, invalid port, open room and 800x600 layout. Desktop screenshots are 1920x1080; the saved game settings override the requested startup resolution. The small-window screenshot is an actual Screen.SetResolution(800,600,false) result. No controls escape the sheet and no scroll bar is needed. The UI lab invokes the native menu button's Press method, switches tabs, checks invalid-port feedback, hosts a room on test port 27019, leaves it and restores the menu. These are method-driven checks, not physical mouse/keyboard automation.

Evidence:

- `menu-0.2.1.png`: native button above Basically Games, no offline banner.
- `lobby-0.2.1.png`: centered Host form, visible focus and complete footer.
- `join-0.2.1.png`: address replaces the seed, matching Join action.
- `error-0.2.1.png`: readable invalid-port message.
- `room-0.2.1.png`: accepted player, port, seed, instructions, close/leave actions.
- `lobby-small-0.2.1.png`: complete 800x600 layout.

Both loader builds compile. The fresh BepInEx host / MelonLoader client run passes all 15 live checks, including matching levels, shared items and the next scene. All 22 transport/claim checks pass. Four malformed/late join probes report a readable reason while the accepted client stays connected. Protocol remains version 4; mod version is 0.2.1.

Contrast follows the existing schoolroom palette: white on green 6.63:1, dark text on cream 18.11:1, white on dark 16.99:1. Native green lettering is on the white menu background. Physical mouse hit testing, keyboard traversal and a separate-PC LAN session still require hands-on play testing. The Watch Skill contract checks reports and screenshot existence; visual layout review is the agent's inspection of the actual screenshots.
