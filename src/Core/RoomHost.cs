using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Queyang.Core
{
    public sealed class RoomHost : IDisposable
    {
        enum NativeStage { None, Requested, Sending, Loading, Ready, Failed }
        sealed class Peer
        {
            public int SessionId;
            public Guid Installation;
            public bool Accepted, Subscribed, Live;
            public bool Disconnected;
            public int X, Y;
            public uint Generation;
            public ulong Revision, LastCommand;
            public byte[] Base = Array.Empty<byte>(), Target;
            public ViewPatch Patch;
            public int Sent, Confirmed, Tokens = 32;
            public double Progress, ScanAt, LastScan, RefillAt, LastSeen;
            public NativeStage Native;
            public NativeSaveSource Save;
            public int PeripheralX, PeripheralY;
            public bool PeripheralPending;
            public double NextPeripheral, NextMotion;
            public bool MotionPaused;
            public int CenterPage;
            public readonly Dictionary<long,int> PeripheralPages = new Dictionary<long,int>();
            public readonly Queue<long> PeripheralOrder = new Queue<long>();
            public readonly Dictionary<ulong, byte[]> Results = new Dictionary<ulong, byte[]>();
        }

        readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        readonly byte[] room = Guid.NewGuid().ToByteArray();
        readonly string fingerprint;
        readonly Action<int, byte[]> send;
        readonly Action<int> drop;
        readonly Func<int,int,byte[]> capture;
        readonly Func<int,int,int,byte[]> capturePage;
        readonly Func<string,int,int,string> execute;
        readonly Func<double> now;
        readonly Func<Stream> openNativeSave;
        public event Action<Exception> NativeCaptureFailed;
        public event Action<string> PeerFailure;
        public Func<int,Guid,string,int,int,string> ExecuteForPeer { get; set; }
        public Func<int,int,IEnumerable<byte[]>> CaptureMotion { get; set; }
        public Func<bool> IsMotionPaused { get; set; }
        ulong sequence;
        int lastSessionId;
        public int Guests => peers.Count;

        // An order may arrive immediately after the periodic view was sent.
        // Coalesce a drag into one early capture per interested peer.
        public void WorldCellChanged(int x,int y,int width,int height)
        {
            foreach (var p in peers.Values)
            {
                if (!p.Live || !p.Subscribed) continue;
                if (p.X>=0 && p.Y>=0 && (x<p.X || y<p.Y || (long)x-p.X>=width || (long)y-p.Y>=height)) continue;
                p.ScanAt=Math.Min(p.ScanAt,p.LastScan+.05);
            }
        }

        public RoomHost(string fingerprint, Action<int,byte[]> send, Action<int> drop,
            Func<int,int,byte[]> capture, Func<string,int,int,string> execute, Func<double> now,
            Func<Stream> openNativeSave = null, Func<int,int,int,byte[]> capturePage = null)
        {
            this.fingerprint = fingerprint; this.send = send; this.drop = drop;
            this.capture = capture; this.execute = execute; this.now = now;
            this.openNativeSave = openNativeSave;
            this.capturePage = capturePage;
        }

        public void Connect(int id)
        {
            if (peers.Count >= 3 || peers.ContainsKey(id)) { drop(id); return; }
            peers.Add(id, new Peer { SessionId = checked(++lastSessionId), Progress = now(), LastSeen = now() });
        }
        public void Remove(int id)
        {
            if (!peers.TryGetValue(id,out var p)) return;
            peers.Remove(id);
            p.Disconnected = true;
            p.Target = null;
            p.Save?.Dispose();
            p.Save = null;
        }

        public void Dispose()
        {
            foreach (var id in peers.Keys.ToArray()) Remove(id);
        }
        void Send(int id, Message type, Action<BinaryWriter> write)
        {
            var session = peers[id].SessionId;
            send(id, Wire.Pack(type, w => { w.Write(room); w.Write(session); write(w); }));
        }

        public void Receive(int id, byte[] frame)
        {
            if (!peers.TryGetValue(id, out var p)) return;
            Wire.Read(frame, (type,r) =>
            {
                if (!p.Accepted)
                {
                    if (type != Message.Hello) throw new InvalidDataException("Expected Hello");
                    var version = Wire.Text(r);
                    byte mode = r.BaseStream.Position == r.BaseStream.Length ? (byte)0 : r.ReadByte();
                    if (mode > 3) throw new InvalidDataException("Unknown join mode.");
                    bool wantsSave = (mode & 1) != 0;
                    if ((mode & 2) != 0) p.Installation = new Guid(Wire.Bytes(r,16));
                    Wire.End(r);
                    if (version != fingerprint || (wantsSave && openNativeSave == null))
                    {
                        var reason = version != fingerprint ? "Game or mod build mismatch" : "Host does not support native save transfer";
                        send(id, Wire.Pack(Message.Reject, w => Wire.Text(w,reason)));
                        return;
                    }
                    p.Accepted = true; p.Progress = now();
                    if (wantsSave) p.Native = NativeStage.Requested;
                    Send(id, Message.Welcome, w => {});
                    return;
                }
                if (!ViewPatch.Equal(Wire.Bytes(r,16),room) || r.ReadInt32() != p.SessionId)
                    throw new InvalidDataException("Wrong room or peer generation");
                p.LastSeen = now();
                switch (type)
                {
                    case Message.NativeWindow:
                        var written = r.ReadInt32(); Wire.End(r);
                        if (p.Native != NativeStage.Sending || p.Save == null)
                            throw new InvalidDataException("Unexpected native save acknowledgement.");
                        if (p.Save.Confirm(written)) p.Progress = now();
                        break;
                    case Message.NativeReady:
                        Wire.End(r);
                        if (p.Native != NativeStage.Loading) throw new InvalidDataException("Unexpected native world acknowledgement.");
                        p.Native = NativeStage.Ready; p.Progress = now();
                        break;
                    case Message.PeripheralRequest:
                        var px = r.ReadInt32(); var py = r.ReadInt32(); Wire.End(r);
                        if (p.Native != NativeStage.Ready || p.PeripheralPending || px < 0 || py < 0)
                            throw new InvalidDataException("Invalid peripheral request");
                        p.PeripheralPending = true; p.PeripheralX = px; p.PeripheralY = py;
                        break;
                    case Message.Subscribe:
                        var gen = r.ReadUInt32(); var x = r.ReadInt32(); var y = r.ReadInt32(); Wire.End(r);
                        if (p.Native != NativeStage.None && p.Native != NativeStage.Ready)
                            throw new InvalidDataException("Native world has not been loaded.");
                        if (gen <= p.Generation) return;
                        Spend(p, 2);
                        p.Generation = gen; p.X = x; p.Y = y; p.Subscribed = true;
                        ResetView(p); break;
                    case Message.Applied:
                    case Message.Window:
                        var generation = r.ReadUInt32(); var rev = r.ReadUInt64(); var offset = r.ReadInt32(); Wire.End(r);
                        if (generation != p.Generation || p.Target == null || rev != p.Revision + 1) return;
                        if (offset < 0 || offset > p.Sent) throw new InvalidDataException("Invalid window");
                        if (type == Message.Window && offset <= p.Confirmed) return;
                        if (offset != p.Confirmed) p.Progress = now();
                        p.Confirmed = offset;
                        if (type == Message.Applied)
                        {
                            if (offset != p.Patch.Changed) throw new InvalidDataException("Incomplete transfer");
                            p.Base = p.Target; p.Target = null; p.Patch = null; p.Revision = rev; p.Live = true;
                            p.Progress = now();
                        }
                        break;
                    case Message.Resync:
                        var sub = r.ReadUInt32(); Wire.End(r);
                        if (sub == p.Generation) { Spend(p, 4); ResetView(p); }
                        break;
                    case Message.Command:
                        var g = r.ReadUInt32(); var cmd = r.ReadUInt64(); var action = Wire.Text(r);
                        var cx = r.ReadInt32(); var cy = r.ReadInt32(); Wire.End(r);
                        Spend(p, 1);
                        if (p.Results.TryGetValue(cmd,out var cached)) { send(id,cached); break; }
                        if (cmd == 0 || cmd <= p.LastCommand) { Result(id,p,cmd,"ERROR Expired",false); break; }
                        p.LastCommand = cmd;
                        string outcome = g == p.Generation && p.Live ? (ExecuteForPeer == null ? execute(action,cx,cy) : ExecuteForPeer(id,p.Installation,action,cx,cy)) : "ERROR View not ready";
                        Result(id,p,cmd,outcome,true); break;
                    case Message.Ping:
                        var stamp = r.ReadInt64(); Wire.End(r);
                        Send(id,Message.Pong,w => w.Write(stamp)); break;
                    default: throw new InvalidDataException("Unexpected client message");
                }
            });
        }

        void Spend(Peer p, int cost)
        {
            var time = now();
            if (time >= p.RefillAt) { p.Tokens = 32; p.RefillAt = time + 1; }
            if (p.Tokens < cost) throw new InvalidDataException("Input rate exceeded");
            p.Tokens -= cost;
        }

        void Result(int id, Peer p, ulong command, string text, bool cache)
        {
            var seq = ++sequence;
            var frame = Wire.Pack(Message.Result, w => {
                w.Write(room); w.Write(p.SessionId); w.Write(command); w.Write(seq); w.Write(p.Revision); Wire.Text(w,text,8192);
            });
            if (cache)
            {
                if (p.Results.Count >= 64) p.Results.Remove(p.Results.Keys.Min());
                p.Results.Add(command,frame);
            }
            send(id,frame);
        }

        void ResetView(Peer p)
        {
            p.Base = Array.Empty<byte>(); p.Target = null; p.Patch = null; p.Revision = 0;
            p.ScanAt = 0; p.Live = false; p.Progress = now();
        }

        byte[] Capture(Peer p, bool peripheral)
        {
            int x=peripheral ? p.PeripheralX : p.X, y=peripheral ? p.PeripheralY : p.Y;
            if (capturePage==null) return capture(x,y);
            if (!peripheral) return capturePage(x,y,p.CenterPage++ & int.MaxValue);
            long key=((long)x<<32)|(uint)y;
            if (!p.PeripheralPages.TryGetValue(key,out int page))
            {
                if (p.PeripheralPages.Count>=256) p.PeripheralPages.Remove(p.PeripheralOrder.Dequeue());
                p.PeripheralOrder.Enqueue(key);
            }
            p.PeripheralPages[key]=(page+1)&int.MaxValue;
            return capturePage(x,y,page);
        }

        // Called from Tick on the host game thread. A new connection freezes a fresh save at admission.
        void TransferNative(int id, Peer p)
        {
            try
            {
                if (p.Native == NativeStage.Requested)
                {
                    p.Save = new NativeSaveSource(openNativeSave());
                    p.Native = NativeStage.Sending;
                    p.Progress = now();
                    Send(id,Message.NativeBegin,w => w.Write(p.Save.Length));
                }
                for (int n=0; n<8 && p.Save != null && p.Save.Sent < p.Save.Length &&
                    p.Save.Sent - p.Save.Confirmed < 8*Wire.ChunkSize; n++)
                    Send(id,Message.NativeChunk,p.Save.WriteChunk);
                if (p.Save != null && p.Save.Confirmed == p.Save.Length)
                {
                    byte[] digest = p.Save.Finish();
                    p.Save.Dispose(); p.Save = null;
                    p.Native = NativeStage.Loading; p.Progress = now();
                    Send(id,Message.NativeEnd,w => w.Write(digest));
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // One guest's capture/read failure must not tear down the authority or reveal local file paths.
                p.Save?.Dispose(); p.Save=null;
                p.Native=NativeStage.Failed; p.Progress=now();
                NativeCaptureFailed?.Invoke(ex);
                Send(id,Message.NativeError,w => Wire.Text(w,"Host could not capture or transfer its native save"));
            }
        }

        public void Tick()
        {
            var time = now();
            bool capturedNative = false;
            foreach (var id in peers.Keys.ToArray())
            {
                if (!peers.TryGetValue(id,out var p)) continue;
                double timeout = p.Native == NativeStage.Loading ? 180 : 30;
                if (time - p.LastSeen > timeout ||
                    ((!p.Accepted || !p.Subscribed || p.Target != null) && time - p.Progress > timeout))
                {
                    PeerFailure?.Invoke(FormattableString.Invariant(
                        $"Timed out stage={p.Native} subscribed={p.Subscribed} revision={p.Revision} incomingAge={time - p.LastSeen:F1}s progressAge={time - p.Progress:F1}s"));
                    drop(id); Remove(id); continue;
                }
                if (p.Native == NativeStage.Requested || p.Native == NativeStage.Sending)
                {
                    if (p.Native == NativeStage.Requested)
                    {
                        if (capturedNative) continue;
                        capturedNative = true;
                    }
                    TransferNative(id,p); continue;
                }
                if (!p.Subscribed) continue;
                try
                {
                    bool paused=IsMotionPaused?.Invoke()==true;
                    if (CaptureMotion!=null && p.Native==NativeStage.Ready && p.Live &&
                        (time>=p.NextMotion || paused!=p.MotionPaused))
                    {
                        double interval=paused?.25:.05;
                        p.NextMotion=p.NextMotion>0 && paused==p.MotionPaused ?
                            Math.Max(time+.001,p.NextMotion+interval) : time+interval;
                        p.MotionPaused=paused;
                        foreach(var packet in CaptureMotion(p.X,p.Y))
                        {
                            if(p.Disconnected) break;
                            if(packet.Length>1100) throw new InvalidDataException("Oversized motion sample");
                            Send(id,Message.Motion,w=>{ w.Write(p.Generation); w.Write(packet); });
                        }
                    }
                    if (p.PeripheralPending && time >= p.NextPeripheral)
                    {
                        p.PeripheralPending = false; p.NextPeripheral = time + .125;
                        var tile = Capture(p,true);
                        if (tile.Length == 0 || tile.Length > Wire.MaxFrame) throw new InvalidDataException("Invalid peripheral tile");
                        for (int offset = 0; !p.Disconnected && offset < tile.Length; offset += Wire.ChunkSize)
                        {
                            int count = Math.Min(Wire.ChunkSize,tile.Length-offset);
                            Send(id,Message.PeripheralChunk,w => {
                                w.Write(tile.Length); w.Write(offset); w.Write(tile,offset,count);
                            });
                        }
                    }
                    if (p.Disconnected) continue;
                    if (p.Target == null && time >= p.ScanAt)
                    {
                        p.LastScan = time;
                        p.ScanAt = time + 0.25;
                        var view = Capture(p,false);
                        if (p.Live && ViewPatch.Equal(p.Base,view)) continue;
                        var patch = ViewPatch.Compare(p.Base,view);
                        p.Target = view; p.Patch = patch; p.Sent = p.Confirmed = 0; p.Progress = time;
                        Send(id,Message.Begin,w => {
                            w.Write(p.Generation); w.Write(p.Revision); w.Write(p.Revision+1);
                            w.Write(patch.Length); w.Write(patch.Prefix); w.Write(patch.Suffix); w.Write(patch.Digest);
                        });
                    }
                    while (p.Target != null && p.Sent < p.Patch.Changed && p.Sent - p.Confirmed < 8 * Wire.ChunkSize)
                    {
                        var offset = p.Sent;
                        var count = Math.Min(Wire.ChunkSize,p.Patch.Changed-offset);
                        Send(id,Message.Chunk,w => {
                            w.Write(p.Generation); w.Write(p.Revision+1); w.Write(offset);
                            w.Write(p.Target,p.Patch.Prefix+offset,count);
                        });
                        p.Sent += count;
                    }
                }
                catch (IOException error)
                {
                    PeerFailure?.Invoke($"Viewport failed at ({p.X},{p.Y}), revision={p.Revision}: {error.Message}");
                    drop(id); Remove(id);
                }
            }
        }
    }
}
