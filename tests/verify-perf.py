"""Check a two-player soak run (scripts/launch-lab.ps1 -Soak) for performance and playability.

Waits for both perf files to cover floor 2, then checks:
- the mod's own per-frame cost stays under 1 ms on host and client during play;
- frame time p95 stays under 16.7 ms (60 FPS) during play;
- managed memory does not keep growing within a floor;
- both sides reached floor 2 (MainLevel_2) and both logs are free of exceptions.
Writes artifacts/tests/perf-verify.json and exits 1 on any failure."""
import json
import re
import sys
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
lab = root / "private/melon-lab"
paths = {"host": root / "artifacts/tests/perf-host.json", "client": lab / "MultiplayerMod/artifacts/tests/perf-client.json"}
logs = {"host": root / "private/host-player.log", "client": lab / "MultiplayerMod/private/client-player.log"}
launched = (root / "private/host-pid.txt").stat().st_mtime if (root / "private/host-pid.txt").exists() else 0
timeout = float(sys.argv[1]) if len(sys.argv) > 1 else 600


def load(path):
    try:
        if path.stat().st_mtime < launched:
            return None
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def floor2_windows(data):
    return sum(1 for w in (data or {}).get("windows", []) if w.get("scene") == "MainLevel_2")


deadline = time.monotonic() + timeout
data = {}
while time.monotonic() < deadline:
    data = {role: load(path) for role, path in paths.items()}
    if all(floor2_windows(d) >= 4 for d in data.values()):
        break
    time.sleep(5)

checks = {}
for role, d in data.items():
    windows = (d or {}).get("windows", [])
    play = [w for w in windows if w.get("worldReady") and w.get("npcs", 0) > 0]
    checks[f"{role}: soak reached floor 2"] = floor2_windows(d) >= 4
    checks[f"{role}: play windows measured"] = len(play) >= 8
    worst_mod = max((w["modMsPerFrame"] for w in play), default=99)
    checks[f"{role}: mod cost under 1 ms per frame (worst window {worst_mod} ms)"] = worst_mod < 1.0
    worst_p95 = max((w["frameMsP95"] for w in play), default=99)
    checks[f"{role}: frame p95 under 16.7 ms (worst {worst_p95} ms)"] = worst_p95 < 16.7
    floor1 = [w["monoHeapMB"] for w in windows if w.get("scene") == "MainLevel_1" and w.get("npcs", 0) > 0]
    growth = (sum(floor1[-3:]) / 3 - sum(floor1[:3]) / 3) if len(floor1) >= 6 else 0
    checks[f"{role}: managed heap steady on floor 1 (drift {growth:+.1f} MB)"] = len(floor1) >= 6 and growth < 15
    text = logs[role].read_text(encoding="utf-8", errors="replace") if logs[role].exists() else ""
    checks[f"{role}: no exceptions in log"] = bool(text) and not re.search(r"Exception", text)

passed = sum(1 for v in checks.values() if v)
(root / "artifacts/tests/perf-verify.json").write_text(json.dumps({"passed": passed, "failed": len(checks) - passed, "checks": checks}, indent=1), encoding="utf-8")
for name, ok in checks.items():
    print(("PASS " if ok else "FAIL ") + name)
print(f"{passed}/{len(checks)} passed")
sys.exit(0 if passed == len(checks) else 1)
