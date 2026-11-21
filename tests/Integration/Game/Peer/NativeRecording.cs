using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Queyang.Core;
using Queyang.Game;

namespace Queyang.Integration
{
    // Files stay in the private fixture directory. Persist exact wire payloads, including their native fields.
    internal sealed class NativeRecording
    {
        internal readonly string DirectoryPath;
        private readonly Action<string> log;
        private bool firstWritten, orderWritten, digAccepted;
        private int digX, digY, ladderX, ladderY;
        private byte[] latest;

        internal static string LocalDirectory()
        {
            string dir=Environment.GetEnvironmentVariable("QY_NATIVE_RECORDING");
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("Set QY_NATIVE_RECORDING to a private fixture directory.");
            return Path.GetFullPath(dir);
        }

        internal NativeRecording(string dir,Action<string> log)
        {
            DirectoryPath=dir; this.log=log;
            Directory.CreateDirectory(dir);
            foreach (var name in new[] {"baseline.sav","first.view","order.view","done.view","dig.txt","ladder-menu.bin","dupe-vitals.bin"})
                if (File.Exists(Path.Combine(dir,name))) throw new IOException("Recording already contains " + name);
        }

        internal void Save(string verified)
        {
            File.Move(verified,Path.Combine(DirectoryPath,"baseline.sav"));
            log("NATIVE_RECORD_BASELINE verified");
        }

        internal void Sent(string action,int x,int y)
        {
            if (action == "ladder") { ladderX=x; ladderY=y; }
            if (action != "dig") return;
            digX=x; digY=y; digAccepted=false;
        }

        internal void Result(string result)
        {
            if (!result.StartsWith("OK Dig",StringComparison.Ordinal)) return;
            digAccepted=true;
            CaptureOrder();
        }

        internal void View(byte[] bytes)
        {
            if (bytes==null || ReferenceEquals(bytes,latest)) return;
            latest=bytes;
            if (!firstWritten)
            {
                if (!ColonyView.Decode(bytes).Native) throw new InvalidDataException("Host did not send a native viewport.");
                WriteNew("first.view",bytes); firstWritten=true; log("NATIVE_RECORD_FIRST_VIEW");
            }
            CaptureOrder();
        }

        private void CaptureOrder()
        {
            if (latest==null || !digAccepted || orderWritten) return;
            if ((CellFlags(latest,digX,digY) & 8) == 0) return;
            WriteNew("order.view",latest);
            WriteNew("dig.txt",Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture,"{0},{1}",digX,digY)));
            orderWritten=true; log("NATIVE_RECORD_ORDER_VIEW " + digX + "," + digY);
        }

        internal void Finish(byte[] bytes, Func<string,int,int,string> request)
        {
            if (!orderWritten || (CellFlags(bytes,digX,digY) & 11) != 1)
                throw new InvalidDataException("Missing authoritative dig order or completed excavation.");
            WriteNew("done.view",bytes);
            log("NATIVE_RECORD_DONE_VIEW");
            var view = ColonyView.Decode(bytes);
            int target = view.Cells[(ladderY-view.Y)*ColonyView.Width+ladderX-view.X].TargetId;
            var query = "nm/proxy/" + target + "/0";
            string firstMenu = request(query, ladderX, ladderY);
            string queue = Row(firstMenu, "指派拆除建筑");
            string queued = request(queue, ladderX, ladderY);
            string cancel = Row(queued, "取消拆除建筑");
            string stale = request(queue, ladderX, ladderY);
            if (!stale.StartsWith("ERROR Setting changed",StringComparison.Ordinal) ||
                Row(request(query,ladderX,ladderY),"取消拆除建筑") != cancel)
                throw new InvalidDataException("Repeated stale ladder setting changed authoritative state");
            WriteNew("ladder-stale.txt",Encoding.UTF8.GetBytes(stale));
            string cancelled = request(cancel, ladderX, ladderY);
            if (Row(cancelled,"指派拆除建筑") != queue) throw new InvalidDataException("Ladder menu did not revert");
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                w.Write(ladderX); w.Write(ladderY);
                foreach (var text in new[] { query, firstMenu, queue, queued, cancel, cancelled }) Wire.Text(w, text, 8192);
                WriteNew("ladder-menu.bin", s.ToArray());
            }
            log("NATIVE_RECORD_LADDER_MENU_AND_CANCEL");
            int saveId = view.Cells[(ladderY-view.Y)*ColonyView.Width+ladderX-view.X].BuildingSaveId;
            var readings = request("nm/readings/" + saveId, ladderX, ladderY);
            if (!readings.StartsWith("NM1/g/" + saveId + "/", StringComparison.Ordinal))
                throw new InvalidDataException("Host failed to inspect the finished ladder");
            WriteNew("ladder-state.txt", Encoding.UTF8.GetBytes(readings));
            RecordVitals(request);
            using (var s=new MemoryStream())
            using (var w=new BinaryWriter(s))
            {
                void Record(string queryName,string expected)
                {
                    string value=request(queryName,0,0);
                    if (!value.StartsWith(expected,StringComparison.Ordinal)) throw new InvalidDataException("Missing host data for "+queryName);
                    Wire.Text(w,queryName); Wire.Text(w,value,8192);
                }
                Record("nm/overview","NM1/ov/");
                Record("nm/research","NM1/r/");
                int pages=1;
                for (int i=0;i<pages;i++)
                {
                    string name="nm/schedules/"+i, value=request(name,0,0);
                    var fields=value.Split('/');
                    if (fields.Length!=4 || fields[1]!="sl") throw new InvalidDataException("Missing schedule catalogue");
                    using (var r=new BinaryReader(new MemoryStream(Convert.FromBase64String(fields[3].Replace('_','/')))))
                    {
                        int total=r.ReadUInt16();
                        if (total<1 || total>2048 || r.ReadUInt16()!=i) throw new InvalidDataException("Invalid schedule catalogue page");
                        pages=(total+1)/2;
                    }
                    Wire.Text(w,name); Wire.Text(w,value,8192);
                }
                WriteNew("global-management.bin",s.ToArray());
                log("NATIVE_RECORD_GLOBAL_MANAGEMENT pages="+pages);
            }
        }

        void RecordVitals(Func<string,int,int,string> request)
        {
            var initial = ColonyView.Decode(File.ReadAllBytes(Path.Combine(DirectoryPath,"first.view")));
            string best = null;
            int chosen = -1;
            // Prefer an occupied duplicant so the screenshot can show an actual work target.
            foreach (var dupe in initial.Dupes.Take(4))
            {
                string prefix = "NM1/v/" + dupe.SaveId + "/";
                string value = request("nm/vitals/" + dupe.SaveId,0,0);
                if (!value.StartsWith(prefix,StringComparison.Ordinal) || value == prefix + "-") continue;
                string payload = value.Substring(prefix.Length);
                using (var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(payload.Replace('_','/')))))
                {
                    if (r.ReadByte() != 1) throw new InvalidDataException("Unknown recorded vitals");
                    Wire.Text(r,256); Wire.Text(r,256);
                    bool occupied = r.ReadBoolean();
                    if (best == null || occupied) { best = value; chosen = dupe.SaveId; }
                    if (occupied) break;
                }
            }
            if (best == null) throw new InvalidDataException("No authoritative duplicant details");
            string profile = request("nm/profile/" + chosen,0,0);
            if (!profile.StartsWith("NM1/pf/" + chosen + "/",StringComparison.Ordinal) || profile.EndsWith("/-",StringComparison.Ordinal))
                throw new InvalidDataException("No authoritative skill and consumables profile");
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                w.Write(chosen); Wire.Text(w,best,8192);
                Wire.Text(w,profile,8192);
                WriteNew("dupe-vitals.bin",s.ToArray());
            }
            log("NATIVE_RECORDED_DUPLICANT_DETAILS id=" + chosen);
        }

        static string Row(string menu, string label)
        {
            if (!menu.StartsWith("NM1/p/BLD1 ", StringComparison.Ordinal)) throw new InvalidDataException("No authoritative proxy menu: " + menu);
            return "nm/" + menu.Split('\n').Skip(1).Select(line=>line.Split('\t')).Single(f=> f.Length==2 && f[1]==label)[0];
        }

        internal static byte CellFlags(byte[] bytes,int x,int y)
        {
            var view=ColonyView.Decode(bytes);
            if (!view.Native || x<view.X || x>=view.X+ColonyView.Width || y<view.Y || y>=view.Y+ColonyView.Height)
                throw new InvalidDataException("Recorded dig is outside the native viewport.");
            return view.Cells[(y-view.Y)*ColonyView.Width+x-view.X].Flags;
        }

        private void WriteNew(string name,byte[] bytes)
        {
            using(var file=new FileStream(Path.Combine(DirectoryPath,name),FileMode.CreateNew,FileAccess.Write,FileShare.None))
                file.Write(bytes,0,bytes.Length);
        }
    }

    internal sealed class NativeReplay
    {
        private readonly byte[] first,order,done;
        private readonly int digX,digY;
        private readonly string save;
        private readonly Action<string> log;
        private readonly Func<double> now;
        private bool ordered,completed;
        private int menuX,menuY,menuStep;
        private bool menuRead;
        private int joins;
        private readonly string vitalsQuery, vitalsReply, profileQuery, profileReply;
        private string[] operations;
        private string buildingReadings;
        private readonly byte[] printedView;
        private readonly string printedVitals, printedProfile;
        private readonly int printedId;
        private bool showingPrinted;
        private readonly StockOrderTape[] stockOrders;
        private int stockIndex = -1;
        private bool stockIssued;
        private readonly string staleLadder;
        private readonly Dictionary<string,string> management=new Dictionary<string,string>();
        private double orderFirstSentAt=double.NaN;

        internal NativeReplay(string dir, Action<string> log, Func<double> now)
        {
            this.log=log; this.now=now; save=Path.Combine(dir,"baseline.sav");
            stockOrders=StockOrderTape.Load(dir);
            long saveBytes=new FileInfo(save).Length;
            if (saveBytes<1 || saveBytes>Queyang.Core.Wire.MaxSnapshot) throw new InvalidDataException("Bad native recording save size.");
            first=ReadView(dir,"first.view");
            order=ReadView(dir,"order.view");
            done=ReadView(dir,"done.view");
            var coordPath=Path.Combine(dir,"dig.txt");
            if (new FileInfo(coordPath).Length>64) throw new InvalidDataException("Bad recorded coordinates.");
            var xy=File.ReadAllText(coordPath).Trim().Split(',');
            if(xy.Length!=2 || !int.TryParse(xy[0],NumberStyles.None,CultureInfo.InvariantCulture,out digX) ||
                !int.TryParse(xy[1],NumberStyles.None,CultureInfo.InvariantCulture,out digY))
                throw new InvalidDataException("Bad recorded coordinates.");
            if ((NativeRecording.CellFlags(first,digX,digY)&11)!=3 || (NativeRecording.CellFlags(order,digX,digY)&8)==0 ||
                (NativeRecording.CellFlags(done,digX,digY)&11)!=1)
                throw new InvalidDataException("Tape lacks initial terrain, order marker, or finished excavation.");
            string menuPath=Path.Combine(dir,"ladder-menu.bin");
            string globalPath=Path.Combine(dir,"global-management.bin");
            if (File.Exists(globalPath)) using (var r=new BinaryReader(File.OpenRead(globalPath)))
                while (r.BaseStream.Position<r.BaseStream.Length)
                {
                    if (management.Count>=1026) throw new InvalidDataException("Too many management pages");
                    management.Add(Wire.Text(r),Wire.Text(r,8192));
                }
            string printedPath=Path.Combine(dir,"printed.bin");
            if (File.Exists(printedPath))
            {
                printedView=ReadView(dir,"printed.view");
                using (var reader=new BinaryReader(File.OpenRead(printedPath)))
                {
                    printedId=reader.ReadInt32(); printedVitals=Wire.Text(reader,8192); printedProfile=Wire.Text(reader,8192);
                    Wire.End(reader);
                }
                if (!ColonyView.Decode(printedView).Dupes.Any(d=>d.SaveId==printedId) || ColonyView.Decode(first).Dupes.Any(d=>d.SaveId==printedId))
                    throw new InvalidDataException("Invalid post-baseline duplicant");
            }
            string readingPath = Path.Combine(dir,"ladder-state.txt");
            string stalePath = Path.Combine(dir,"ladder-stale.txt");
            if (File.Exists(stalePath)) staleLadder = File.ReadAllText(stalePath);
            if (File.Exists(readingPath)) buildingReadings = File.ReadAllText(readingPath);
            string detailsPath = Path.Combine(dir,"dupe-vitals.bin");
            if (File.Exists(detailsPath)) using (var r = new BinaryReader(File.OpenRead(detailsPath)))
            {
                int id = r.ReadInt32();
                vitalsQuery = "nm/vitals/" + id;
                vitalsReply = Wire.Text(r,8192);
                if (id < 1 || !vitalsReply.StartsWith("NM1/v/" + id + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("Recorded duplicant identifier does not match the response");
                if (r.BaseStream.Position < r.BaseStream.Length)
                {
                    profileQuery = "nm/profile/" + id;
                    profileReply = Wire.Text(r,8192);
                    if (!profileReply.StartsWith("NM1/pf/" + id + "/",StringComparison.Ordinal)) throw new InvalidDataException("Invalid recorded profile");
                }
                Wire.End(r);
            }
            if (File.Exists(menuPath))
            {
                using(var r=new BinaryReader(File.OpenRead(menuPath)))
                {
                    menuX=r.ReadInt32(); menuY=r.ReadInt32();
                    operations=Enumerable.Range(0,6).Select(_=>Wire.Text(r,8192)).ToArray();
                    Wire.End(r);
                }
                if ((NativeRecording.CellFlags(first,menuX,menuY)&48)!=0 || (NativeRecording.CellFlags(done,menuX,menuY)&48)!=16)
                    throw new InvalidDataException("Tape must show a newly completed ladder");
            }
        }

        private static byte[] ReadView(string dir,string name)
        {
            var file=new FileInfo(Path.Combine(dir,name));
            if(file.Length<1 || file.Length>Queyang.Core.Wire.MaxFrame) throw new InvalidDataException("Invalid recording " + name);
            return File.ReadAllBytes(file.FullName);
        }

        internal Stream OpenSave()
        {
            showingPrinted=false;
            stockIndex=-1; stockIssued=false;
            ordered=false; completed=false; menuStep=0; menuRead=false; orderFirstSentAt=double.NaN;
            log("NATIVE_REPLAY_BASELINE");
            log("NATIVE_REPLAY_JOIN " + (++joins));
            return File.OpenRead(save);
        }

        internal string Command(string action,int x,int y)
        {
            if (completed && stockOrders!=null)
            {
                int next=stockIndex+1;
                if (next<stockOrders.Length && (stockIndex<0 || stockIssued) && action=="fixture/order/"+next)
                { stockIndex=next; stockIssued=false; return "OK recorded stock tool viewport"; }
                if (stockIndex>=0)
                {
                    var row=stockOrders[stockIndex];
                    if (!stockIssued && action==row.Command && x==row.X && y==row.Y)
                    { stockIssued=true; log("NATIVE_REPLAY_STOCK_TOOL "+action); return row.Reply; }
                }
            }
            if (management.TryGetValue(action,out var value)) return value;
            if (action=="nm/overview" || action=="nm/research" || action.StartsWith("nm/schedules/",StringComparison.Ordinal))
                return "NM1/unrecorded";
            if (printedView!=null && completed && action=="fixture/printed")
            { showingPrinted=true; return "OK post-baseline recorded view"; }
            if (showingPrinted && action=="nm/vitals/"+printedId) return printedVitals;
            if (showingPrinted && action=="nm/profile/"+printedId) return printedProfile;
            if (action == vitalsQuery) return vitalsReply;
            if (action == profileQuery) return profileReply;
            // Tapes produced before these optional selected-object replies have no such observations.
            if (action.StartsWith("nm/vitals/",StringComparison.Ordinal)) return "NM1/v/" + action.Substring(10) + "/-";
            if (action.StartsWith("nm/profile/",StringComparison.Ordinal)) return "NM1/pf/" + action.Substring(11) + "/-";
            if (operations!=null && completed && x==menuX && y==menuY)
            {
                if (menuStep == 1 && action==operations[2] && staleLadder != null) return staleLadder;
                if (buildingReadings != null && action == "nm/readings/" + buildingReadings.Split('/')[2])
                    return buildingReadings;
                if (action==operations[0])
                {
                    if (!menuRead) { log("NATIVE_REPLAY_LADDER_MENU_OPEN"); menuRead=true; }
                    return operations[1+menuStep*2];
                }
                if (menuStep<2 && action==operations[2+menuStep*2])
                {
                    menuStep++;
                    log("NATIVE_REPLAY_LADDER_MENU_EDIT " + menuStep);
                    return operations[1+menuStep*2];
                }
            }
            if (action.StartsWith("nm/setproxy/",StringComparison.Ordinal))
                log("NATIVE_REPLAY_LADDER_MENU_REJECT " + action + " at " + x + "," + y + " step=" + menuStep);
            if (action!="dig" || x!=digX || y!=digY || ordered) return "ERROR Expected recorded dig";
            ordered=true;
            log("NATIVE_REPLAY_DIG " + x + "," + y);
            return "OK Dig designated";
        }

        internal byte[] Capture(int x,int y)
        {
            if (stockIndex>=0) return stockIssued ? stockOrders[stockIndex].After : stockOrders[stockIndex].Before;
            if (showingPrinted) return printedView;
            if (!ordered) return first;
            if (double.IsNaN(orderFirstSentAt))
            {
                orderFirstSentAt=now();
                log("NATIVE_REPLAY_ORDER_VIEW");
            }
            if (now()-orderFirstSentAt<1.5) return order;
            if (!completed) { completed=true; log("NATIVE_REPLAY_DONE_VIEW"); }
            return done;
        }

        internal static int Run(Action<string> log, int duration)
        {
            var clock=Stopwatch.StartNew();
            double Now() => clock.Elapsed.TotalSeconds;
            var tape=new NativeReplay(NativeRecording.LocalDirectory(),log,Now);
            RoomHost host=null;
            using(var wire=new CoopTransport((id,b)=>host.Receive(id,b),id=>host.Connect(id),id=>host.Remove(id)))
            using(host=new RoomHost(Environment.GetEnvironmentVariable("QY_SMOKE_FINGERPRINT"),wire.Send,wire.Drop,
                tape.Capture,tape.Command,Now,tape.OpenSave))
            {
                wire.Host(false); log("NATIVE_REPLAY_READY");
                while (Now()<duration) { wire.Poll(); host.Tick(); Thread.Sleep(4); }
            }
            return 0;
        }
    }
}
