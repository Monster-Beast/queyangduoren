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
                if (args.Length>=1 && args[0]=="--native-replay")
                {
                    int duration=300;
                    if (args.Length>2 || (args.Length==2 && (!int.TryParse(args[1],out duration) || duration<1)))
                        throw new ArgumentException("Expected --native-replay [durationSeconds]");
                    return NativeReplay.Run(log,duration);
                }
                var recording = args.Length==1 && args[0]=="--native-record"
                    ? new NativeRecording(NativeRecording.LocalDirectory(),log) : null;
                if (args.Length!=0 && recording==null) throw new ArgumentException("Unknown peer mode.");
                RoomClient client = null;
                using (var transport = new CoopTransport((id,data) => { client.Receive(data); recording?.View(client.View); },
                    id => client.Connected(), id => { throw new IOException("Host disconnected."); }))
                using (client = new RoomClient(Environment.GetEnvironmentVariable("QY_SMOKE_FINGERPRINT"), p => transport.Send(0,p),
                    recording?.DirectoryPath, recording == null ? (Action<string>)null : file => {
                        recording.Save(file);
                        client.NativeWorldReady();
                    }))
                {
                    if (recording!=null) client.ResultReceived+=recording.Result;
                    var scenario = new GuestScenario(log,(action,x,y) => {
                        recording?.Sent(action,x,y);
                        client.Command(action,x,y);
                    });
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
                    if (recording!=null) recording.Finish(client.View, (command,x,y) =>
                    {
                        string reply = null;
                        void Receive(string value) { reply=value; }
                        client.ResultReceived += Receive;
                        try
                        {
                            client.Command(command,x,y);
                            var timeout=Stopwatch.StartNew();
                            while (reply==null && timeout.Elapsed.TotalSeconds<10) { transport.Poll(); Thread.Sleep(4); }
                            return reply ?? throw new TimeoutException("No response for "+command);
                        }
                        finally { client.ResultReceived -= Receive; }
                    });
                    log(recording==null ? "PASS transport peer observed host excavation and construction" : "PASS native recording complete");
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
                    transport.Drop, FixtureViewport, (action,x,y) => {
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

        // Fixed, synthetic colony for observing UI layers. All objects keep their
        // world coordinates and target IDs when a guest pans or joins again.
        private static byte[] FixtureViewport(int x,int y)
        {
            var v = new ColonyView { X=Math.Min(140, Math.Max(100,x)), Y=Math.Min(125, Math.Max(100,y)),
                Cycle=4, CycleFraction=.3f, Speed=1 };
            for (int i=0; i<v.Cells.Length; i++)
            {
                int cx=v.X+i%ColonyView.Width, cy=v.Y+i/ColonyView.Width;
                var cell=new ColonyCell { Flags=65, Element="Oxygen", R=117, G=168, B=203, Mass=2, Temperature=300 };
                if (cy<105 || cx<=102 && cy<111)
                    cell=new ColonyCell { Flags=3, Element="Sandstone", R=183, G=139, B=95, Mass=700, Temperature=295 };
                else if (cx>=106 && cx<=110 && cy<107)
                    cell=new ColonyCell { Flags=5, Element="Water", R=53, G=132, B=205, Mass=650, Temperature=291 };
                if (cx==113 && cy>=105 && cy<113)
                { cell.Flags |= 16; cell.Building="梯子"; cell.BuildingStyle=1; cell.TargetId=cy; }
                if (cy==105 && cx>=115 && cx<121)
                { cell.Flags |= 32; cell.Building="砖块"; cell.BuildingStyle=2; cell.Progress=45; cell.TargetId=1000+cx; }
                v.Cells[i]=cell;
            }
            if (114>=v.X && 114<v.X+ColonyView.Width && 106>=v.Y && 106<v.Y+ColonyView.Height)
                v.Dupes=new[] { new ColonyDupe { Id=1, X=114, Y=106, SubX=128, Name="测试复制人", Activity="搬运" } };
            return v.Encode();
        }
    }
}
