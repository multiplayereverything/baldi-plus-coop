using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace BaldiTogether
{
    // Steam carrier for Transport: Steam networking sockets (P2P through Valve's relay, no IPs or port
    // forwarding) under the game's own Steam app. Each connection is an IPacketLink; one Transport frame is one
    // reliable Steam message, so everything above Transport is the same as on LAN.
    // The game's SteamManager initialises Steam and runs callbacks every frame; Pump() runs on the main thread.
    // Steam keeps connections alive on its own thread, so a stalled game loop doesn't end a link.
    public sealed class SteamNet : IDisposable
    {
        readonly Transport owner;
        readonly bool hosting;
        HSteamListenSocket listen = HSteamListenSocket.Invalid;
        HSteamNetPollGroup group;
        readonly Dictionary<HSteamNetConnection, SteamPeerLink> links = new Dictionary<HSteamNetConnection, SteamPeerLink>();
        readonly IntPtr[] incoming = new IntPtr[64];
        Callback<SteamNetConnectionStatusChangedCallback_t> statusCallback;
        bool disposed;
        public int Received, Sent;

        static bool bufferSet;

        public SteamNet(Transport owner, bool hosting)
        {
            this.owner = owner; this.hosting = hosting;
            if (!bufferSet)
            {
                // Steam's default send buffer is 512 KB; match the TCP links' 4 MB so a slow relay path queues
                // instead of refusing frames (a refused send drops the peer, see SteamPeerLink.Send).
                bufferSet = true;
                IntPtr value = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(value, Transport.MaxOutgoingBytes);
                    SteamNetworkingUtils.SetConfigValue(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, value);
                }
                finally { Marshal.FreeHGlobal(value); }
            }
            group = SteamNetworkingSockets.CreatePollGroup();
            statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatus);
        }

        public void Listen()
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            owner.MarkSteamHost();
        }

        public void Connect(CSteamID host)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var identity = new SteamNetworkingIdentity(); identity.SetSteamID(host);
            var connection = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
            Track(connection, false);
        }

        // Lab: a connected pair inside one process (Steam's own loopback), for the steam tour.
        public static bool Pair(SteamNet a, SteamNet b)
        {
            HSteamNetConnection first, second;
            var ia = new SteamNetworkingIdentity(); var ib = new SteamNetworkingIdentity();
            if (!SteamNetworkingSockets.CreateSocketPair(out first, out second, false, ref ia, ref ib)) return false;
            a.Attach(a.Track(first, a.hosting)); b.Attach(b.Track(second, b.hosting));
            return true;
        }

        SteamPeerLink Track(HSteamNetConnection connection, bool asHost)
        {
            var link = new SteamPeerLink(this, connection);
            links[connection] = link;
            SteamNetworkingSockets.SetConnectionPollGroup(connection, group);
            return link;
        }

        void Attach(SteamPeerLink link)
        {
            link.Id = owner.AttachExternal(link, hosting);
            if (link.Id < 0) { link.Close(); links.Remove(link.Connection); }
        }

        void OnStatus(SteamNetConnectionStatusChangedCallback_t status)
        {
            if (disposed) return;
            var connection = status.m_hConn;
            var state = status.m_info.m_eState;
            SteamPeerLink link;
            bool known = links.TryGetValue(connection, out link);
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
            {
                // a friend calling the host's listen socket
                if (!known && hosting && listen != HSteamListenSocket.Invalid && status.m_info.m_hListenSocket == listen)
                {
                    if (links.Count >= 3 || SteamNetworkingSockets.AcceptConnection(connection) != EResult.k_EResultOK)
                    { SteamNetworkingSockets.CloseConnection(connection, 0, "Room is full.", false); return; }
                    Track(connection, true);
                }
            }
            else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                if (known && link.Id < 0) Attach(link);
            }
            else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
            {
                if (known)
                {
                    links.Remove(connection);
                    if (link.Id >= 0) owner.ExternalClosed(link.Id, link);
                    else if (!hosting) owner.ExternalError("Couldn't reach the host over Steam: " + status.m_info.m_szEndDebug);
                    SteamNetworkingSockets.CloseConnection(connection, 0, null, false);
                }
            }
        }

        // Main thread, every frame: move received messages into the Transport's event queue.
        public void Pump()
        {
            if (disposed) return;
            for (int round = 0; round < 8; round++)
            {
                int count = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(group, incoming, incoming.Length);
                if (count <= 0) return;
                for (int i = 0; i < count; i++)
                {
                    var message = (SteamNetworkingMessage_t)Marshal.PtrToStructure(incoming[i], typeof(SteamNetworkingMessage_t));
                    SteamPeerLink link;
                    // A message proves the connection is up even if its Connected callback hasn't been dispatched yet
                    // (callbacks run in the game's SteamManager.Update, which can come after this pump in a frame).
                    if (links.TryGetValue(message.m_conn, out link) && link.Id < 0) Attach(link);
                    if (link != null && link.Id >= 0 && (message.m_cbSize < 1 || message.m_cbSize > Transport.MaxFrame)) owner.Drop(link.Id); // like an invalid TCP frame
                    else if (links.TryGetValue(message.m_conn, out link) && link.Id >= 0)
                    {
                        byte[] data = new byte[message.m_cbSize];
                        Marshal.Copy(message.m_pData, data, 0, data.Length);
                        Received++;
                        owner.ExternalPacket(link.Id, data);
                    }
                    SteamNetworkingMessage_t.Release(incoming[i]);
                }
            }
        }

        internal void Forget(SteamPeerLink link) { links.Remove(link.Connection); }
        // Steam posts no status callback for a connection closed on this side, so report the end to Transport here.
        internal void ClosedLocally(SteamPeerLink link) { if (link.Id >= 0) owner.ExternalClosed(link.Id, link); }
        internal void SendFailed(SteamPeerLink link) { if (link.Id >= 0) owner.Drop(link.Id); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var link in new List<SteamPeerLink>(links.Values)) link.Close();
            links.Clear();
            if (listen != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(listen);
            SteamNetworkingSockets.DestroyPollGroup(group);
            if (statusCallback != null) statusCallback.Dispose();
        }
    }

    public sealed class SteamPeerLink : IPacketLink
    {
        readonly SteamNet net;
        public readonly HSteamNetConnection Connection;
        public int Id = -1;
        bool closed;
        public SteamPeerLink(SteamNet net, HSteamNetConnection connection) { this.net = net; Connection = connection; }

        public void Send(byte[] packet)
        {
            if (closed) return;
            GCHandle pin = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                long number;
                var result = SteamNetworkingSockets.SendMessageToConnection(Connection, pin.AddrOfPinnedObject(), (uint)packet.Length, Constants.k_nSteamNetworkingSend_ReliableNoNagle, out number);
                if (result == EResult.k_EResultOK) net.Sent++;
                else net.SendFailed(this); // a lost reliable frame would desync silently: drop the peer, as TCP does
            }
            finally { pin.Free(); }
        }

        // The final frame (a rejection reason) goes out before the connection closes: linger delivers it.
        public void SendAndClose(byte[] packet) { Send(packet); Close(true); net.ClosedLocally(this); }
        public void Close() { Close(false); }
        void Close(bool linger)
        {
            if (closed) return;
            closed = true;
            net.Forget(this);
            SteamNetworkingSockets.CloseConnection(Connection, 0, null, linger);
        }
    }
}
