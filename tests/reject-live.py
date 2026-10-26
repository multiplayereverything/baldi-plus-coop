"""Check that malformed/unhandshaken peers cannot join the test host."""
import socket
import struct
import time
from pathlib import Path
import hashlib

def text(value):
    data = value.encode("utf-8")
    return struct.pack("<H", len(data)) + data

root = Path(__file__).resolve().parents[2]
game_hash = hashlib.sha256((root / "BALDI_Data/Managed/Assembly-CSharp.dll").read_bytes()).hexdigest()

def hello(code):
    return b"\x01" + struct.pack("<i", 14) + text("0.9.1") + text(game_hash) + text("Probe") + text(code)

cases = {
    "message before handshake": b"\x07" + struct.pack("<ii4f", 1, 3, 0, 0, 0, 0),
    "wrong protocol": b"\x01" + struct.pack("<i", -1) + b"\x00\x00" * 4,
    "incorrect room code": hello("incorrect-code"),
    "late join": hello(""),
}
for name, body in cases.items():
    with socket.create_connection(("127.0.0.1", 27015), timeout=3) as peer:
        peer.sendall(struct.pack("<i", len(body)) + body)
        peer.settimeout(5)
        def exact(count):
            data = b""
            while len(data) < count:
                chunk = peer.recv(count - len(data))
                assert chunk, name + ": missing rejection reason"
                data += chunk
            return data
        size = struct.unpack("<i", exact(4))[0]
        response = exact(size)
        assert response[0] == 15, name + ": expected Reject packet"
        length = struct.unpack("<H", response[1:3])[0]
        reason = response[3:3 + length].decode("utf-8")
        assert reason, name + ": empty reason"
        assert peer.recv(4) == b"", name + ": socket did not close"
        print("PASS " + name)
        print("  Reason: " + reason)
    time.sleep(0.1)

