"""Verify the focused BepInEx/MelonLoader elevator tour from runtime evidence."""
import json
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[1]
host = (root / "private/elevator-host.log").read_text(errors="replace")
client = (root / "private/melon-lab/MelonLoader/Latest.log").read_text(errors="replace")
checks = {}
for generation, name in [(1, "initial-entry"), (2, "restart-pitstop"), (3, "return-to-floor")]:
    exit_at = client.find(f"Client native elevator exit started for generation {generation}")
    begin_at = client.find(f"Level began generation {generation} nativeElevator=True")
    checks[name] = 0 <= exit_at < begin_at
parts = re.split(r"Loading the host's level with seed", client)[1:]
checks["validation-before-exit"] = len(parts) >= 3 and all(
    0 <= part.find("Level matched. Co-op connected.") < part.find("Client native elevator exit started")
    for part in parts[:3]
)
pid = int((root / "private/elevator-client-pid.txt").read_text())
state = json.loads((root / f"private/melon-lab/MultiplayerMod/private/runtime-{pid}.json").read_text())
checks["native-screen-cleanup"] = state["epoch"] == 3 and state["playing"] and state["elevatorScreens"] == 0
checks["host-progressed"] = all(f"Level began generation {n}" in host for n in (1, 2, 3))
checks["matching-worlds"] = len(re.findall("loaded the same level", host)) >= 3 and "mismatch" not in client.lower()
checks["single-patch-installation"] = "Elevator start patch count=1" in host and "Elevator start patch count=1" in client
result = {"passed": sum(checks.values()), "failed": len(checks) - sum(checks.values()), "checks": checks}
output = root / "artifacts/tests/elevator-results.json"
output.write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
raise SystemExit(bool(result["failed"]))
