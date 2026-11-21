using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Queyang.Core;
using Queyang.Game;

namespace Queyang.Integration
{
    internal static class PeerProgram
    {
        private static int Main(string[] args)
        {
            string report = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
            Action<string> log = line => File.AppendAllText(report,line + Environment.NewLine);
            try
            {
                if (args.Length >= 1 && args[0] == "--serve-readonly")
                {
                    int seconds = 300;
                    if (args.Length > 2 || args.Length == 2 && (!int.TryParse(args[1], out seconds) || seconds < 1))
                        throw new ArgumentException("Expected --serve-readonly [durationSeconds]");
                    return ServeReplica(log, seconds);
                }
                if (args.Length == 2 && args[0] == "--soak")
                {
                    SoakPeers.Run(log,int.Parse(args[1],System.Globalization.CultureInfo.InvariantCulture));
                    log("PASS real host with three concurrent TCP test peers");
                    return 0;
                }
                RoomClient client = null;
                using (var transport = new CoopTransport((id,data) => client.Receive(data),
                    id => client.Connected(), id => { throw new IOException("Host disconnected."); }))
                {
                    client = new RoomClient(Environment.GetEnvironmentVariable("QY_SMOKE_FINGERPRINT"), p => transport.Send(0,p));
                    var scenario = new GuestScenario(log,(action,x,y) => client.Command(action,x,y));
                    string previous = "";
                    transport.Join(Environment.GetEnvironmentVariable("QY_SMOKE_ENDPOINT"),false);
                    var timer = Stopwatch.StartNew();
                    long last = 0, ping = 0;
                    while (!scenario.Done && timer.Elapsed.TotalMinutes < 5)
                    {
                        transport.Poll();
                        if (previous != client.Status) { previous=client.Status; log("STATUS " + previous); }
                        if (client.Rejected) throw new IOException(client.Status);
                        if (client.Live && timer.ElapsedMilliseconds-last>100)
                        { last=timer.ElapsedMilliseconds; scenario.Update(client.View,client.Status); }
                        if (client.Live && timer.ElapsedMilliseconds-ping > 2000)
                        { ping=timer.ElapsedMilliseconds; client.Ping(ping); }
                        Thread.Sleep(4);
                    }
                    if (!scenario.Done) throw new TimeoutException("Orders did not finish.");
                    log("PASS transport peer observed host excavation and construction");
                }
                return 0;
            }
            catch (Exception e) { log("FAIL " + e); return 1; }
        }

        // Only used to exercise the actual Unity guest on a machine allowing one game process.
        private static int ServeReplica(Action<string> log, int seconds)
        {
            RoomHost room = null;
            var clock = Stopwatch.StartNew();
            using (var transport = new CoopTransport((id,p) => room.Receive(id,p),
                id => { room.Connect(id); log("READONLY_FIXTURE_CONNECTED " + id); },
                id => { room.Remove(id); log("READONLY_FIXTURE_DISCONNECTED " + id); }))
            {
                room = new RoomHost(Environment.GetEnvironmentVariable("QY_SMOKE_FINGERPRINT"), transport.Send,
                    transport.Drop, (x,y) => {
                        var v = new ColonyView { X=Math.Max(100,x), Y=Math.Max(100,y) };
                        for (int i=0;i<v.Cells.Length;i++) v.Cells[i]=new ColonyCell {
                            Element="Oxygen", Flags=65, Mass=2f, Temperature=300f
                        };
                        return v.Encode();
                    }, (action,x,y) => {
                        log("READONLY_FIXTURE_COMMAND " + action + " " + x + "," + y);
                        return action=="inspect" ? "OK Test inspect" : "ERROR Unsupported fixture command";
                    }, () => clock.Elapsed.TotalSeconds);
                transport.Host(false);
                log("READONLY_FIXTURE_READY");
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    transport.Poll();
                    room.Tick();
                    Thread.Sleep(4);
                }
            }
            return 0;
        }
    }
}
