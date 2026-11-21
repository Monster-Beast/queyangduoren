using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Queyang.Core
{
    public sealed class RoomHost
    {
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
        ulong sequence;
        public int Guests => peers.Count;

        public RoomHost(string fingerprint, Action<int,byte[]> send, Action<int> drop,
            Func<int,int,byte[]> capture, Func<string,int,int,string> execute, Func<double> now)
        {
            this.fingerprint = fingerprint; this.send = send; this.drop = drop;
            this.capture = capture; this.execute = execute; this.now = now;
        }

        public void Connect(int id)
        {
            if (peers.Count >= 3 || peers.ContainsKey(id)) { drop(id); return; }
            peers.Add(id, new Peer { Progress = now(), LastSeen = now() });
        }
        public void Remove(int id) => peers.Remove(id);
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
                    var version = Wire.Text(r); Wire.End(r);
                    if (version != fingerprint)
                    {
                        send(id, Wire.Pack(Message.Reject, w => Wire.Text(w,"Game or mod build mismatch")));
                        return;
                    }
                    p.Accepted = true; p.Progress = now();
                    Send(id, Message.Welcome, w => {});
                    return;
                }
                if (!ViewPatch.Equal(Wire.Bytes(r,16),room) || r.ReadInt32() != id)
                    throw new InvalidDataException("Wrong room or peer generation");
                p.LastSeen = now();
                switch (type)
                {
                    case Message.Subscribe:
                        var gen = r.ReadUInt32(); var x = r.ReadInt32(); var y = r.ReadInt32(); Wire.End(r);
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

        public void Tick()
        {
            var time = now();
            foreach (var id in peers.Keys.ToArray())
            {
                var p = peers[id];
                if (time - p.LastSeen > 30 ||
                    ((!p.Accepted || !p.Subscribed || p.Target != null) && time - p.Progress > 30))
                { drop(id); Remove(id); continue; }
                if (!p.Subscribed) continue;
                try
                {
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
