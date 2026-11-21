using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Queyang.Core;
using Queyang.Game;

internal static class LateJoin
{
    // Keep actual sockets/framing and impose a link budget at the application boundary.
    sealed class LinkBudget
    {
        readonly Queue<(double due, byte[] bytes)> packets = new Queue<(double,byte[])>();
        readonly Stopwatch clock;
        double available;
        public LinkBudget(Stopwatch clock) { this.clock = clock; }
        public void Enqueue(byte[] bytes)
        {
            available = Math.Max(clock.Elapsed.TotalSeconds, available) + (bytes.Length+4)*8.0 / 20000000;
            packets.Enqueue((available+0.075,bytes));
        }
        public void Release(Action<byte[]> send)
        {
            while (packets.Count != 0 && packets.Peek().due <= clock.Elapsed.TotalSeconds)
                send(packets.Dequeue().bytes);
        }
    }

    public static int Run()
    {
        var selector = new TcpListener(IPAddress.Loopback,0);
        selector.Start(); string endpoint = selector.LocalEndpoint.ToString(); selector.Stop();
        string old = Environment.GetEnvironmentVariable("ONI_COOP_BIND");
        Environment.SetEnvironmentVariable("ONI_COOP_BIND", endpoint);
        try
        {
            var bytes = new byte[10 * 1024 * 1024];
            new Random(744825).NextBytes(bytes);
            var stopwatch = Stopwatch.StartNew();
            var up = new LinkBudget(stopwatch); var down = new LinkBudget(stopwatch);
            RoomHost host = null; RoomClient client = null; int guest = 0;
            using (var server = new CoopTransport((id,b)=>host.Receive(id,b),
                id=>{ guest=id; host.Connect(id); },id=>host.Remove(id)))
            using (var peer = new CoopTransport((id,b)=>client.Receive(b),id=>client.Connected(),
                id=>throw new IOException("Unexpected disconnect in join benchmark")))
            {
                host = new RoomHost("benchmark",(id,b)=>down.Enqueue(b),server.Drop,
                    (x,y)=>bytes,(a,x,y)=>"OK",()=>stopwatch.Elapsed.TotalSeconds);
                client = new RoomClient("benchmark",up.Enqueue);
                server.Host(false); peer.Join(server.Endpoint,false);
                while (!client.Live && stopwatch.Elapsed.TotalSeconds<20)
                {
                    up.Release(b=>peer.Send(0,b)); down.Release(b=>server.Send(guest,b));
                    server.Poll(); peer.Poll(); host.Tick(); Thread.Sleep(1);
                }
                var seconds=stopwatch.Elapsed.TotalSeconds;
                if (!client.Live || !ViewPatch.Equal(client.View,bytes))
                    throw new IOException("Snapshot did not converge.");
                Console.WriteLine("Actual TCP; synthetic 10 MiB baseline; shaped 20 Mbit/s per direction + 150 ms RTT: {0:F3} s to Live.",seconds);
                if (seconds>15) throw new TimeoutException("Late join exceeded 15 second budget.");
                return 0;
            }
        }
        finally { Environment.SetEnvironmentVariable("ONI_COOP_BIND",old); }
    }
}
