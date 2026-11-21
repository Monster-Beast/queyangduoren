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
            public bool Accepted, Subscribed, Live;
            public int X, Y;
            public uint Generation;
            public ulong Revision, LastCommand;
            public byte[] Base = Array.Empty<byte>(), Target;
            public ViewPatch Patch;
            public int Sent, Confirmed, Tokens = 32;
            public double Progress, ScanAt, RefillAt, LastSeen;
            public NativeStage Native;
            public NativeSaveSource Save;
            public int PeripheralX, PeripheralY;
            public bool PeripheralPending;
            public double NextPeripheral;
            public readonly Dictionary<ulong, byte[]> Results = new Dictionary<ulong, byte[]>();
        }

        readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        readonly byte[] room = Guid.NewGuid().ToByteArray();
        readonly string fingerprint;
        readonly Action<int, byte[]> send;
        readonly Action<int> drop;
        readonly Func<int,int,byte[]> capture;
        readonly Func<string,int,int,string> execute;
        readonly Func<double> now;
        readonly Func<Stream> openNativeSave;
        public event Action<Exception> NativeCaptureFailed;
        ulong sequence;
        public int Guests => peers.Count;

        public RoomHost(string fingerprint, Action<int,byte[]> send, Action<int> drop,
            Func<int,int,byte[]> capture, Func<string,int,int,string> execute, Func<double> now,
            Func<Stream> openNativeSave = null)
        {
            this.fingerprint = fingerprint; this.send = send; this.drop = drop;
            this.capture = capture; this.execute = execute; this.now = now;
            this.openNativeSave = openNativeSave;
        }

        public void Connect(int id)
        {
            if (peers.Count >= 3 || peers.ContainsKey(id)) { drop(id); return; }
            peers.Add(id, new Peer { Progress = now(), LastSeen = now() });
        }
        public void Remove(int id)
        {
            if (!peers.TryGetValue(id,out var p)) return;
            peers.Remove(id);
            p.Save?.Dispose();
            p.Save = null;
        }

        public void Dispose()
        {
            foreach (var id in peers.Keys.ToArray()) Remove(id);
        }
        void Send(int id, Message type, Action<BinaryWriter> write)
        {
            send(id, Wire.Pack(type, w => { w.Write(room); w.Write(id); write(w); }));
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
                    bool wantsSave = r.BaseStream.Position < r.BaseStream.Length;
                    if (wantsSave && r.ReadByte() != 1) throw new InvalidDataException("Unknown join mode.");
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
                if (!ViewPatch.Equal(Wire.Bytes(r,16),room) || r.ReadInt32() != id)
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
                        if (offset < p.Confirmed || offset > p.Sent) throw new InvalidDataException("Invalid window");
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
                        if (p.Results.TryGetValue(cmd,out var cached)) { send(id,cached); break; }
                        if (cmd == 0 || cmd <= p.LastCommand) { Result(id,p,cmd,"ERROR Expired",false); break; }
                        Spend(p, 1);
                        p.LastCommand = cmd;
                        string outcome = g == p.Generation && p.Live ? execute(action,cx,cy) : "ERROR View not ready";
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
                w.Write(room); w.Write(id); w.Write(command); w.Write(seq); w.Write(p.Revision); Wire.Text(w,text,8192);
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
                { drop(id); Remove(id); continue; }
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
                    if (p.PeripheralPending && time >= p.NextPeripheral)
                    {
                        p.PeripheralPending = false; p.NextPeripheral = time + .125;
                        var tile = capture(p.PeripheralX,p.PeripheralY);
                        if (tile.Length == 0 || tile.Length > Wire.MaxFrame) throw new InvalidDataException("Invalid peripheral tile");
                        for (int offset = 0; offset < tile.Length; offset += Wire.ChunkSize)
                        {
                            int count = Math.Min(Wire.ChunkSize,tile.Length-offset);
                            Send(id,Message.PeripheralChunk,w => {
                                w.Write(tile.Length); w.Write(offset); w.Write(tile,offset,count);
                            });
                        }
                    }
                    if (p.Target == null && time >= p.ScanAt)
                    {
                        p.ScanAt = time + 0.25;
                        var view = capture(p.X,p.Y);
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
                catch (IOException) { drop(id); Remove(id); }
            }
        }
    }
}
