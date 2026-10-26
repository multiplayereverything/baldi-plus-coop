"""Verify the two-process encounter lab started by scripts/launch-lab.ps1 -Encounter.

  python tests/verify-encounter.py [timeout-seconds]

A BepInEx host (game root) and a MelonLoader client (private/melon-lab) run src/EncounterLab.cs. Each writes its own
observations: artifacts/tests/encounter-host.json and private/melon-lab/MultiplayerMod/artifacts/tests/encounter-client.json.
This waits for both to finish (default 360 s), checks every expected observation, scans all four logs for exceptions,
prints PASS/FAIL per check, writes artifacts/tests/encounter-verify.json and exits 1 on any failure."""
import json
import re
import sys
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
game = root.parent
tests = root / "artifacts/tests"
lab = root / "private/melon-lab"
host_path = tests / "encounter-host.json"
client_path = lab / "MultiplayerMod/artifacts/tests/encounter-client.json"
timeout = float(sys.argv[1]) if len(sys.argv) > 1 else 360

# Must match EncHostChecks / EncClientChecks in src/EncounterLab.cs; a missing name counts as a failure.
HOST_CHECKS = [
    "host-setup-ready", "host-client-at-arena",
    "host-beans-targets-client", "host-beans-gum-hits-client",
    "host-crafters-gaze-accepted",
    "host-pomp-assigns-client", "host-pomp-arrival-rewarded",
    "host-reflex-test-for-client", "host-reflex-click-accepted", "host-reflex-pass-pays-wallet",
    "host-chalkles-client-in-board-room", "host-chalkles-client-charges",
    "host-thetest-client-gaze-reported", "host-thetest-remote-gaze-freezes",
    "host-item-bsoda-relayed", "host-item-alarm-clock-relayed", "host-item-whistle-relayed",
    "host-client-eliminated-host-alive", "host-no-exceptions",
]
CLIENT_CHECKS = [
    "client-ready-acknowledged", "client-reached-arena",
    "client-beans-native-gum-applied",
    "client-crafters-gaze-reported",
    "client-pomp-appointment-received", "client-pomp-reward-received",
    "client-reflex-test-started", "client-reflex-click-sent", "client-reflex-pass-received",
    "client-chalkles-boards-built", "client-chalkles-charge-mirrored",
    "client-thetest-gaze-reported", "client-thetest-freeze-mirrored",
    "client-item-bsoda-used", "client-item-alarm-clock-used", "client-item-whistle-used",
    "client-spectate-overlay-shows-host", "client-no-exceptions",
]
LOGS = {
    "host": [root / "private/host-player.log", game / "BepInEx/LogOutput.log"],
    "client": [lab / "MultiplayerMod/private/client-player.log", lab / "MelonLoader/Latest.log"],
}
# Unity exceptions, Guard ("UpdateX failed: ..."), rejected peers and a client dropping a malformed host packet.
PROBLEM = re.compile(r"Exception|\bfailed: |Rejected peer|Host sent invalid")


# Results older than this launch (host-pid.txt is written by launch-lab.ps1) are stale and never count.
launched = (root / "private/host-pid.txt").stat().st_mtime if (root / "private/host-pid.txt").exists() else 0


def load(path):
    try:
        if path.stat().st_mtime < launched:
            return None
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


deadline = time.monotonic() + timeout
host = client = None
last = None
while time.monotonic() < deadline:
    host, client = load(host_path), load(client_path)
    status = (host or {}).get("step"), (client or {}).get("step")
    if status != last:
        print(f"... host step {status[0]}, client step {status[1]}", flush=True)
        last = status
    if host and client and host.get("finished") and client.get("finished"):
        break
    time.sleep(1)

checks = {
    "host results written": host is not None,
    "host scenario finished": bool(host and host.get("finished")),
    "client results written": client is not None,
    "client scenario finished": bool(client and client.get("finished")),
}
for side, data, expected in (("host", host, HOST_CHECKS), ("client", client, CLIENT_CHECKS)):
    observed = (data or {}).get("checks", {})
    for name in expected + [n for n in observed if n not in expected]:
        checks[f"{side}: {name}"] = observed.get(name) is True

problems = {}
for side, paths in LOGS.items():
    found = [p for p in paths if p.exists()]
    lines = []
    for path in found:
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            if PROBLEM.search(line):
                lines.append(f"{path.name}: {line.strip()[:300]}")
    problems[side] = lines
    checks[f"{side} logs: present"] = bool(found)
    checks[f"{side} logs: no exceptions"] = bool(found) and not lines

passed = sum(1 for v in checks.values() if v)
report = {
    "passed": passed,
    "failed": len(checks) - passed,
    "checks": checks,
    "host": {k: (host or {}).get(k) for k in ("step", "elapsed", "info", "notes", "exceptions", "firstException")},
    "client": {k: (client or {}).get(k) for k in ("step", "elapsed", "info", "notes", "exceptions", "firstException")},
    "logProblems": {side: lines[:20] for side, lines in problems.items()},
}
tests.mkdir(parents=True, exist_ok=True)
(tests / "encounter-verify.json").write_text(json.dumps(report, indent=1), encoding="utf-8")
for name, ok in checks.items():
    print(("PASS " if ok else "FAIL ") + name)
for side in ("host", "client"):
    data = report[side]
    for key, value in sorted((data.get("info") or {}).items()):
        print(f"INFO {side} {key} = {value}")
    for note in data.get("notes") or []:
        print(f"NOTE {side} {note}")
    if data.get("firstException"):
        print(f"NOTE {side} first exception: {data['firstException']}")
    for line in problems[side][:10]:
        print(f"LOG  {side} {line}")
print(f"{passed}/{len(checks)} passed")
sys.exit(0 if passed == len(checks) else 1)
