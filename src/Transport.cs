using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BaldiTogether
{
    public interface IPacketLink { void Send(byte[] packet); void SendAndClose(byte[] packet); void Close(); }

    public sealed class NetworkEvent
    {
        public int Peer;
        public string Kind;
        public byte[] Data;
        public string Message;
    }

    // No Unity calls are permitted on the socket threads.
    public sealed class Transport : IDisposable
    {
        public const int MaxFrame = 65536;
        // Link liveness belongs to the socket threads, not the game loop: a link that has sent nothing for
        // KeepAliveMs sends a keepalive frame (length 1, byte 0), which the reader swallows. A peer whose game
        // loop stalls (loading, a hitch, Alt+Tab) stays connected; a dead process or network still times out.
        public static int KeepAliveMs = 2000, LinkTimeoutMs = 15000;
        public static bool KeepAlive = true; // lab switch -bt-lab-no-keepalive turns it off (the 0.8.x behaviour)
        // Packets waiting for the game loop are capped by size, not count, so a stalled loop can queue a
        // minute of poses from three clients while a flooding peer is still cut off.
        public const int MaxQueuedBytes = 8 * 1024 * 1024;
        // Per link, packets the socket hasn't taken yet (a slow or briefly stalled internet link); 0.8.x dropped at 128 packets.
        public const int MaxOutgoingBytes = 4 * 1024 * 1024;
        long queuedBytes;
        readonly ConcurrentQueue<NetworkEvent> events = new ConcurrentQueue<NetworkEvent>();
        readonly Dictionary<int, IPacketLink> links = new Dictionary<int, IPacketLink>();
        readonly object gate = new object();
        TcpListener listener;
        volatile bool disposed;
        public bool Hosting { get; private set; }
        public int Port { get; private set; }
        // Steam (SteamLink.cs): friends arrive as Steam networking connections instead of sockets; no host migration.
        public bool Steam { get; private set; }

        void Emit(int peer, string kind, byte[] data, string message)
        {
            if (disposed) return;
            // Only data packets count against the cap; dropping a "disconnected" event would leave a ghost peer.
            if (kind == "packet" && Interlocked.Add(ref queuedBytes, data.Length) > MaxQueuedBytes) { Interlocked.Add(ref queuedBytes, -data.Length); Drop(peer); return; }
            events.Enqueue(new NetworkEvent { Peer = peer, Kind = kind, Data = data, Message = message });
        }

        public bool Poll(out NetworkEvent e)
        {
            if (!events.TryDequeue(out e)) return false;
            if (e.Kind == "packet") Interlocked.Add(ref queuedBytes, -e.Data.Length);
            return true;
        }
        public long QueuedBytes { get { return Interlocked.Read(ref queuedBytes); } }

        public void Host(int port, bool loopback)
        {
            if (listener != null || disposed) throw new InvalidOperationException("Transport already used.");
            Hosting = true;
            listener = new TcpListener(loopback ? IPAddress.Loopback : IPAddress.Any, port);
            listener.Start(4);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            StartThread(delegate
            {
                while (!disposed)
                {
                    try
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        lock (gate)
                        {
                            int id = 1;
                            while (links.ContainsKey(id)) id++;
                            if (disposed || id > 3) { client.Close(); continue; }
                            Attach(id, client);
                        }
                    }
                    catch (Exception ex) { if (!disposed) Emit(-1, "error", null, ex.Message); break; }
                }
            });
        }

        // Another carrier (Steam) hands over each link, then reports its packets and its end.
        // AttachExternal picks the next free peer id like the TCP listener (host: 1-3) or 0 (client); -1 when full.
        public int AttachExternal(IPacketLink link, bool hosting)
        {
            lock (gate)
            {
                if (disposed) return -1;
                Steam = true; if (hosting) Hosting = true;
                int id = 0;
                if (hosting) { id = 1; while (links.ContainsKey(id)) id++; if (id > 3) return -1; }
                else if (links.ContainsKey(0)) return -1;
                links.Add(id, link);
                Emit(id, "connected", null, null);
                return id;
            }
        }
        public void MarkSteamHost() { lock (gate) { Steam = true; Hosting = true; } }
        public void ExternalPacket(int id, byte[] data) { if (data != null && data.Length > 0 && data.Length <= MaxFrame) Emit(id, "packet", data, null); }
        public void ExternalClosed(int id, IPacketLink link) { Drop(id, link); }
        public void ExternalError(string message) { Emit(0, "error", null, message); }

        public void Join(string address, int port)
        {
            if (disposed) throw new ObjectDisposedException("Transport");
            StartThread(delegate
            {
                TcpClient client = new TcpClient();
                try
                {
                    IAsyncResult attempt = client.BeginConnect(address, port, null, null);
                    using (attempt.AsyncWaitHandle)
                    {
                        if (!attempt.AsyncWaitHandle.WaitOne(5000)) throw new IOException("Connection timed out.");
                        client.EndConnect(attempt);
                    }
                    lock (gate)
                    {
                        if (disposed) { client.Close(); return; }
                        Attach(0, client);
                    }
                }
                catch (Exception ex) { client.Close(); Emit(0, "error", null, ex.Message); }
            });
        }

        void Attach(int id, TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = LinkTimeoutMs;
            client.SendTimeout = 5000;
            Link link = new Link(this, id, client);
            links.Add(id, link);
            Emit(id, "connected", null, null);
            link.Start();
        }

        public void Send(int id, byte[] packet)
        {
            if (packet == null || packet.Length == 0 || packet.Length > MaxFrame) throw new ArgumentException("Invalid packet size.");
            lock (gate) { IPacketLink link; if (links.TryGetValue(id, out link)) link.Send(packet); }
        }

        public void Drop(int id)
        { Drop(id, null); }

        public void SendAndClose(int id, byte[] packet)
        {
            if (packet == null || packet.Length < 1 || packet.Length > MaxFrame) throw new ArgumentException("Invalid final packet size.");
            lock (gate) { IPacketLink link; if (links.TryGetValue(id, out link)) link.SendAndClose(packet); }
        }

        void Drop(int id, IPacketLink expected)
        {
            lock (gate)
            {
                IPacketLink link;
                if (!links.TryGetValue(id, out link)) return;
                if (expected != null && link != expected) return;
                links.Remove(id);
                link.Close();
                Emit(id, "disconnected", null, null);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                if (listener != null) listener.Stop();
                foreach (IPacketLink link in links.Values) link.Close();
                links.Clear();
                NetworkEvent unused; while (events.TryDequeue(out unused)) { }
            }
        }

        static void StartThread(ThreadStart action) { new Thread(action) { IsBackground = true, Name = "BaldiTogether network" }.Start(); }

        sealed class Link : IPacketLink
        {
            readonly Transport owner;
            readonly int id;
            readonly TcpClient client;
            readonly NetworkStream stream;
            readonly ConcurrentQueue<byte[]> outgoing = new ConcurrentQueue<byte[]>();
            readonly AutoResetEvent wake = new AutoResetEvent(false);
            volatile bool closed;
            volatile bool closeAfterSend;
            long outgoingBytes;
            volatile bool heard; // keepalives start after the peer's first frame, so a mismatched old build still reads its Reject
            public Link(Transport owner, int id, TcpClient client)
            { this.owner = owner; this.id = id; this.client = client; stream = client.GetStream(); }
            public void Start() { StartThread(Read); StartThread(Write); }
            public void Close() { closed = true; client.Close(); wake.Set(); }
            public void Send(byte[] packet)
            {
                if (closed || closeAfterSend) return;
                if (Interlocked.Add(ref outgoingBytes, packet.Length) > MaxOutgoingBytes) { owner.Drop(id); return; }
                outgoing.Enqueue(packet); wake.Set();
            }
            public void SendAndClose(byte[] packet) { Send(packet); closeAfterSend = true; wake.Set(); }
            void Read()
            {
                try
                {
                    while (!closed)
                    {
                        byte[] header = ReadExactly(stream, 4);
                        int size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
                        if (size < 1 || size > MaxFrame) throw new IOException("Invalid frame length.");
                        byte[] data = ReadExactly(stream, size);
                        heard = true;
                        if (size == 1 && data[0] == 0) continue; // keepalive
                        owner.Emit(id, "packet", data, null);
                    }
                }
                catch (Exception) { }
                finally { owner.Drop(id, this); }
            }
            void Write()
            {
                try
                {
                    int lastWrite = Environment.TickCount;
                    while (!closed)
                    {
                        byte[] packet;
                        if (!outgoing.TryDequeue(out packet))
                        {
                            if (closeAfterSend) break;
                            if (KeepAlive && heard && unchecked(Environment.TickCount - lastWrite) >= KeepAliveMs)
                            { stream.Write(KeepAliveFrame, 0, KeepAliveFrame.Length); lastWrite = Environment.TickCount; }
                            wake.WaitOne(Math.Min(1000, Math.Max(50, KeepAliveMs / 2)));
                            continue;
                        }
                        lastWrite = Environment.TickCount;
                        Interlocked.Add(ref outgoingBytes, -packet.Length);
                        int size = packet.Length;
                        byte[] header = { (byte)size, (byte)(size >> 8), (byte)(size >> 16), (byte)(size >> 24) };
                        stream.Write(header, 0, 4); stream.Write(packet, 0, size);
                    }
                }
                catch (Exception) { }
                finally { owner.Drop(id, this); wake.Dispose(); }
            }
        }

        static readonly byte[] KeepAliveFrame = { 1, 0, 0, 0, 0 };

        public static byte[] ReadExactly(Stream stream, int size)
        {
            byte[] data = new byte[size];
            int offset = 0;
            while (offset < size)
            {
                int read = stream.Read(data, offset, size - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return data;
        }
    }

    public enum Op : byte { Hello = 1, Welcome, Roster, Start, Ready, ReadyAck, Pose, World, Interact, Leave, Ping, End, Begin, Pong, Reject, Notice, TeamPing, PickupRequest, PickupGrant, PickupAck, CatalogRequest, SupplyCatalog, HideRequest, HideState, ShopRequest, ShopGrant, ShopAck, PlayerStatus = 40, NpcCommand = 41, NpcReply = 42, PrincipalState = 43, PrincipalDetention = 44,RoundState=45,ElevatorRequest=46,MigrationPlan=50,MigrationResume=51,MigrationHello=52,VendingRequest=60,VendingReply=61,VendingAck=62,ResultsState=63,PlayerHud=64,BeansToClient=70,BeansToHost=71,CraftersToClient=72,CraftersToHost=73,PompToClient=74,PompToHost=75,TheTestToClient=76,TheTestToHost=77,ReflexToClient=78,ReflexToHost=79,ChalklesToClient=80,ChalklesToHost=81,ItemsToClient=82,ItemsToHost=83 }

    public static class Wire
    {
        public const int Version = 15; // 14: keepalive frames; 15: time-limit challenge in the run state
        public static byte[] Packet(Op op, Action<BinaryWriter> write)
        {
            using (MemoryStream memory = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(memory))
            {
                writer.Write((byte)op); if (write != null) write(writer);
                if (memory.Length > Transport.MaxFrame) throw new IOException("Packet too large.");
                return memory.ToArray();
            }
        }
        public static string Text(BinaryReader reader, int max)
        {
            // Explicit byte lengths avoid BinaryReader.ReadString allocating from an attacker supplied prefix.
            int count = reader.ReadUInt16();
            if (count > max * 4 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new IOException("Invalid text length.");
            string value = System.Text.Encoding.UTF8.GetString(Transport.ReadExactly(reader.BaseStream, count));
            if (value.Length > max) throw new IOException("Text too long.");
            return value;
        }
        public static void Text(BinaryWriter writer, string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > 4096) throw new IOException("Text too long.");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        public static float Number(BinaryReader reader)
        {
            float value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 100000) throw new IOException("Invalid coordinate.");
            return value;
        }
    }
}
