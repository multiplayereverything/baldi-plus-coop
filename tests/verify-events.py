"""Check a two-process random-event run (scripts/launch-lab.ps1 -EventSync): the host runs a fog event for 6 s and the
client mirrors its begin and end. Observes only; sends no game input. Exit code 0 when every check passed."""
import json
import sys
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
host_id = (root / "private/host-pid.txt").read_text(encoding="utf-8-sig").strip()
client_id = (root / "private/client-pid.txt").read_text(encoding="utf-8-sig").strip()
host_path = root / f"private/runtime-{host_id}.json"
client_path = root / f"private/melon-lab/MultiplayerMod/private/runtime-{client_id}.json"
checks = dict.fromkeys(["same_event_list", "host_sent_begin_and_end", "client_mirrored_fog", "client_fog_active_while_host_ran_it",
                        "client_mirror_ended", "no_event_mismatches", "client_timers_still_host_only"], False)
timeout = float(sys.argv[1]) if len(sys.argv) > 1 else 240
deadline = time.monotonic() + timeout
host = client = {}
while time.monotonic() < deadline:
    try:
        host = json.loads(host_path.read_text())
        client = json.loads(client_path.read_text())
    except (OSError, ValueError):
        time.sleep(0.5)
        continue
    current = {
        "same_event_list": client.get("eventCount", 0) > 0 and host.get("eventTypes") == client.get("eventTypes"),
        "host_sent_begin_and_end": host.get("eventLabStep") == 3 and host.get("eventsSent", 0) >= 2,
        "client_mirrored_fog": client.get("eventsMirrored", 0) >= 1 and client.get("lastMirroredEvent") == "Fog",
        "client_fog_active_while_host_ran_it": "Fog" in client.get("activeEvents", "") and "Fog" in host.get("activeEvents", ""),
        "client_mirror_ended": client.get("eventsMirrorEnded", 0) >= 1 and "Fog" not in client.get("activeEvents", "Fog"),
        "no_event_mismatches": client.get("eventsMirrored", 0) >= 1 and client.get("eventMismatches", 1) == 0,
        "client_timers_still_host_only": client.get("eventsStarted") is False,
    }
    new = [k for k, v in current.items() if v and not checks[k]]
    for k in new:
        checks[k] = True
        print("PASS " + k, flush=True)
    # Lever check only when the floor has a host-owned button (reported, not required, when it has none).
    if client.get("labButtonPressed"):
        checks.setdefault("lever_press_host_authoritative", False)
        if (host.get("acceptedButtons", 0) >= 1 and host.get("lockdownDoorsOpen") == client.get("lockdownDoorsOpen")
                and (client.get("lockdownDoors", 0) == 0 or client.get("lockdownDoorsOpen") != client.get("labButtonDoorsOpenBefore"))):
            if not checks["lever_press_host_authoritative"]:
                print("PASS lever_press_host_authoritative", flush=True)
            checks["lever_press_host_authoritative"] = True
    if all(checks.values()) and (client.get("eventLabStep", 0) >= 4 or not client):
        break
    time.sleep(0.5)
result = {"passed": sum(checks.values()), "failed": len(checks) - sum(checks.values()), "checks": checks,
          "host": {k: host.get(k) for k in ("eventTypes", "eventsSent", "eventLabStep", "activeEvents", "buttons", "acceptedButtons", "lockdownDoors", "lockdownDoorsOpen")},
          "client": {k: client.get(k) for k in ("eventTypes", "eventsMirrored", "eventsMirrorEnded", "eventMismatches", "activeEvents", "eventsStarted", "buttons", "labButtonPressed", "lockdownDoors", "lockdownDoorsOpen")}}
(root / "artifacts/tests").mkdir(parents=True, exist_ok=True)
(root / "artifacts/tests/events-verify.json").write_text(json.dumps(result, indent=1), encoding="utf-8")
print(f"{result['passed']}/{len(checks)} passed")
sys.exit(0 if all(checks.values()) else 1)
