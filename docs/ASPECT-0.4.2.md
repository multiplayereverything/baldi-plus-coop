# Native menu frame in 0.4.2

The user identified the missing black side borders in the Multiplayer menu. The existing game code establishes a 480x360 safe area: `GlobalCam.UpdateResolution` uses 360 vertical units for wider screens and 480 horizontal units for narrower screens; `GlobalCam.UiToRealScreenPosition` centers the corresponding area. The inspected main-menu hierarchy places its four Border rectangles outside x = +/-240 and y = +/-180. These are visual borders rather than a physical display-resolution change.

The previous Multiplayer canvas used a 640x360 reference and stretched its white background over the entire window. It now uses the native 480x360 reference with Expand scaling, a fixed white 480x360 menu area and a black surrounding background. The pointer remains within the native area. The same camera and GraphicRaycaster map menu clicks, while border clicks hit the background rather than a menu choice. No new game artwork is created or bundled.

## Focused checks

The isolated `-bt-lab -bt-diagnostics -bt-aspect-tour` run renders the actual menu camera at 1920x1080, 800x600, 2560x1080 and 600x800. Each size checks the white-frame geometry, rendered black/white pixels, a menu-button raycast and rejection of input in the outer margin. All eight combined frame/input checks pass. Widescreen and 4:3 captures were visually inspected. These are controlled render-target and raycast checks, not physical mouse or window-resize automation.

At 1920x1080 the white area is 1440x1080 with 240-pixel black bars on each side. At 800x600 the 4:3 menu fills the image. Ultrawide gets wider side bars; portrait gets top/bottom bars. Existing gameplay/network tests are not repeated for this layout-only change. Both loader builds compile.

Preview: [widescreen](multiplayer-menu-0.4.2.png), [4:3](multiplayer-classic-0.4.2.png).

## Design gate

- Direction PASS: match the native frame shown in the user's screenshot and confirmed in game code.
- Layout/contrast PASS: eight geometry, pixel and raycast checks; black borders and white menu retain the existing text contrast.
- Interaction PASS: button-center rays resolve to the intended native button and outer-margin rays resolve to no button at each size.
- Assets/copy PASS: existing game UI assets and functional menu copy; no generated artwork or invented claims.
- Craft PASS: both loaders build; rendered wide and 4:3 layouts are inspected. The full 0.4.1 design gate remains in UI-0.4.1.md; this change only adjusts framing.
