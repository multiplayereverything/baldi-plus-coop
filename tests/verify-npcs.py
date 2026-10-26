"""Focused two-loader NPC spawn, effect, visibility and culprit ownership checks."""
import json
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
pids = [(root / f'private/{role}-pid.txt').read_text(encoding='utf-8-sig').strip() for role in ('host', 'client')]
paths = [root / f'private/runtime-{pids[0]}.json', root / f'private/melon-lab/MultiplayerMod/private/runtime-{pids[1]}.json']
checks = dict.fromkeys(('npc_catalog', 'npc_animation', 'firstprize_remote_target', 'rope_owner_only', 'rope_completion_reward', 'bully_once', 'sweep_force', 'sweep_release', 'bully_visibility_show', 'bully_visibility_hide', 'principal_remote_culprit', 'detention_clock', 'host_despawn_catalog', 'hall_monitor_replica', 'hall_monitor_marks_client'), False)
samples = []
deadline = time.monotonic() + 220
while time.monotonic() < deadline:
    try:
        h, c = [json.loads(p.read_text()) for p in paths]
    except (OSError, ValueError):
        time.sleep(.2)
        continue
    stage = h.get('npcLabStage', 0)
    matched = h.get('npcCount', 0) >= 5 and h.get('npcCatalog') == c.get('npcCatalog') and c.get('npcPresentationFrames', 0) > 0
    current = {
        'npc_catalog': stage > 0 and matched,
        'npc_animation': c.get('npcAnimationCorrections', 0) > 0,
        'firstprize_remote_target': h.get('npcLabFirstPrizeTarget'),
        'rope_owner_only': c.get('localJumpropes', 0) == 1 and h.get('localJumpropes') == 0 and h.get('ropeStarts') == c.get('ropeStarts') == 1,
        'rope_completion_reward': h.get('ropeEnds') == c.get('ropeEnds') == 1 and h.get('shopWallet', 0) > 0 and h.get('shopWallet') == c.get('shopWallet'),
        'bully_once': h.get('bullyThefts') == c.get('bullyThefts') == 1,
        'sweep_force': c.get('npcPushFrames', 0) > 2,
        'sweep_release': stage >= 5 and c.get('npcPushFrames', 0) > 2 and c.get('attachedNpcPushes') == 0,
        'bully_visibility_show': stage == 5 and h.get('visibleBully') == c.get('visibleBully') == 1,
        'bully_visibility_hide': stage >= 6 and h.get('visibleBully') == c.get('visibleBully') == 0,
        'principal_remote_culprit': h.get('principalRemoteChases', 0) > 0 and h.get('principalRemoteDetentions', 0) == 1 and c.get('principalDetentionCommands', 0) == 1 and c.get('localDetained') and h.get('npcLabHostStayed') and not h.get('localDetained'),
        'host_despawn_catalog': stage >= 8 and matched,
        # Hall Monitor (0.8.0): the client's replica is the mod's own NPC body, and a real run in his sight on the
        # client is marked by the host and arrives as a cue, a local mark and the native HUD gauge.
        'hall_monitor_replica': h.get('hallMonitorPresent') and h.get('hallMonitorBodyOk') and c.get('hallMonitorPresent') and c.get('hallMonitorBodyOk'),
        'hall_monitor_marks_client': stage == 9 and h.get('hallMonitorMarks', 0) >= 1 and c.get('hallMonitorClientCues', 0) >= 1 and c.get('hallMonitorLocalMarked') and c.get('hallMonitorGauge'),
        # Diagnostics update once per second in independent processes. Project the older
        # sample to the same UTC instant using the native detention rate (1.25 at lab scale 1).
        'detention_clock': h.get('detentionRemaining', 0) > 1 and c.get('detentionRemaining', 0) > 1 and abs((h.get('detentionRemaining', 0)-c.get('detentionRemaining', 0)) + (h.get('sampleUtcMs', 0)-c.get('sampleUtcMs', 0))*.001*1.25) < .4,
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
(root / 'artifacts/tests/npc-results.json').write_text(json.dumps(result, indent=2))
print(json.dumps(checks), flush=True)
raise SystemExit(0 if all(checks.values()) else 1)
