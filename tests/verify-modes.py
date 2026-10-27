"""Check a two-process challenge or Endless run (scripts/launch-lab.ps1 -Scene <scene> -ModeSync), see src/ModeSync.cs.
  verify-modes.py challenge [timeout]: the host wins; the client shows the win and takes the host's leaving as the end.
  verify-modes.py endless [timeout]: notebook reset and second collection, item respawns and the results screen.
Observes only; sends no game input. Exit code 0 when every check passed."""
import json
import sys
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
mode = sys.argv[1] if len(sys.argv) > 1 else "endless"
timeout = float(sys.argv[2]) if len(sys.argv) > 2 else 240
host_id = (root / "private/host-pid.txt").read_text(encoding="utf-8-sig").strip()
client_id = (root / "private/client-pid.txt").read_text(encoding="utf-8-sig").strip()
host_path = root / f"private/runtime-{host_id}.json"
client_path = root / f"private/melon-lab/MultiplayerMod/private/runtime-{client_id}.json"


def challenge_checks(host, client):
    return {
        "both_in_challenge": host.get("challenge") is True and client.get("modeManager") == host.get("modeManager"),
        "host_won": host.get("challengeWinsSent") == 1 and host.get("challengeWinSeen") is True,
        "client_win_screen": client.get("challengeWinsShown") == 1 and client.get("challengeWinSeen") is True,
        "client_left_as_run_end": client.get("modeEndDisconnects") == 1,
        "no_mode_mismatches": client.get("challengeWinsShown") == 1 and client.get("modeMismatches", 1) == 0,
    }


def endless_checks(host, client):
    return {
        "both_in_endless": host.get("modeManager") == "EndlessGameManager" and client.get("modeManager") == "EndlessGameManager",
        "client_reset_timers_host_only": client.get("clientResetTimersBlocked", 0) >= 1,
        "notebook_reset_mirrored": host.get("activityResetsSent", 0) >= 1 and client.get("activityResetsMirrored") == host.get("activityResetsSent"),
        "notebook_collected_again": host.get("foundNotebooks", 0) >= 2 and client.get("modeLabStep", 0) >= 3,
        "same_notebook_count": host.get("foundNotebooks", 0) >= 2 and client.get("foundNotebooks") == host.get("foundNotebooks"),
        "item_respawns_mirrored": host.get("supplyRespawnsSent", 0) >= 1 and client.get("supplyRespawnsMirrored") == host.get("supplyRespawnsSent")
                                  and client.get("clientRespawnsBlocked", 0) >= 1,
        "same_items": host.get("supplyRespawnsSent", 0) >= 1 and bool(host.get("supplyDigest")) and client.get("supplyDigest") == host.get("supplyDigest"),
        "results_for_everyone": host.get("endlessResultsSeen") is True and client.get("endlessResultsSeen") is True and client.get("endlessResultsShown") == 1,
    }


build = challenge_checks if mode == "challenge" else endless_checks
checks = dict.fromkeys(build({}, {}), False)
deadline = time.monotonic() + timeout
host = client = {}
while time.monotonic() < deadline:
    try:
        host = json.loads(host_path.read_text())
        client = json.loads(client_path.read_text())
    except (OSError, ValueError):
        time.sleep(0.5)
        continue
    for key, value in build(host, client).items():
        if value and not checks[key]:
            checks[key] = True
            print("PASS " + key, flush=True)
    if all(checks.values()):
        break
    time.sleep(0.5)
fields = ("modeManager", "foundNotebooks", "modeLabStep", "challengeWinsSent", "challengeWinsShown", "challengeWinSeen", "activityResetsSent",
          "activityResetsMirrored", "clientResetTimersBlocked", "supplyRespawnsSent", "supplyRespawnsMirrored", "clientRespawnsBlocked",
          "lastRespawnItem", "supplyDigest", "endlessResultsShown", "endlessResultsSeen", "modeEndDisconnects", "modeMismatches", "labNotebookTaken")
result = {"mode": mode, "passed": sum(checks.values()), "failed": len(checks) - sum(checks.values()), "checks": checks,
          "host": {k: host.get(k) for k in fields}, "client": {k: client.get(k) for k in fields}}
for key, value in checks.items():
    if not value:
        print("FAIL " + key, flush=True)
(root / "artifacts/tests").mkdir(parents=True, exist_ok=True)
(root / f"artifacts/tests/modes-{mode}-verify.json").write_text(json.dumps(result, indent=1), encoding="utf-8")
print(f"{result['passed']}/{len(checks)} passed")
sys.exit(0 if all(checks.values()) else 1)
