using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Queyang.Core;
using Queyang.Game;

namespace Queyang.Integration
{
    internal static class SoakPeers
    {
        sealed class Guest : IDisposable
        {
            internal readonly CoopTransport Link;
            internal readonly RoomClient Client;
            internal readonly List<double> Latencies = new List<double>();
            internal long Bytes, Frames, Applied;
            internal ColonyView View;
            internal int OriginX, OriginY, Inspected;
            readonly string temp = Path.Combine(Path.GetTempPath(),"qy-soak-" + Guid.NewGuid().ToString("N"));
            double nextPeripheral;
            internal int SideViews;
            byte[] last;
            public Guest(string fingerprint,string endpoint)
            {
                Link=new CoopTransport((_,b)=>{
                    Frames++; Bytes+=b.Length; Client.Receive(b);
                },_=>Client.Connected(),_=>throw new IOException("Unexpected peer disconnect"));
                Client=new RoomClient(fingerprint,b=>Link.Send(0,b),temp,file => {
                    File.Delete(file);
                    Client.NativeWorldReady();
                });
                Client.PeripheralReceived += b => {
                    if (!ColonyView.Decode(b).Native) throw new InvalidDataException("Expected native peripheral viewport");
                    SideViews++;
                };
                Client.CommandLatencyMs+=ms=>Latencies.Add(ms);
                Client.ResultReceived+=_=>Inspected++;
                Link.Join(endpoint,false);
            }
            public void Poll()
            {
                Link.Poll();
                if(Client.Rejected) throw new IOException(Client.Status);
                if(Client.Live && !ReferenceEquals(last,Client.View))
                {
                    last=Client.View;
                    View=ColonyView.Decode(last);
                    if(Applied++==0) { OriginX=View.X; OriginY=View.Y; }
                }
                double now = (double)Stopwatch.GetTimestamp()/Stopwatch.Frequency;
                if (Client.Live && now >= nextPeripheral)
                {
                    nextPeripheral = now+.25;
                    Client.RequestPeripheral(OriginX+24,OriginY);
                }
            }
            public void Dispose()
            {
                Link.Dispose(); Client.Dispose();
                if (Directory.Exists(temp)) Directory.Delete(temp);
            }
        }

        public static void Run(Action<string> log,int seconds)
        {
            if(seconds<15 || seconds>12*3600) throw new ArgumentOutOfRangeException(nameof(seconds));
            var fingerprint=Environment.GetEnvironmentVariable("QY_SMOKE_FINGERPRINT");
            var endpoint=Environment.GetEnvironmentVariable("QY_SMOKE_ENDPOINT");
            var guests=new Guest[3];
            var clock=Stopwatch.StartNew();
            double started=-1, lastReport=0, nextInspection=0, nextSwap=0, nextPing=0;
            int joined=0;
            var previousSamples=new long[3];
            double rejoinAt=-1;
            try
            {
                for(int i=0;i<3;i++) guests[i]=new Guest(fingerprint,endpoint);
                while(true)
                {
                    var now=clock.Elapsed.TotalSeconds;
                    foreach(var guest in guests) guest?.Poll();
                    if(started<0)
                    {
                        if(guests.All(g=>g.Client.Live))
                        {
                            started=now; nextSwap=now+10; lastReport=now;
                            nextInspection=now+1;
                            log("SOAK three concurrent guests Live, target seconds="+seconds);
                        }
                        else if(now>60) throw new TimeoutException("Three guests did not reach Live");
                    }
                    if(started>=0)
                    {
                        if(now>=started+seconds && joined>0 && guests.All(g=>g!=null && g.Client.Live && g.Inspected>0 && g.Applied>1))
                            break;
                        if(now>=started+seconds+30) throw new TimeoutException("Incomplete reconnect or acknowledgements");
                        if(guests[2]==null && now>=rejoinAt)
                        {
                            guests[2]=new Guest(fingerprint,endpoint); joined++;
                            previousSamples[2]=0;
                        }
                        if(now>=nextInspection)
                        {
                            nextInspection=now+2;
                            for(int i=0;i<3;i++)
                            {
                                var guest=guests[i]; if(guest==null || !guest.Client.Live) continue;
                                guest.Client.Command("overview",0,0);
                            }
                        }
                        if(now>=nextSwap && now<started+seconds-3)
                        {
                            nextSwap=now+150;
                            guests[2]?.Dispose(); guests[2]=null; rejoinAt=now+1;
                            guests[1].Client.Subscribe(guests[1].OriginX+(joined%2==0?8:0),guests[1].OriginY);
                        }
                        if(now-lastReport>=60)
                        {
                            lastReport=now;
                            foreach(int i in new[]{0,1})
                            {
                                if(guests[i].Applied<=previousSamples[i]) throw new IOException("Healthy guest stopped receiving views: "+i);
                                previousSamples[i]=guests[i].Applied;
                            }
                            log("SOAK seconds="+(int)(now-started)+" joins="+joined+" healthy guest views="+guests[0].Applied+"/"+guests[1].Applied);
                        }
                    }
                    if(now>=nextPing)
                    {
                        nextPing=now+2;
                        foreach(var g in guests) if(g?.Client.Live==true)g.Client.Ping(clock.ElapsedMilliseconds);
                    }
                    Thread.Sleep(5);
                }
                for(int i=0;i<guests.Length;i++)
                {
                    var g=guests[i]; g.Latencies.Sort();
                    if (g.SideViews < 1) throw new IOException("No peripheral view");
                    log("SOAK peer="+i+" views="+g.Applied+" peripheralViews="+g.SideViews+" frames="+g.Frames+" rxBytes="+g.Bytes+
                        " commands="+g.Inspected+" commandP95ms="+g.Latencies[(int)Math.Ceiling(g.Latencies.Count*0.95)-1].ToString("F1"));
                }
                log("PASS real host, three TCP guests, "+seconds+" seconds, "+joined+" reconnections");
            }
            finally { foreach(var g in guests) g?.Dispose(); }
        }
    }
}
