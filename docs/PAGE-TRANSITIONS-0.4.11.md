# Multiplayer page transitions — 0.4.11

Host Game, Join Game, Player Options, Lobby Options, internal Back, Host/Join confirmation and Leave/Cancel use the actual GlobalCam Dither transition. Each action captures its outgoing Multiplayer camera frame, performs the existing action and renders the resulting page into the native destination target. The camera target and pointer are restored after completion. Navigation is blocked while dissolving. Text fields edit immediately; Coming Soon labels remain static.

The focused lab exercises eleven navigation actions, including hosting a real local room and leaving it. It checks native transition routing, destination pixels, changed destination/blend pixels and camera restoration for each action, plus preserved disconnect/validation/text-edit behavior. It invokes component handlers, not physical mouse input. The Join Game form is covered; a remote client's connection handshake is outside this UI test. No transport changes or unrelated gameplay tests.

The visual direction remains the native white menu, black game font and native dither timing. Anti-slop during-development delivery gate: ENERGY 1, RHYTHM 1, MOTION 1. No new art or ornamental motion.
