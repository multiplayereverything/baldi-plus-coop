"""Focused live checks for host-led entry, seed, NPC rendering and extra world doors."""
import json
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
pids = [(root / f'private/{role}-pid.txt').read_text(encoding='utf-8-sig').strip() for role in ('host', 'client')]
paths = [root / f'private/runtime-{pids[0]}.json', root / f'private/melon-lab/MultiplayerMod/private/runtime-{pids[1]}.json']
checks = dict.fromkeys(('random_shared_seed', 'host_starts_without_waiting', 'client_enters_without_elevator', 'npc_frame_interpolation', 'window_breaks', 'swing_lock', 'swing_unlock_open', 'host_reload_auto_entry'), False)
samples = []
initial_seed = None
deadline = time.monotonic() + 150
while time.monotonic() < deadline:
    try:
        h, c = [json.loads(p.read_text()) for p in paths]
    except (OSError, ValueError):
        time.sleep(.2)
        continue
    if h['epoch'] == 1:
        initial_seed = h.get('seed')
    current = {
        'random_shared_seed': h['epoch'] == c['epoch'] == 1 and h.get('seed') == c.get('seed') and h.get('seed') != 12345,
        'host_starts_without_waiting': h.get('playing') and h.get('unreadyPeers', 0) > 0 and not c.get('ack'),
        'client_enters_without_elevator': c.get('ack') and c.get('playing') and h.get('playing') and h.get('elevatorScreens') == c.get('elevatorScreens') == 0,
        'npc_frame_interpolation': c.get('observedNpcFrames', 0) > 20 and c.get('npcRenderSteps', 0) > c.get('observedNpcFrames', 0) * 1.5,
        'window_breaks': h.get('brokenWindows', 0) > 0 and h.get('brokenWindows') == c.get('brokenWindows'),
        'swing_lock': h.get('lockedSwingDoors', 0) > 0 and h.get('lockedSwingDoors') == c.get('lockedSwingDoors'),
        'swing_unlock_open': checks['swing_lock'] and h.get('lockedSwingDoors') == c.get('lockedSwingDoors') == 0 and h.get('openSwingDoors', 0) > 0 and h.get('openSwingDoors') == c.get('openSwingDoors'),
        'host_reload_auto_entry': h['epoch'] == c['epoch'] == 2 and h.get('seed') == c.get('seed') == initial_seed and c.get('ack') and h.get('playing') and c.get('playing') and h.get('elevatorScreens') == c.get('elevatorScreens') == 0 and h.get('worldHash') == c.get('worldHash'),
    }
    new = [key for key, value in current.items() if value and not checks[key]]
    if new:
        checks.update({key: True for key in new})
        samples.append({'observed': new, 'host': h, 'client': c})
        print('PASS ' + ', '.join(new), flush=True)
    if all(checks.values()):
        break
    time.sleep(.2)
result = {'checks': checks, 'passed': sum(checks.values()), 'failed': len(checks)-sum(checks.values()), 'samples': samples}
(root / 'artifacts/tests/flow-results.json').write_text(json.dumps(result, indent=2))
print(json.dumps(checks), flush=True)
raise SystemExit(0 if all(checks.values()) else 1)
