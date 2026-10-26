"""Observe two test-owned game processes; do not send game input."""
import json
import sys
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
host_id = (root / "private/host-pid.txt").read_text(encoding="utf-8-sig").strip()
client_id = (root / "private/client-pid.txt").read_text(encoding="utf-8-sig").strip()
host_path = root / f"private/runtime-{host_id}.json"
client_path = root / f"private/melon-lab/MultiplayerMod/private/runtime-{client_id}.json"
checks = dict.fromkeys([
    "two_loaders_connected", "matching_level", "bidirectional_player_poses",
    "shared_notebook", "shared_door", "shared_activity", "npc_replication",
    "matching_transition", "shared_item", "team_ping", "latency", "duplicate_item_blocked", "full_inventory_blocked", "authoritative_item_catalog",
], False)
if "--sprites" in sys.argv:
    checks.update(dict.fromkeys(["sprite_avatar", "sprite_directions", "sprite_walk", "sprite_idle"], False))
samples = []
deadline = time.monotonic() + 180
while time.monotonic() < deadline:
    try:
        host = json.loads(host_path.read_text())
        client = json.loads(client_path.read_text())
    except (OSError, ValueError):
        time.sleep(0.5)
        continue
    matching = (host["ready"] and client["ready"] and client["ack"]
                and host["epoch"] == client["epoch"] and host["worldHash"] == client["worldHash"])
    current = {
        "two_loaders_connected": host["peers"] == client["peers"] == 2,
        "matching_level": matching,
        "bidirectional_player_poses": host["remotePoses"] > 0 and client["remotePoses"] > 0,
        "shared_notebook": matching and host["notebooks"] == client["notebooks"] and host["notebooks"] > 0,
        "shared_door": client.get("observedDoorOpens", 0) > 0 and host.get("acceptedInteractions", 0) >= 2,
        "shared_activity": matching and host.get("completedActivities", 0) == client.get("completedActivities", 0) and host.get("completedActivities", 0) >= 2,
        "npc_replication": client.get("observedNpcFrames", 0) > 0,
        "matching_transition": matching and host["epoch"] >= 2,
        "shared_item": matching and host.get("consumedSupplies", 0) == client.get("consumedSupplies", 0) and host.get("consumedSupplies", 0) > 0 and client.get("pickupGrants", 0) == 1,
        "team_ping": host.get("teamPings", 0) > 0 and client.get("teamPings", 0) > 0,
        "latency": host.get("latencySamples", 0) > 0 and client.get("rttMs", -1) >= 0,
        # loading_barrier was removed with the group loading barrier in 0.3.1 (host-led automatic entry).
        "duplicate_item_blocked": client.get("duplicateItemBlocked", False),
        "full_inventory_blocked": client.get("fullInventoryBlocked", False),
        "authoritative_item_catalog": matching and client.get("catalogApplied", False) and client.get("catalogPerturbed", False) and host.get("catalogHash") == client.get("catalogHash"),
    }
    if "--sprites" in sys.argv:
        current.update({
            "sprite_avatar": host.get("spriteAvatars", 0) > 0 and client.get("spriteAvatars", 0) > 0 and host.get("spriteFrames") == client.get("spriteFrames") == 16 and host.get("spriteTransparent") and client.get("spriteTransparent"),
            "sprite_directions": host.get("spriteDirections") == 15,
            "sprite_walk": host.get("spriteWalkingFrames") == 15,
            "sprite_idle": host.get("spriteIdle", False) and client.get("spriteIdle", False),
        })
    new = [key for key, value in current.items() if value and not checks[key]]
    if new:
        checks.update({key: True for key in new})
        samples.append({"observed": new, "host": host, "client": client})
        print("PASS " + ", ".join(new), flush=True)
    if all(checks.values()):
        break
    time.sleep(0.5)
result = {"checks": checks, "passed": sum(checks.values()), "failed": len(checks) - sum(checks.values()),
          "host_pid": int(host_id), "client_pid": int(client_id), "samples": samples,
          "scope": "Two local Unity processes, BepInEx host and MelonLoader client; test actions call game methods, not native mouse input."}
output = root / "artifacts/tests/live-results.json"
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(result, indent=2))
print(json.dumps(checks), flush=True)
raise SystemExit(0 if all(checks.values()) else 1)
