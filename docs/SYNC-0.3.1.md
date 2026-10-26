# 0.3.1 focused loading and world synchronization

Normal rooms choose a cryptographically generated, nonnegative random seed when the host initializes a new run. Reinitializing a run chooses again; moving floors and restarting keep the current run seed. The legacy seed setting no longer controls normal rooms. Developer lab runs remain reproducible unless the focused flow mode enables normal seed selection.

The host starts after its own world registry is ready. It broadcasts the start independently of client acknowledgements. A client retains that signal while generating, reconciles its item catalog and layout with the host, then enters automatically. Multiplayer removes the loaded elevator screen and its pending coroutines when play begins. Local generation is still required; a slow client can miss early countdown beats and resumes at the current host beat. Invalid worlds are still rejected.

Client NPC snapshots supply positions and yaw; a separate render-frame update interpolates toward each sample over the observed packet interval, bounded to 50–200 ms. A displacement above 20 world units snaps. No prediction or overshoot is used. Host AI, slap cadence and catch decisions retain native authority. This adds a small visual interpolation delay and does not replicate missing NPC abilities.

World snapshots additionally carry host window open/broken state and swing-door open/lock state. Client replay calls native methods so textures, map tiles and collision update together. Swing-door replica triggers are suppressed; host checks fresh remote poses within eight units of the door center to open unlocked doors. Collocated components use hierarchy-derived IDs and participate in layout validation. This does not implement client item consumption/effects for breaking windows or applying locks.

## Focused verification

Run `scripts/launch-lab.ps1 -FlowSync`, then `python tests/verify-flow.py`. This uses one BepInEx host and one MelonLoader client, with an eight-second client validation delay. Lab commands invoke game methods, rather than physical input. The test checks shared random seed, host entry before client acknowledgement, client automatic entry without an elevator, multiple render movement steps per NPC packet, window breaks, swing-door locking and reopening, and native host restart through the pitstop with automatic client entry and the same seed.

The required 22 transport/claim checks run after protocol changes. The broad sprite suite and old shop transaction suite are not required for these changes. General multi-floor progression, Internet play, field trips, other NPC abilities and client item effects remain unverified or incomplete as described in README.

Final result: eight focused live checks and all 22 transport/claim checks pass. The final live run used seed `139266`, matching world hashes on both initial load and native restart into the pitstop. The Melon client entered after its deliberate eight-second validation delay while the host was already playing. Window breaks and swing-door lock/reopen replay passed, and client NPC movement advanced more than 1.5 times per received NPC sample. No game-code exceptions appeared in either final player log; the Melon log contains an environment cursor-confinement warning. The last build also changes the loading-message text; this copy-only change did not require another live run.
