using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using BaldiTogether;

static class TransportTests
{
    // Link liveness with short timeouts: 1.5 s read timeout, keepalive every 0.3 s.
    // BT_MUTANT=no-keepalive turns keepalives off; the idle-link check must then fail (test.ps1 runs it).
    static void LinkChecks()
    {
        Transport.LinkTimeoutMs = 1500; Transport.KeepAliveMs = 300;
        if (Environment.GetEnvironmentVariable("BT_MUTANT") == "no-keepalive") Transport.KeepAlive = false;
        try
        {
            using (Transport host = new Transport())
            using (Transport client = new Transport())
            {
                host.Host(0, true); client.Join("127.0.0.1", host.Port);
                Wait(client, "connected", 3000); int id = Wait(host, "connected", 3000).Peer;
                client.Send(0, new byte[] { 1 }); Wait(host, "packet", 3000);
                host.Send(id, new byte[] { 2 }); Wait(client, "packet", 3000);
                // Both game loops "stall": nothing is sent for well past the read timeout.
                DateTime end = DateTime.UtcNow.AddMilliseconds(4000); NetworkEvent e; bool quiet = true;
                while (DateTime.UtcNow < end) { while (host.Poll(out e) || client.Poll(out e)) quiet = false; Thread.Sleep(20); }
                Check(quiet, "idle link survives past the read timeout, keepalives never surface as packets");
                client.Send(0, new byte[] { 3 });
                Check(Wait(host, "packet", 3000).Data[0] == 3, "link still carries packets after the stall");
            }
            using (Transport host = new Transport())
            {
                host.Host(0, true);
                using (TcpClient raw = new TcpClient("127.0.0.1", host.Port))
                {
                    Wait(host, "connected", 3000);
                    raw.GetStream().Write(new byte[] { 1, 0, 0, 0, 5 }, 0, 5);
                    Wait(host, "packet", 3000);
                    DateTime start = DateTime.UtcNow;
                    Wait(host, "disconnected", 6000); // the raw peer never sends keepalives: a dead process
                    Check((DateTime.UtcNow - start).TotalMilliseconds < 4000, "a peer that goes silent still times out");
                }
            }
            using (Transport host = new Transport())
            using (Transport client = new Transport())
            {
                host.Host(0, true); client.Join("127.0.0.1", host.Port);
                Wait(client, "connected", 3000); Wait(host, "connected", 3000);
                // 0.8.x dropped a peer at 512 queued packets: about 25 s of one client's poses during a host stall.
                for (int i = 0; i < 1500; i++) client.Send(0, new byte[] { 7, (byte)i, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
                Thread.Sleep(1500);
                int packets = 0; NetworkEvent e; bool dropped = false;
                while (host.Poll(out e)) { if (e.Kind == "packet") packets++; if (e.Kind == "disconnected") dropped = true; }
                Check(packets == 1500 && !dropped && host.QueuedBytes == 0, "1500 queued poses wait for a stalled game loop");
            }
        }
        finally { Transport.LinkTimeoutMs = 15000; Transport.KeepAliveMs = 2000; Transport.KeepAlive = true; }
    }

    static int passed;
    static void Check(bool result, string name) { if (!result) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static NetworkEvent Wait(Transport transport, string kind, int timeout)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeout);
        NetworkEvent e;
        while (DateTime.UtcNow < end)
        { while (transport.Poll(out e)) if (e.Kind == kind) return e; Thread.Sleep(5); }
        throw new Exception("Timed out waiting for " + kind);
    }
    static void Main(string[] args)
    {
        using (Transport host = new Transport())
        using (Transport a = new Transport())
        using (Transport b = new Transport())
        {
            host.Host(0, true);
            a.Join("127.0.0.1", host.Port); b.Join("127.0.0.1", host.Port);
            Wait(a, "connected", 3000); Wait(b, "connected", 3000);
            NetworkEvent first = Wait(host, "connected", 3000), second = Wait(host, "connected", 3000);
            Check(first.Peer != second.Peer, "two clients get distinct peer IDs");
            byte[] payload = Enumerable.Range(0, Transport.MaxFrame).Select(i => (byte)(i % 251)).ToArray();
            a.Send(0, payload);
            NetworkEvent received = Wait(host, "packet", 3000);
            Check(payload.SequenceEqual(received.Data), "maximum-size frame round trip");
            host.Send(received.Peer, new byte[] { 42 });
            Check(Wait(a, "packet", 3000).Data[0] == 42, "host routes reply to its actual sender");
            a.Dispose(); Wait(host, "disconnected", 3000);
            b.Send(0, new byte[] { 99 });
            Check(Wait(host, "packet", 3000).Data[0] == 99, "one disconnect leaves the other client working");
            using (TcpClient raw = new TcpClient("127.0.0.1", host.Port))
            {
                Wait(host, "connected", 3000);
                byte[] fragments = { 3,0,0,0,7,8,9 };
                foreach (byte part in fragments) { raw.GetStream().WriteByte(part); Thread.Sleep(5); }
                Check(Wait(host, "packet", 3000).Data.SequenceEqual(new byte[] { 7,8,9 }), "fragmented TCP header and body reassemble");
                raw.GetStream().Write(new byte[] { 1,0,1,0 }, 0, 4);
                Wait(host, "disconnected", 3000);
                Check(true, "oversize frame disconnects only its sender");
            }
            using (TcpClient raw = new TcpClient("127.0.0.1", host.Port))
            {
                Wait(host, "connected", 3000);
                raw.GetStream().Write(new byte[] { 0,0,0,0 }, 0, 4);
                Wait(host, "disconnected", 3000);
                Check(true, "zero-length frame rejected");
            }
            b.Send(0, new byte[] { 100 });
            Check(Wait(host, "packet", 3000).Data[0] == 100, "healthy peer survives malformed traffic");
            host.Dispose(); Wait(b, "disconnected", 3000);
            Check(true, "host disposal closes connected sockets");
        }
        using (MemoryStream memory = new MemoryStream())
        {
            BinaryWriter writer = new BinaryWriter(memory); Wire.Text(writer, "Player \u05d0"); memory.Position = 0;
            Check(Wire.Text(new BinaryReader(memory), 24) == "Player \u05d0", "UTF-8 names round trip");
        }
        bool rejected = false;
        try { using (var memory = new MemoryStream(new byte[] { 255,255 })) Wire.Text(new BinaryReader(memory), 24); }
        catch (IOException) { rejected = true; }
        Check(rejected, "untrusted string length checked before allocation");
        rejected = false;
        try { using (var memory = new MemoryStream(BitConverter.GetBytes(float.NaN))) Wire.Number(new BinaryReader(memory)); }
        catch (IOException) { rejected = true; }
        Check(rejected, "NaN coordinates rejected");
        using (Transport host = new Transport())
        {
            host.Host(0, true);
            using (var a = new TcpClient("127.0.0.1", host.Port))
            using (var b = new TcpClient("127.0.0.1", host.Port))
            using (var c = new TcpClient("127.0.0.1", host.Port))
            {
                Wait(host, "connected", 3000); Wait(host, "connected", 3000); Wait(host, "connected", 3000);
                using (var extra = new TcpClient("127.0.0.1", host.Port))
                {
                    extra.ReceiveTimeout = 3000;
                    Check(extra.GetStream().ReadByte() == -1, "room caps at host plus three clients");
                }
            }
        }
        using (Transport host = new Transport())
        {
            host.Host(0, true);
            for (int i = 0; i < 20; i++)
            {
                using (Transport client = new Transport())
                {
                    client.Join("127.0.0.1", host.Port); Wait(client, "connected", 3000);
                    int id = Wait(host, "connected", 3000).Peer;
                    client.Send(0, new byte[] { 50 }); Wait(host, "packet", 3000);
                    host.Drop(id); Wait(host, "disconnected", 3000);
                }
            }
            Check(true, "rapid reconnect does not let old socket threads close a reused peer ID");
        }
        using (Transport host = new Transport())
        using (TcpClient client = new TcpClient())
        {
            host.Host(0, true); client.Connect("127.0.0.1", host.Port); client.ReceiveTimeout = 3000;
            int id = Wait(host, "connected", 3000).Peer;
            byte[] reason = Wire.Packet(Op.Reject, w => Wire.Text(w, "Incorrect room code."));
            host.SendAndClose(id, reason);
            byte[] header = Transport.ReadExactly(client.GetStream(), 4);
            int size = BitConverter.ToInt32(header, 0);
            Check(Transport.ReadExactly(client.GetStream(), size).SequenceEqual(reason), "final rejection reason arrives before socket closes");
            Check(client.GetStream().ReadByte() == -1, "graceful rejection closes after final frame");
        }
        LinkChecks();
        var claims = new SupplyClaims(); int token, ignored; string key;
        Check(claims.TryClaim("apple", 1, out token) && !claims.TryClaim("apple", 2, out ignored), "simultaneous pickup requests have one winner");
        Check(!claims.TryClaim("soda", 1, out ignored), "player cannot hold two unacknowledged pickup grants");
        Check(!claims.Resolve(token, 2, true, out key) && claims.IsClaimed("apple"), "another player cannot settle a pickup grant");
        Check(claims.Resolve(token, 1, true, out key) && key == "apple" && !claims.Resolve(token, 1, true, out key), "pickup acknowledgement is idempotent");
        Check(!claims.TryClaim("apple", 2, out ignored), "committed pickup cannot be granted twice");
        Check(claims.TryClaim("soda", 1, out token) && claims.Resolve(token, 1, false, out key) && !claims.IsClaimed("soda") && claims.TryClaim("soda", 2, out ignored), "declined pickup is returned and may be claimed by another player");
        if (args.Length > 0) File.WriteAllText(args[0], "{\"passed\":" + passed + ",\"failed\":0,\"scope\":\"transport-wire-and-shared-supplies\"}");
        Console.WriteLine("Passed " + passed + " checks.");
    }
}
