"""Observe only hiding, closest-player targeting, and shop transactions in two isolated game copies."""
import json
import time
from pathlib import Path

root = Path(__file__).resolve().parents[1]
host_id = (root / 'private/host-pid.txt').read_text(encoding='utf-8-sig').strip()
client_id = (root / 'private/client-pid.txt').read_text(encoding='utf-8-sig').strip()
paths = [root / f'private/runtime-{host_id}.json', root / f'private/melon-lab/MultiplayerMod/private/runtime-{client_id}.json']
checks = dict.fromkeys(['countdown_alignment', 'countdown_beats', 'nearest_player_switches', 'shop_stock', 'shop_purchase', 'shop_refund', 'shop_funds_and_duplicate'], False)
aligned = set()
samples = []
deadline = time.monotonic() + 160
while time.monotonic() < deadline:
    try:
        host, client = [json.loads(p.read_text()) for p in paths]
    except (OSError, ValueError):
        time.sleep(.2)
        continue
    if host['epoch'] == client['epoch'] == 1 and host.get('hidingStep') == client.get('hidingStep') and 0 <= host.get('hidingStep', 10) <= 9:
        aligned.add(host['hidingStep'])
    current = {
        'countdown_alignment': len(aligned) >= 8,
        'countdown_beats': host.get('hidingBeats') == client.get('hidingBeats') == 11 and host.get('hidingFinished') and client.get('hidingFinished'),
        'nearest_player_switches': host.get('nearestRemote') and host.get('nearestHost') and host.get('nearestSwitchedBack'),
        'shop_stock': host.get('shopStock', 0) > 0 and host.get('shopStock') == client.get('shopStock') and client.get('shopReplicaFrames', 0) > 0,
        'shop_purchase': host.get('shopPurchases') == client.get('shopPurchases') == 1 and host.get('shopReserved') == 0 and client.get('shopTestStep') == 6,
        'shop_refund': host.get('shopRefunds') == 1 and host.get('shopTestStep') == 4,
        'shop_funds_and_duplicate': host.get('shopRejected', 0) >= 2 and client.get('shopTestCost', 0) > 0 and host.get('shopLastCharge') == client.get('shopLastCharge') == client.get('shopTestCost') and host.get('shopWallet') == client.get('shopWallet') == 10000-client.get('shopTestCost', 0),
    }
    new = [key for key, value in current.items() if value and not checks[key]]
    if new:
        checks.update({key: True for key in new})
        samples.append({'observed': new, 'host': host, 'client': client})
        print('PASS ' + ', '.join(new), flush=True)
    if all(checks.values()):
        break
    time.sleep(.2)
result = {'checks': checks, 'passed': sum(checks.values()), 'failed': len(checks)-sum(checks.values()), 'aligned_beats': sorted(aligned), 'samples': samples,
          'scope': 'One BepInEx host and one MelonLoader client. Only countdown, nearest-target changes and real-shop purchase/refund/duplicate checks. Lab methods supply movement; no physical input automation.'}
(root / 'artifacts/tests/sync-results.json').write_text(json.dumps(result, indent=2))
print(json.dumps(checks), flush=True)
raise SystemExit(0 if all(checks.values()) else 1)
