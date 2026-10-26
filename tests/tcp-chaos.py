"""Seeded bad-network proxy for TCP game links (lab only, loopback).

The hub's chaos box (coop-anything tools/chaos) impairs UDP datagrams. Baldi Together runs over TCP, where the
kernel already repairs loss, reordering and duplicates, so what a player feels is delay, jitter, head-of-line
stalls after a loss, a bandwidth ceiling, outages and cut connections. This proxy models exactly those, per
direction, with the hub's profile table (tools/chaos/profiles.json; embedded copy below if the hub is missing):

- delay: latency +/- jitter (uniform), and bytes always leave in order;
- loss (Gilbert-Elliott good/bad states, as in the hub): a lost segment is held one retransmission timeout,
  max(200 ms, 2 x latency) + latency, and everything behind it waits too;
- bandwidth_bps: pacing; the reader stops reading once buffer_ms of data is queued (TCP backpressure);
- live control on a loopback port, one JSON object per line:
  {"cmd":"set_profile","profile":"hell"}  {"cmd":"stall","dir":"both","seconds":8}
  {"cmd":"cut"}  {"cmd":"stats"}  {"cmd":"shutdown"}
  stall holds the data (an outage TCP survives); cut closes every proxied connection (a dead link).

  python -I tests/tcp-chaos.py --listen 7801 --target 127.0.0.1:27015 --profile bad_wifi --control 7802 --info private/chaos-info.json
  python -I tests/tcp-chaos.py call 7802 '{"cmd":"stall","dir":"both","seconds":8}'
  python -I tests/tcp-chaos.py selftest
"""
import asyncio
import json
import random
import socket
import struct
import sys
import time
from pathlib import Path

HUB_PROFILES = Path.home() / ".claude/coop-anything/tools/chaos/profiles.json"
EMBEDDED = {
    "lan": {"seed": 1001, "latency_ms": 0.5, "jitter_ms": 0.2, "loss": 0.0, "burst_enter": 0.0, "burst_exit": 0.5, "burst_loss": 1.0, "bandwidth_bps": 0, "buffer_ms": 200},
    "good_wifi": {"seed": 1002, "latency_ms": 12, "jitter_ms": 4, "loss": 0.005, "burst_enter": 0.0, "burst_exit": 0.5, "burst_loss": 1.0, "bandwidth_bps": 0, "buffer_ms": 200},
    "bad_wifi": {"seed": 1003, "latency_ms": 35, "jitter_ms": 25, "loss": 0.02, "burst_enter": 0.01, "burst_exit": 0.25, "burst_loss": 0.6, "bandwidth_bps": 0, "buffer_ms": 200},
    "mobile_tether": {"seed": 1004, "latency_ms": 70, "jitter_ms": 40, "loss": 0.01, "burst_enter": 0.01, "burst_exit": 0.2, "burst_loss": 0.5, "bandwidth_bps": 2000000, "buffer_ms": 300},
    "transatlantic": {"seed": 1005, "latency_ms": 90, "jitter_ms": 8, "loss": 0.003, "burst_enter": 0.0, "burst_exit": 0.5, "burst_loss": 1.0, "bandwidth_bps": 0, "buffer_ms": 200},
    "hell": {"seed": 1006, "latency_ms": 200, "jitter_ms": 150, "loss": 0.08, "burst_enter": 0.03, "burst_exit": 0.1, "burst_loss": 0.9, "bandwidth_bps": 512000, "buffer_ms": 500},
}


def load_profiles():
    try:
        table = json.loads(HUB_PROFILES.read_text(encoding="utf-8"))
        return {k: v for k, v in table.items() if not k.startswith("_")}, str(HUB_PROFILES)
    except (OSError, ValueError):
        return dict(EMBEDDED), "embedded"


PROFILES, PROFILE_SOURCE = load_profiles()


class Direction:
    """One direction of one connection: ordered release times from the impairment model."""

    def __init__(self, name, profile, seed):
        self.name = name
        self.set_profile(profile, seed)
        self.release = 0.0
        self.stall_until = 0.0
        self.bad = False

    def set_profile(self, profile, seed):
        self.p = dict(profile)
        self.rng = random.Random(f"{seed}:{self.name}")

    def schedule(self, now, size):
        p = self.p
        if self.bad:
            if self.rng.random() < p.get("burst_exit", 0.5): self.bad = False
        elif self.rng.random() < p.get("burst_enter", 0.0): self.bad = True
        lost = self.rng.random() < (p.get("burst_loss", 1.0) if self.bad else p.get("loss", 0.0))
        latency = p.get("latency_ms", 0) / 1000
        delay = max(0.0, latency + self.rng.uniform(-1, 1) * p.get("jitter_ms", 0) / 1000)
        if lost: delay += max(0.2, 2 * latency) + latency  # retransmission timeout, then the resend
        at = max(now + delay, self.release, self.stall_until)
        bps = p.get("bandwidth_bps", 0)
        if bps: at = max(at, self.release + size * 8 / bps)
        self.release = at
        return at, lost

    def buffer_limit(self):
        bps = self.p.get("bandwidth_bps", 0)
        return max(16384, int(bps / 8 * self.p.get("buffer_ms", 200) / 1000)) if bps else 1 << 22


class Proxy:
    def __init__(self, target, profile, seed=None):
        self.target = target
        self.profile_name = profile
        self.seed = seed if seed is not None else PROFILES[profile]["seed"]
        self.directions = []
        self.writers = []
        self.stats = {"c2s": {"bytes": 0, "chunks": 0, "lost": 0}, "s2c": {"bytes": 0, "chunks": 0, "lost": 0},
                      "connections": 0, "cuts": 0, "stalls": 0, "started": time.monotonic()}
        self.done = asyncio.Event()

    async def pipe(self, reader, writer, direction):
        queue, queued = asyncio.Queue(), [0]
        space = asyncio.Event(); space.set()

        async def send():
            while True:
                at, data = await queue.get()
                if data is None: break
                wait = at - time.monotonic()
                if wait > 0: await asyncio.sleep(wait)
                writer.write(data)
                await writer.drain()
                queued[0] -= len(data)
                if queued[0] < direction.buffer_limit(): space.set()
            writer.close()

        sender = asyncio.ensure_future(send())
        try:
            while True:
                data = await reader.read(16384)
                if not data: break
                at, lost = direction.schedule(time.monotonic(), len(data))
                s = self.stats[direction.name]; s["bytes"] += len(data); s["chunks"] += 1; s["lost"] += int(lost)
                queued[0] += len(data)
                await queue.put((at, data))
                if queued[0] >= direction.buffer_limit():
                    space.clear(); await space.wait()
        except (ConnectionError, OSError):
            pass
        await queue.put((0, None))
        try: await sender
        except (ConnectionError, OSError): pass

    async def handle(self, client_reader, client_writer):
        host, port = self.target
        try: server_reader, server_writer = await asyncio.open_connection(host, port)
        except OSError:
            client_writer.close(); return
        self.stats["connections"] += 1
        n = self.stats["connections"]
        c2s = Direction("c2s", PROFILES[self.profile_name], f"{self.seed}:{n}")
        s2c = Direction("s2c", PROFILES[self.profile_name], f"{self.seed}:{n}")
        self.directions += [c2s, s2c]
        self.writers += [client_writer, server_writer]
        await asyncio.gather(self.pipe(client_reader, server_writer, c2s), self.pipe(server_reader, client_writer, s2c))

    def control(self, msg):
        cmd = msg.get("cmd")
        now = time.monotonic()
        if cmd == "set_profile":
            name = msg["profile"]
            if name not in PROFILES: return {"error": f"unknown profile {name}"}
            self.profile_name = name
            for d in self.directions: d.set_profile(PROFILES[name], f"{msg.get('seed', PROFILES[name]['seed'])}")
            return {"ok": True, "profile": name}
        if cmd == "stall":
            which = msg.get("dir", "both")
            for d in self.directions:
                if which in ("both", d.name): d.stall_until = max(d.stall_until, now + float(msg["seconds"]))
            self.stats["stalls"] += 1
            return {"ok": True}
        if cmd == "cut":
            for w in self.writers:
                try:
                    sock = w.get_extra_info("socket")
                    if sock is not None:  # linger 0: close with a reset, like a link that died
                        sock.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("HH" if sys.platform == "win32" else "ii", 1, 0))
                except OSError: pass
                w.close()
            self.writers.clear(); self.stats["cuts"] += 1
            return {"ok": True}
        if cmd == "stats":
            out = json.loads(json.dumps(self.stats))
            elapsed = max(0.001, now - self.stats["started"])
            out["elapsed"] = round(elapsed, 1); out["profile"] = self.profile_name; out["profile_source"] = PROFILE_SOURCE
            for k in ("c2s", "s2c"): out[k]["kbit_per_s"] = round(self.stats[k]["bytes"] * 8 / elapsed / 1000, 1)
            return out
        if cmd == "shutdown":
            self.done.set(); return {"ok": True}
        return {"error": "unknown cmd"}

    async def control_client(self, reader, writer):
        try:
            while True:
                line = await reader.readline()
                if not line: break
                try: reply = self.control(json.loads(line))
                except (ValueError, KeyError, TypeError) as ex: reply = {"error": str(ex)}
                writer.write((json.dumps(reply) + "\n").encode()); await writer.drain()
        finally:
            writer.close()


async def serve(listen, target, profile, control_port, info_path, seconds):
    proxy = Proxy(target, profile)
    server = await asyncio.start_server(proxy.handle, "127.0.0.1", listen)
    control = await asyncio.start_server(proxy.control_client, "127.0.0.1", control_port)
    info = {"listen": server.sockets[0].getsockname()[1], "control": control.sockets[0].getsockname()[1],
            "target": f"{target[0]}:{target[1]}", "profile": profile, "profile_source": PROFILE_SOURCE}
    if info_path: Path(info_path).write_text(json.dumps(info))
    print(json.dumps(info), flush=True)
    try:
        if seconds: await asyncio.wait_for(proxy.done.wait(), seconds)
        else: await proxy.done.wait()
    except asyncio.TimeoutError:
        pass
    server.close(); control.close()
    return proxy


def call(port, message):
    with socket.create_connection(("127.0.0.1", int(port)), timeout=5) as s:
        s.sendall((json.dumps(message) + "\n").encode())
        return json.loads(s.makefile().readline())


def selftest():
    """Echo server behind the proxy: order, delay, loss stalls, stall, cut, profile switch."""
    results = {}

    async def run():
        async def echo(r, w):
            try:
                while True:
                    d = await r.read(4096)
                    if not d: break
                    w.write(d); await w.drain()
            except (ConnectionError, OSError): pass
            w.close()
        es = await asyncio.start_server(echo, "127.0.0.1", 0)
        eport = es.sockets[0].getsockname()[1]
        proxy = Proxy(("127.0.0.1", eport), "transatlantic")
        ps = await asyncio.start_server(proxy.handle, "127.0.0.1", 0)
        pport = ps.sockets[0].getsockname()[1]
        r, w = await asyncio.open_connection("127.0.0.1", pport)
        t = time.monotonic(); w.write(b"x"); await w.drain(); await r.readexactly(1)
        rtt = time.monotonic() - t
        results["delay: transatlantic round trip is about 180 ms"] = 0.15 < rtt < 0.6
        payload = bytes(i % 251 for i in range(200000))
        w.write(payload); await w.drain()
        got = await asyncio.wait_for(r.readexactly(len(payload)), 20)
        results["bytes arrive complete and in order"] = got == payload
        proxy.control({"cmd": "set_profile", "profile": "lan"})
        proxy.control({"cmd": "stall", "dir": "both", "seconds": 1.5})
        t = time.monotonic(); w.write(b"y"); await w.drain(); await r.readexactly(1)
        results["stall holds data, then delivers it"] = 1.3 < time.monotonic() - t < 3
        hell = Direction("c2s", PROFILES["hell"], "1")
        holds = sum(hell.schedule(i * 0.01, 100)[1] for i in range(5000))
        results["hell loses about 8-20% of segments (bursty)"] = 0.05 * 5000 < holds < 0.3 * 5000
        lan = Direction("c2s", PROFILES["lan"], "1")
        results["lan loses nothing"] = sum(lan.schedule(i * 0.01, 100)[1] for i in range(5000)) == 0
        proxy.control({"cmd": "cut"})
        try:
            data = await asyncio.wait_for(r.read(10), 5)
            results["cut closes the connection"] = data == b""
        except (ConnectionError, OSError):
            results["cut closes the connection"] = True
        ps.close(); es.close()

    asyncio.run(run())
    for name, ok in results.items(): print(("PASS " if ok else "FAIL ") + name)
    print(f"{sum(results.values())} passed, {len(results) - sum(results.values())} failed")
    return 0 if all(results.values()) else 1


def main(argv):
    if argv and argv[0] == "selftest": return selftest()
    if argv and argv[0] == "call":
        print(json.dumps(call(argv[1], json.loads(argv[2])))); return 0
    opts = dict(zip(argv[0::2], argv[1::2]))
    host, port = opts["--target"].rsplit(":", 1)
    profile = opts.get("--profile", "lan")
    if profile not in PROFILES: raise SystemExit(f"unknown profile {profile}; have {sorted(PROFILES)}")
    asyncio.run(serve(int(opts.get("--listen", 0)), (host, int(port)), profile, int(opts.get("--control", 0)),
                      opts.get("--info"), float(opts.get("--seconds", 0))))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
