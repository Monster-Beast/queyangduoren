using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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

    public static int Run(bool native = false)
    {
        var selector = new TcpListener(IPAddress.Loopback,0);
        selector.Start(); string endpoint = selector.LocalEndpoint.ToString(); selector.Stop();
        string old = Environment.GetEnvironmentVariable("ONI_COOP_BIND");
        Environment.SetEnvironmentVariable("ONI_COOP_BIND", endpoint);
        string scratch = Path.Combine(Path.GetTempPath(),"qy-join-"+Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = new byte[10 * 1024 * 1024];
            new Random(744825).NextBytes(bytes);
            var display = native ? new byte[] {1,2,3} : bytes;
            string source = Path.Combine(scratch,"host.sav");
            if (native) { Directory.CreateDirectory(scratch); File.WriteAllBytes(source,bytes); }
            var stopwatch = Stopwatch.StartNew();
            var up = new LinkBudget(stopwatch); var down = new LinkBudget(stopwatch);
            RoomHost host = null; RoomClient client = null; int guest = 0;
            using (var server = new CoopTransport((id,b)=>host.Receive(id,b),
                id=>{ guest=id; host.Connect(id); },id=>host.Remove(id)))
            using (var peer = new CoopTransport((id,b)=>client.Receive(b),id=>client.Connected(),
                id=>throw new IOException("Unexpected disconnect in join benchmark")))
            {
                Func<Stream> open = native ? (Func<Stream>)(() => File.OpenRead(source)) : null;
                Action<string> loaded = native ? (Action<string>)(path => {
                    using (var hash=SHA256.Create())
                    using (var received=File.OpenRead(path))
                        if (!ViewPatch.Equal(hash.ComputeHash(received),hash.ComputeHash(bytes)))
                            throw new IOException("Native save file content differs.");
                    client.NativeWorldReady(); // Emulates a successful native loader in this transport-only test.
                }) : null;
                using (host = new RoomHost("benchmark",(id,b)=>down.Enqueue(b),server.Drop,
                    (x,y)=>display,(a,x,y)=>"OK",()=>stopwatch.Elapsed.TotalSeconds,open))
                using (client = new RoomClient("benchmark",up.Enqueue,native ? scratch : null,loaded))
                {
                    server.Host(false); peer.Join(server.Endpoint,false);
                    while (!client.Live && stopwatch.Elapsed.TotalSeconds<20)
                    {
                        up.Release(b=>peer.Send(0,b)); down.Release(b=>server.Send(guest,b));
                        server.Poll(); peer.Poll(); host.Tick(); Thread.Sleep(1);
                    }
                    var seconds=stopwatch.Elapsed.TotalSeconds;
                    if (!client.Live || !ViewPatch.Equal(client.View,display))
                        throw new IOException("Snapshot did not converge.");
                    Console.WriteLine("Actual TCP; synthetic 10 MiB {0}; shaped 20 Mbit/s per direction + 150 ms RTT: {1:F3} s to Live.",
                        native ? "native file" : "display baseline",seconds);
                    if (seconds>15) throw new TimeoutException("Late join exceeded 15 second budget.");
                }
                return 0;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ONI_COOP_BIND",old);
            if (Directory.Exists(scratch)) Directory.Delete(scratch,true);
        }
    }
}
