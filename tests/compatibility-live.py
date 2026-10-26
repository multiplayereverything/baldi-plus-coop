"""Check real host rejection messages without joining an active room."""
import argparse
import hashlib
import json
import socket
import struct
from pathlib import Path

parser=argparse.ArgumentParser()
parser.add_argument('--port',type=int,required=True)
args=parser.parse_args()
root=Path(__file__).resolve().parents[2]
digest=hashlib.sha256((root/'BALDI_Data/Managed/Assembly-CSharp.dll').read_bytes()).hexdigest()
def text(value):
    data=value.encode();return struct.pack('<H',len(data))+data
def hello(protocol=14,version='0.9.0',game=digest,code=''):
    return b'\x01'+struct.pack('<i',protocol)+text(version)+text(game)+text('Compatibility probe')+text(code)
cases={
    'wrong-protocol':(hello(protocol=-1),'Mod version mismatch'),
    'wrong-mod-version':(hello(version='0.4.13'),'Mod version mismatch'),
    'wrong-game-build':(hello(game='0'*64),'Game version mismatch'),
    'wrong-room-code':(hello(code='wrong-code'),'Incorrect room code'),
}
checks={}
for name,(body,expected) in cases.items():
    with socket.create_connection(('127.0.0.1',args.port),timeout=5) as peer:
        peer.sendall(struct.pack('<i',len(body))+body)
        def exact(size):
            data=b''
            while len(data)<size:
                chunk=peer.recv(size-len(data))
                if not chunk:raise EOFError(name)
                data+=chunk
            return data
        size=struct.unpack('<i',exact(4))[0]
        packet=exact(size)
        length=struct.unpack('<H',packet[1:3])[0]
        reason=packet[3:3+length].decode()
        checks[name]=packet[0]==15 and expected in reason and peer.recv(1)==b''
        print(name,reason)
result={'passed':sum(checks.values()),'failed':sum(not c for c in checks.values()),'checks':checks}
(root/'MultiplayerMod/artifacts/tests/compatibility-live-results.json').write_text(json.dumps(result))
print(json.dumps(result))
if result['failed']:raise SystemExit(1)

