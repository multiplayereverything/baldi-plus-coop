# Baldi capture regression — 0.5.1

Native Baldi.CaughtPlayer changes into Baldi_Attack before calling the manager/core game-over path. The old spectator guard intercepted only CoreGameManager.EndGame. It prevented game over but left Baldi in a terminal attack state, which does not run the chase/slap behavior. BaseGameManager.EndGame also stops its coroutines after forwarding to the core.

Multiplayer now intercepts CaughtPlayer before either side effect and performs individual elimination there. A separate manager entry guard protects callers that bypass CaughtPlayer. Single-player remains native; the host's team-wipe path still calls native CoreGameManager.EndGame under Applying.

The regression fixture now invokes actual Baldi.CaughtPlayer, rather than only the core game-over method. It checks chase state, retargeting to a living teammate and actual accumulated movement over five seconds, plus the manager entry, existing spectator constraints, balanced camera locks, round reset and native team wipe. This is a native-component fixture with a synthetic remote teammate, not a full two-machine natural encounter. No transport changes or unrelated suites are needed.
