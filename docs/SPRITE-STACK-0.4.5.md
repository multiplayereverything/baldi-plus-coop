# Player/NPC camera ordering, 0.4.5

The continued gameplay overlap exposed a gap in 0.4.3's test: that fixture rendered both sprites through one camera. The game uses separate world and billboard cameras. Its native NPC sprite renderers occupy layer 9; remote player visuals were left on layer 0. The later billboard pass could therefore paint a farther NPC over a nearer player, even with matching materials.

Evidence from the game's loaded GameCamera prefab:

| Camera | Culling mask | Clear behavior |
| --- | --- | --- |
| MainCamera | 405332247, includes layer 0 and excludes layer 9 | Skybox; URP clearDepth true |
| BillboardCamera | 512, layer 9 only | Nothing; URP clearDepth false |

PlayerSpriteSheet now records the selected native NPC renderer's layer alongside its cloned material. RemotePlayerSprite assigns that layer to its visual child. The layer is copied from the game, rather than hardcoded. No camera masks or NPC renderers are modified.

The isolated perspective fixture creates the actual RemotePlayerSprite component and a Baldi sprite, then renders world and billboard passes using the loaded native camera masks and billboard clear behavior. It compares each combined image with separately rendered front sprites. Test-only materials use a color multiplier of 20 for the unlit title-scene environment; production keeps native material values.

Five checks pass:

| Check | Evidence |
| --- | --- |
| Native camera routing | Player and NPC share the billboard layer; the world camera excludes it |
| Reproduce old layering defect | Near player matches 0 of 413 overlapping pixels with layer 0 |
| Fixed player in front | 413 of 413 overlapping pixels match the player |
| Fixed NPC in front | 735 of 735 overlapping pixels match the NPC |
| Transparent player areas | 91 of 91 tested holes reveal the NPC behind |

Run only in isolated lab mode with `-bt-lab -bt-sprite-stack`. Results are written to artifacts/tests/sprite-stack-results.json. Before/after captures were visually inspected. This tests camera-pass composition with native masks and artwork, not a full two-player gameplay session. Both loader builds share the fix; unrelated network/gameplay tests were not rerun.
