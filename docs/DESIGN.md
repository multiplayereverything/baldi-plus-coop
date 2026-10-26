# Lobby direction

Reading this as: a LAN lobby inside Baldi's Basics Plus, inheriting the game's schoolroom theme. ENERGY 1 / RHYTHM 1 / MOTION 1.

Cream paper, black text and green actions recall the game's classrooms and chalkboards. Use the existing Unity skin font so no external font or game asset is distributed. The connection action is the focal point. Form rows follow the order needed to join; the roster replaces the form while connected. No decorative animation is needed during game setup. Focused fields use green and focused buttons use black with yellow text. Empty, connecting and error states use explicit status text.

Player art is the user's supplied transparent green character sheet. Keep its original pixels and select sixteen runtime rectangles, excluding the sheet's left captions. Upright sprites face the camera as the game's characters do, selecting front/back/left/right from the remote player's facing relative to the viewer. Walking follows traveled distance; stationary players use an idle pose. Shared artwork keeps the provided colors; names distinguish teammates.

Player names are secondary information: small pale text with a one-pixel black shadow, no box, and a short viewing/distance range. Only looking toward a nearby teammate reveals a label; it fades at the edge and is hidden behind world geometry. The room roster retains full names for deliberate identification.

The user's menu screenshots establish the placement: MULTIPLAYER sits immediately above the Basically Games copyright. Its native TextMeshPro label uses an existing game font and the game's cursor/underline behavior. Green uppercase lettering follows the existing PLAY/OPTIONS menu voice. The title screen has no offline HUD banner.

The lobby is a centered cream sheet with a dark chalkboard heading. Dimming the game behind it makes the active input surface clear. Host/Join tabs reflect the two actual workflows; each has three field rows and one primary connection action. The connected roster replaces the form. Fields use white interiors and dark outlines; focused outlines use green. No decorative motion, gradients, pills or external art are needed.

The fixed logical sheet scales to fit desktop windows, including 800x600, without scrolling or clipped controls. It supports Tab, Shift+Tab, Enter, Space, F5 and Escape. The OS pointer is visible inside the panel while the game's cursor is hidden and blocked; its prior state is restored on close. This mod targets the Windows game, not phones.
