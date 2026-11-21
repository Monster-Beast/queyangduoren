using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Queyang.Game;

internal static class TransportSmoke
{
    private static readonly List<CoopTransport> Nodes = new List<CoopTransport>();
    private static readonly HashSet<int> Peers = new HashSet<int>();
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            foreach (var node in Nodes) node.Poll();
            if (watch.ElapsedMilliseconds > 5000) throw new Exception("Timed out");
            Thread.Sleep(1);
        }
    }

    private static void Check(bool result, string message) { if (!result) throw new Exception(message); }
    public static int Main()
    {
        // Ask the OS for a test port to avoid interfering with a real game session.
        var selector = new TcpListener(IPAddress.Loopback, 0);
        selector.Start();
        int port = ((IPEndPoint)selector.LocalEndpoint).Port;
        selector.Stop();
        Environment.SetEnvironmentVariable("ONI_COOP_BIND", "127.0.0.1:" + port);
        try
        {
            CoopTransport host = null;
            host = new CoopTransport((peer, data) => host.Send(peer, data), peer => Peers.Add(peer), peer => Peers.Remove(peer));
            Nodes.Add(host);
            host.Host(false);
            bool ready = false;
            byte[] response = null;
            var client = new CoopTransport((peer, data) => response = data, peer => ready = peer == 0, peer => ready = false);
            Nodes.Add(client);
            client.Join(host.Endpoint, false);
            Until(() => ready && Peers.Count == 1);
            var packet = new byte[65536];
            new Random(457140).NextBytes(packet);
            client.Send(0, packet);
            Until(() => response != null);
            Check(packet.SequenceEqual(response), "Maximum frame corrupted");

            // Fragment a wire header/body manually, then follow it with a malformed length.
            using (var raw = new TcpClient("127.0.0.1", port))
            {
                Until(() => Peers.Count == 2);
                var stream = raw.GetStream();
                stream.Write(new byte[] {3, 0}, 0, 2);
                host.Poll();
                stream.Write(new byte[] {0, 0, 9, 8}, 0, 4);
                host.Poll();
                stream.WriteByte(7);
                host.Poll();
                host.Poll();
                var got = new byte[7];
                int offset = 0;
                raw.ReceiveTimeout = 3000;
                while(offset < got.Length)
                {
                    int n = stream.Read(got, offset, got.Length - offset);
                    if(n == 0) throw new Exception("Premature EOF");
                    offset += n;
                }
                Check(got.SequenceEqual(new byte[] {3, 0, 0, 0, 9, 8, 7}), "Fragmented frame corrupt");
                stream.Write(new byte[] {1, 0, 1, 0}, 0, 4);
                Until(() => Peers.Count == 1);
            }
            var simultaneous = new List<TcpClient>();
            try
            {
                simultaneous.Add(new TcpClient("127.0.0.1", port));
                simultaneous.Add(new TcpClient("127.0.0.1", port));
                Until(() => Peers.Count == 3);
                using(var extra = new TcpClient("127.0.0.1", port))
                {
                    host.Poll();
                    extra.ReceiveTimeout = 3000;
                    Check(extra.GetStream().ReadByte() < 0, "Exceeded room capacity");
                }
            }
            finally { foreach(var s in simultaneous) s.Close(); }
            Until(() => Peers.Count == 1);
            response = null;
            client.Send(0, new byte[] { 42 });
            Until(() => response != null);
            Check(response.SequenceEqual(new byte[] {42}), "Malformed peer broke existing client");
            int previous = Peers.Single();
            client.Dispose();
            Until(() => Peers.Count == 0);
            using(var reconnected = new TcpClient("127.0.0.1",port))
            {
                Until(() => Peers.Count == 1);
                Check(Peers.Single() > previous, "Reused peer generation");
            }
            Console.WriteLine("PASS: loopback round trip; 65536-byte frame; fragmented header/body; invalid length isolation; room cap; disconnect/rejoin.");
            return 0;
        }
        finally { foreach (var node in Nodes) node.Dispose(); }
    }
}
