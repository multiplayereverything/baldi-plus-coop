# Supplied assets

## Connection indicator

`connection-indicator.png` is the user's `b80f3c98-96ec-4b70-b686-1a3e108e061f-removebg-preview.png`, copied unchanged. SHA256 `31E823FE0DBEF2086794CE20BD81F0A735B3D740295FF2DD45B67AF3B8CC956B`. Its five signal silhouettes are cropped at runtime and tinted for measured/fresh/stale connection status. Both loaders embed it. The code's MIT license does not assign rights to supplied artwork.

## Multiplayer Hide & Seek audio

`BAL_HideSeek.wav` is the user's replacement from `Game Sound Preview/NewAudio/BAL_HideSeek.wav`, copied unchanged. SHA256: `C3A443F5A67778FEEAA79C477C4706416EAA1F43C7074E2F144E9EC44AE27DDF`. It is embedded in both loader DLLs and used only while multiplayer is connected. The code's MIT license does not assign rights to this supplied recording.

`player-sheet.png` is the user's supplied `Gemini_Generated_Image_pcqijrpcqijrpcqi (1)-Photoroom.png`, copied without changing its bytes. Original dimensions: 720x1475, RGBA PNG.

SHA256: `F573A3CC87E0EA6A7DA304C4B4DF7B02D228F592CEFF4384267DDFBDC7B71A56`.

The game loads it from an embedded resource in each loader DLL. Runtime sprite rectangles exclude the direction captions at the left. Frame groups are front 0/1/2/3, back 4/5/6/7, left 8/9/12/13, and right 10/11/14/15. Idle uses each group's first frame; traveled distance advances the walking cycle. Front/back/left/right are chosen relative to the viewer and the remote player's facing.

Artwork was provided by the user for this mod. Its original filename identifies it as Gemini-generated and processed with Photoroom. No additional artwork was generated or downloaded. The source code's MIT license does not assign rights to this supplied image.

## Multiplayer button

`multiplayer-button.png` is the user's purple lettering from `Gemini_Generated_Image_tkc2xftkc2xftkc2-removebg-preview.png`, copied unchanged. It is a 489x510 RGBA PNG with transparent background, SHA256 `078C543F6796B92859BA49FBE120F899960C21C8313240BF1D9C8645AAB3A2A5`. Both loader DLLs embed it. Runtime sprite rectangle (9,183,473,127) excludes transparent margins without modifying the source image. This is the artwork used in the title menu from 0.4.8.

The earlier cyan `multiplayer-lettering-v1.png` was generated with the built-in image tool as a preview, with its prompt stored beside it. That preview is not used or packaged as menu artwork. The supplied purple image was not regenerated or recolored.

## Team sounds (0.6.1-0.6.2)

`sfx/ping.wav`, `sfx/join.wav`, `sfx/leave.wav`, `sfx/caught.wav`, `sfx/switch.wav` and `sfx/found.wav` were generated locally with **Stable Audio 3 small-sfx** (`stabilityai/stable-audio-3-small-sfx`) and chosen by the user from round-two candidates. Powered by Stability AI. The model is used under the Stability AI Community License (https://stability.ai/license); check its terms before any commercial use.

| File | Picked take | Prompt (each prefixed "TrackType: SFX.") | SHA256 |
| --- | --- | --- | --- |
| `ping.wav` | seed 5 | small metal desk bell, one clean ding | `19BB756426094496EE296572341E5BBCC02EED28EC9FDB778519B2BF572BAA64` |
| `join.wav` | seed 53 | two rising toy-xylophone notes | `3474CFB3AE1F857DEC2CD2B06E2932CF717AAA85964CCEFD215554050C6B1584` |
| `leave.wav` | seed 17 | two falling toy-xylophone notes | `C6F59DBA7F43C5FF052A95885CFCDEB910928E0678CD388B688E1D3C5539E309` |
| `caught.wav` | seed 5 | wooden ruler slapping a school desk | `F9EA4D900433A4E3177F039C8DA1E595B414188B52E73179BA5C06ACED83D735` |
| `switch.wav` | seed 17 | quick paper whoosh | `D54D7E579C25D74E961E5B1814BD94455A31A312836A406A597776E37C99B9DF` |
| `found.wav` | seed 5 | old school computer beep | `F180C6526BEEC018554C333E347677D5EF5C93AAC71FA76AD6D95F4DDA332461` |

All takes used 8 steps, CFG 1.0 and a 2-second generation length. Post-processing: mono mix, trimmed to the audible span (at most 0.6-1.2 s), short fades, and levelled to about -18 dB RMS with peaks no higher than -6 dBFS to match the game's own short sounds. Saved as 44.1 kHz PCM16 and embedded in both loader DLLs.

## Hall Monitor NPC (0.8.0)

- **Artwork:**
  - **Files:** `npc/hall-monitor-patrol.png`, `npc/hall-monitor-writing.png` and `npc/hall-monitor-whistle.png` (256x512 RGBA), and `npc/hall-monitor-poster.png` (256x256). From 0.8.3 the poster is a base image in the native character-poster layout, measured from the game's posters, with no game pixels. The game writes his name and description on it at runtime in its own font.
  - **How they were made:** drawn procedurally by `scripts/draw-hall-monitor.py` using Pillow and NumPy. Every part is a lit sphere or tube, which imitates the game's shaded pseudo-3D characters. No game artwork, reference image or image model was used. The poster text is set in Arial from the local Windows fonts.
- **Sounds,** generated locally and embedded in both loader DLLs:

| File | Source | Prompt or text |
| --- | --- | --- |
| `sfx/hm-whistle.wav` | Stable Audio 3 small-sfx, seed 53, 8 steps, CFG 1.0. Powered by Stability AI (Stability AI Community License). | "TrackType: SFX. One loud sharp blast on a plastic referee whistle, a single shrill pea whistle tone held for under a second, recorded in a school hallway with a short echo, no music, no voices." |
| `sfx/hm-scribble.wav` | Stable Audio 3 small-sfx, seed 17, same settings | "TrackType: SFX. A pencil quickly scribbling a short note on paper on a clipboard, fast scratchy writing strokes, recorded close, dry, no other sounds." |
| `sfx/hm-norunning.wav` | Windows speech synthesis (Microsoft David), resampled x1.3 so it sounds younger | "No running in the halls!" |
| `sfx/hm-together.wav` | Same | "Hey! You two! I'm telling!" |

- **Choosing takes:** the user picked whistle seed 53 and scribble seed 17 from five takes each. The other candidates are in `private/sfx-gen/final-hm`.
- **Processing:** the same as the team sounds. Mono, trimmed, short fades, and about -18 dB RMS with peaks no higher than -6 dBFS. The voices were levelled to about -16 dB RMS.
