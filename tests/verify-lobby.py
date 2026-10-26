"""Verify the 0.6 lobby features from lab runs started by scripts/launch-lab.ps1.

  python tests/verify-lobby.py lobby   after  launch-lab.ps1 -LobbyTour
  python tests/verify-lobby.py lan     after  launch-lab.ps1 -LanTour

Waits for the tour's own results (written by the game) and, for the LAN test, also checks the
host's diagnostics. Writes artifacts/tests/lobby-verify-<mode>.json and exits 1 on any failure."""
import json
import sys
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
mode = sys.argv[1] if len(sys.argv) > 1 else "lobby"
tests = root / "artifacts/tests"


def wait_json(path, timeout, ready=lambda d: True):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            if ready(data):
                return data
        except (OSError, ValueError):
            pass
        time.sleep(.5)
    return None


def pid(role):
    return (root / f"private/{role}-pid.txt").read_text(encoding="utf-8-sig").strip()


checks = {}
if mode == "lobby":
    tour = wait_json(tests / "lobby-results.json", 240)
    checks["tour finished"] = tour is not None
    if tour:
        checks.update({f"tour: {k}": v for k, v in tour["checks"].items()})
elif mode == "lan":
    client = wait_json(root / "private/melon-lab/MultiplayerMod/artifacts/tests/lan-tour-results.json", 240)
    if client is None:  # the Melon lab writes into its own MultiplayerMod folder when it has one, else the shared one
        client = wait_json(tests / "lan-tour-results.json", 5)
    checks["client tour finished"] = client is not None
    if client:
        checks.update({f"client: {k}": v for k, v in client["checks"].items()})
    host_path = root / f"private/runtime-{pid('host')}.json"
    def host_ready(d):
        # The client finishes its own checks first; give the host time to receive its pose and HUD.
        return ";1:colour=4:avatar=4:hudSlots=" in d.get("lobbyFeatures", "") and ":hudSlots=-1" not in d.get("lobbyFeatures", "").split(";1:")[1]
    host = wait_json(host_path, 60, host_ready) or wait_json(host_path, 5, lambda d: "lobbyFeatures" in d)
    features = (host or {}).get("lobbyFeatures", "")
    sounds = (host or {}).get("teamSoundLog", "").split(",")
    # Host diagnostics: "<id>:colour=<n>:avatar=<n>:hudSlots=<n>:hudAge=<s>" per peer.
    peers = {}
    for entry in filter(None, features.split(";")):
        fields = entry.split(":")
        peers[int(fields[0])] = {k: int(v) for k, v in (f.split("=") for f in fields[1:])}
    client_peer = peers.get(1, {})
    checks["host: client colour from LAN join"] = client_peer.get("colour") == 4
    checks["host: client avatar in client colour"] = client_peer.get("avatar") == 4
    checks["host: HUD data from client"] = client_peer.get("hudSlots", -1) >= 1 and 0 <= client_peer.get("hudAge", 99) <= 3
    checks["host: join sound played"] = "join" in sounds
    checks["host: beacon stopped once the run began"] = (host or {}).get("lanBeacon") is False
else:
    sys.exit(f"unknown mode {mode}")

passed = sum(1 for v in checks.values() if v)
report = {"mode": mode, "passed": passed, "failed": len(checks) - passed, "checks": checks}
(tests / f"lobby-verify-{mode}.json").write_text(json.dumps(report, indent=1), encoding="utf-8")
for name, ok in checks.items():
    print(("PASS " if ok else "FAIL ") + name)
print(f"{passed}/{len(checks)} passed")
sys.exit(0 if passed == len(checks) else 1)
