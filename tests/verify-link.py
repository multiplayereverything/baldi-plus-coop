"""Link-health run: BepInEx host and MelonLoader client through tests/tcp-chaos.py.

Launched by `launch-lab.ps1 -Chaos <profile> -StallHost 25`. Observes both games' diagnostics and drives the proxy:
1. both connected through the proxy, same floor;
2. the host's game loop blocks for 25 s (longer than the 15 s socket timeout);
   the client must wait (notice only), not drop the link or migrate, and the host must keep the client;
3. a 10 s network outage (proxy holds both directions); same expectations;
4. the proxy cuts the link: a dead link must still be noticed (the client starts migration or reports a drop).
--expect-mutant: the run used -NoKeepAlive; succeed only if step 2 fails (the check can see the bug).
"""
import json
import socket
import sys
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
host_id = (root / "private/host-pid.txt").read_text(encoding="utf-8-sig").strip()
client_id = (root / "private/client-pid.txt").read_text(encoding="utf-8-sig").strip()
host_path = root / f"private/runtime-{host_id}.json"
client_path = root / f"private/melon-lab/MultiplayerMod/private/runtime-{client_id}.json"
info = json.loads((root / "private/chaos-info.json").read_text())
mutant = "--expect-mutant" in sys.argv
checks = dict.fromkeys(["connected_through_proxy", "matching_level", "host_stall_happened", "client_waited_through_host_stall",
                        "host_kept_client_through_stall", "outage_survived", "dead_link_still_detected"], False)
notes = {}


def proxy(message):
    with socket.create_connection(("127.0.0.1", info["control"]), timeout=5) as s:
        s.sendall((json.dumps(message) + "\n").encode())
        return json.loads(s.makefile().readline())


def read():
    try: return json.loads(host_path.read_text()), json.loads(client_path.read_text())
    except (OSError, ValueError): return None, None


def wait_for(predicate, seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        host, client = read()
        if host and client and predicate(host, client): return host, client
        time.sleep(0.5)
    return read()


def matching(h, c):
    return h["ready"] and c["ready"] and c["ack"] and h["epoch"] == c["epoch"] and h["worldHash"] == c["worldHash"]


def healthy(h, c):
    return (h["peers"] == c["peers"] == 2 and c["linkDrops"] == 0 and h["linkDrops"] == 0 and c["migrationTerm"] == 0
            and not c["migrating"] and 0 <= c["hostPacketAgeMs"] < 1500)


def stats():
    try: return proxy({"cmd": "stats"})
    except OSError as ex: return {"error": str(ex)}


def report():
    result = {"checks": checks, "passed": sum(checks.values()), "failed": len(checks) - sum(checks.values()), "mutant": mutant,
              "profile": info["profile"], "notes": notes, "proxy": stats(),
              "scope": "Two local Unity processes (BepInEx host, MelonLoader client) through a seeded TCP impairment proxy."}
    name = "link-mutant-results.json" if mutant else "link-results.json"
    (root / "artifacts/tests" / name).write_text(json.dumps(result, indent=2))
    for k, v in checks.items(): print(("PASS " if v else "FAIL ") + k)
    if mutant:
        caught = not checks["client_waited_through_host_stall"]
        print("mutant caught" if caught else "mutant NOT caught")
        raise SystemExit(0 if caught else 1)
    print(f"{result['passed']} passed")
    raise SystemExit(0 if all(checks.values()) else 1)


h, c = wait_for(lambda h, c: h["peers"] == c["peers"] == 2 and matching(h, c), 150)
if not (h and c and h["peers"] == c["peers"] == 2):
    print("never connected (startup stall?)")
    raise SystemExit(3)  # link-test.ps1 relaunches once
checks["connected_through_proxy"] = h["peers"] == c["peers"] == 2 and proxy({"cmd": "stats"})["connections"] >= 1
checks["matching_level"] = matching(h, c)
if not checks["matching_level"]:
    report()

# 2. host game-loop stall (starts 8 s after the floor is up on the host)
h, c = wait_for(lambda h, c: h["labStalls"] >= 1 or c["linkDrops"] > 0 or c["migrationTerm"] > 0 or c["migrating"], 90)
checks["host_stall_happened"] = bool(h and h["labStalls"] >= 1)
h, c = wait_for(lambda h, c: (healthy(h, c) and c["hostQuietMaxMs"] >= 15000) or c["linkDrops"] > 0 or c["migrationTerm"] > 0, 30)
notes["after_host_stall"] = {"clientQuietMaxMs": c["hostQuietMaxMs"], "clientDrops": c["linkDrops"], "clientMigrationTerm": c["migrationTerm"],
                             "hostDrops": h["linkDrops"], "peers": [h["peers"], c["peers"]]}
checks["client_waited_through_host_stall"] = checks["host_stall_happened"] and healthy(h, c) and c["hostQuietMaxMs"] >= 15000
checks["host_kept_client_through_stall"] = checks["host_stall_happened"] and h["peers"] == 2 and h["linkDrops"] == 0
if mutant or not checks["client_waited_through_host_stall"]:
    report()

# 3. network outage: the proxy holds both directions for 10 s
proxy({"cmd": "stall", "dir": "both", "seconds": 10})
time.sleep(11)
h, c = wait_for(healthy, 15)
notes["after_outage"] = {"clientDrops": c["linkDrops"], "clientMigrationTerm": c["migrationTerm"], "hostPacketAgeMs": c["hostPacketAgeMs"]}
checks["outage_survived"] = healthy(h, c) and matching(h, c)

# 4. dead link
proxy({"cmd": "cut"})
h, c = wait_for(lambda h, c: c["linkDrops"] > 0 or c["migrationTerm"] > 0 or c["migrating"] or c["peers"] != 2, 25)
notes["after_cut"] = {"clientDrops": c["linkDrops"], "clientMigrationTerm": c["migrationTerm"], "clientMigrating": c["migrating"], "clientPeers": c["peers"]}
checks["dead_link_still_detected"] = c["linkDrops"] > 0 or c["migrationTerm"] > 0 or c["migrating"]
report()
