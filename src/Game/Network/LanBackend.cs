using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Queyang.Game
{
    internal sealed class LanBackend : ConnectionBackend
    {
        internal const int DefaultPort = 24714;
        private sealed class Peer
        {
            internal Socket Socket;
            internal readonly byte[] Header = new byte[4];
            internal byte[] Body;
            internal int Received;
            internal long LastRead;
            internal readonly Queue<byte[]> Outgoing = new Queue<byte[]>();
            internal int SendOffset, QueuedBytes;
        }

        private TcpListener listener;
        private Socket connecting;
        private IAsyncResult attempt;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long connectTime;
        private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        private int nextPeer = 1;

        internal LanBackend(CoopTransport owner) : base(owner) { }

        internal override void Start(bool hosting, string endpoint)
        {
            if (hosting) endpoint = Environment.GetEnvironmentVariable("ONI_COOP_BIND");
            var address = ParseEndpoint(endpoint);
            Owner.Endpoint = address.ToString();
            if (hosting)
            {
                listener = new TcpListener(address);
                listener.Start(CoopTransport.MaxGuests);
            }
            else
            {
                connecting = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                connectTime = clock.ElapsedMilliseconds;
                attempt = connecting.BeginConnect(address, null, null);
            }
        }

        private static IPEndPoint ParseEndpoint(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new IPEndPoint(IPAddress.Loopback, DefaultPort);
            var parts = text.Trim().Split(':');
            var port = DefaultPort;
            IPAddress address;
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port < 1 || port > 65535)))
                throw new ArgumentException("Expected an IPv4 address and optional port, e.g. 127.0.0.1:24714.");
            return new IPEndPoint(address, port);
        }

        internal override void Poll()
        {
            if (connecting != null)
            {
                if (attempt.IsCompleted)
                {
                    try { connecting.EndConnect(attempt); }
                    catch (SocketException ex) { FailConnect(ex.Message); return; }
                    var socket = connecting;
                    connecting = null;
                    Register(0, socket);
                }
                else if (clock.ElapsedMilliseconds - connectTime > 15000)
                    FailConnect("Connection timed out.");
            }
            // Bound accepts, reads and writes per frame, including when the room is full.
            for (int i = 0; i < 4 && listener != null && listener.Pending(); i++)
            {
                var socket = listener.AcceptSocket();
                if (peers.Count == CoopTransport.MaxGuests) socket.Dispose();
                else Register(checked(nextPeer++), socket);
            }
            foreach (var id in new List<int>(peers.Keys))
            {
                Peer peer;
                if (!peers.TryGetValue(id, out peer)) continue;
                try
                {
                    Write(peer);
                    Read(id, peer);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.WouldBlock) FailPeer(id, ex.Message);
                }
                catch (IOException ex) { FailPeer(id, ex.Message); }
            }
        }

        private void Register(int id, Socket socket)
        {
            socket.Blocking = false;
            socket.NoDelay = true;
            peers.Add(id, new Peer { Socket = socket });
            Owner.Connected(id);
        }

        private void FailConnect(string error)
        {
            connecting.Dispose();
            connecting = null;
            Owner.LastError = error;
            Owner.Disconnected(0);
        }

        private void FailPeer(int id, string error)
        {
            Owner.LastError = error;
            Drop(id);
        }

        private void Read(int id, Peer peer)
        {
            var now = clock.ElapsedMilliseconds;
            if ((peer.Received > 0 || peer.Body != null) && now - peer.LastRead > 15000)
                throw new IOException("Partial frame timed out.");
            for (int step = 0; step < 16 && peers.ContainsKey(id) && peer.Socket.Poll(0, SelectMode.SelectRead); step++)
            {
                var buffer = peer.Body ?? peer.Header;
                int count = peer.Socket.Receive(buffer, peer.Received, buffer.Length - peer.Received, SocketFlags.None);
                if (count == 0) throw new EndOfStreamException("Peer closed the connection.");
                peer.LastRead = now;
                peer.Received += count;
                if (peer.Received != buffer.Length) continue;
                peer.Received = 0;
                if (peer.Body == null)
                {
                    int length = buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
                    if (length <= 0 || length > CoopTransport.MaxFrame) throw new IOException("Invalid packet length.");
                    peer.Body = new byte[length];
                }
                else
                {
                    peer.Body = null;
                    Owner.Message(id, buffer);
                }
            }
        }

        internal override void Send(int id, byte[] packet)
        {
            Peer peer;
            if (!peers.TryGetValue(id, out peer)) return;
            int length = packet.Length;
            if (peer.QueuedBytes + length + 4 > CoopTransport.QueueBudget || peer.Outgoing.Count >= 4096)
            {
                FailPeer(id, "Outgoing queue limit exceeded.");
                return;
            }
            var frame = new byte[length + 4];
            frame[0] = (byte)length;
            frame[1] = (byte)(length >> 8);
            frame[2] = (byte)(length >> 16);
            frame[3] = (byte)(length >> 24);
            Buffer.BlockCopy(packet, 0, frame, 4, length);
            peer.Outgoing.Enqueue(frame);
            peer.QueuedBytes += frame.Length;
        }

        private static void Write(Peer peer)
        {
            for (int i = 0; i < 8 && peer.Outgoing.Count > 0 && peer.Socket.Poll(0, SelectMode.SelectWrite); i++)
            {
                var packet = peer.Outgoing.Peek();
                int sent = peer.Socket.Send(packet, peer.SendOffset, packet.Length - peer.SendOffset, SocketFlags.None);
                if (sent == 0) throw new EndOfStreamException("Peer closed during write.");
                peer.SendOffset += sent;
                if (peer.SendOffset != packet.Length) continue;
                peer.QueuedBytes -= packet.Length;
                peer.SendOffset = 0;
                peer.Outgoing.Dequeue();
            }
        }

        internal override void Drop(int id)
        {
            Peer peer;
            if (!peers.TryGetValue(id, out peer)) return;
            peers.Remove(id);
            peer.Socket.Dispose();
            Owner.Disconnected(id);
        }

        public override void Dispose()
        {
            listener?.Stop();
            listener = null;
            connecting?.Dispose();
            connecting = null;
            foreach (var peer in peers.Values) peer.Socket.Dispose();
            peers.Clear();
        }
    }
}
