# Player sprite depth, 0.4.3

Follow-up: this single-camera fixture did not cover the game's separate billboard camera. Gameplay overlap remained because player visuals were on the world layer. Version 0.4.5 adds the rendering-layer correction and camera-pass evidence described in [SPRITE-STACK-0.4.5.md](SPRITE-STACK-0.4.5.md).

Remote players previously used `Sprites/Default`; native Baldi, Playtime and Principal renderers use `SpriteStandard_NoBatching`. Mixing those materials produced incorrect overlap. The player now clones the loaded native NPC material, keeping its shader, render settings, billboard behavior and lighting textures. The user-supplied sheet is unchanged.

An isolated Unity camera rendered the embedded player sprite and an actual Baldi sprite at reversed depths. The title scene has no level lighting, so test-only material copies use a color multiplier of 20 to make overlapping pixels distinguishable. Production materials retain the native multiplier of 1.

Four focused checks passed:

- Reproduce the legacy defect: only 3,841 of 4,158 distinguishable overlap pixels match the front player with the old shader.
- NPC in front: 3,090 of 3,090 overlap pixels match the NPC with the native material.
- Player in front: 3,090 of 3,090 overlap pixels match the player with the native material.
- Transparency: all 837 tested transparent player pixels reveal the NPC behind.

The fixture runs only with `-bt-lab -bt-sprite-depth`; results are written to `artifacts/tests/depth-results.json`. This verifies GPU overlap with native artwork/materials, rather than a full multiplayer gameplay session. Both loader builds compile from the same sprite code. Network behavior is unchanged.
