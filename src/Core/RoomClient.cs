using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Queyang.Core
{
    public sealed class RoomClient
    {
        readonly string fingerprint;
        readonly Action<byte[]> send;
        byte[] room, staging;
        int peer, received;
        uint generation;
        ulong revision, command;
        int x = -1, y = -1;
        ViewPatch patch;
        public byte[] View { get; private set; }
        public bool Live => room != null && View != null;
        public string Status { get; private set; } = "Connecting";
        public bool Rejected { get; private set; }
        public event Action<string> ResultReceived;
        public event Action<double> CommandLatencyMs;
        readonly Dictionary<ulong,long> pending = new Dictionary<ulong,long>();

        public RoomClient(string fingerprint, Action<byte[]> send) { this.fingerprint = fingerprint; this.send = send; }
        public void Connected() => send(Wire.Pack(Message.Hello,w => Wire.Text(w,fingerprint)));
        public void Ping(long stamp)
        {
            if (room != null) Send(Message.Ping,w => w.Write(stamp));
        }
        void Send(Message type, Action<BinaryWriter> fields) =>
            send(Wire.Pack(type,w => { w.Write(room); w.Write(peer); fields(w); }));

        public void Subscribe(int x, int y)
        {
            if (this.x == x && this.y == y && generation != 0) return;
            this.x=x; this.y=y;
            if (room != null) RequestView();
        }

        void RequestView()
        {
            generation++; revision = 0; staging = null; patch = null; View = null;
            Status = "Loading view";
            Send(Message.Subscribe,w => { w.Write(generation); w.Write(x); w.Write(y); });
        }

        public void Command(string action,int x,int y)
        {
            if (!Live) { Status = "Wait for the view"; return; }
            if (pending.Count >= 256) { Status = "Waiting for host acknowledgements"; return; }
            var id = ++command;
            pending[id] = Stopwatch.GetTimestamp();
            Send(Message.Command,w => { w.Write(generation); w.Write(id); Wire.Text(w,action); w.Write(x); w.Write(y); });
        }

        public void Receive(byte[] frame)
        {
            Wire.Read(frame,(type,r) =>
            {
                if (type == Message.Reject && room == null)
                {
                    Status = Wire.Text(r); Wire.End(r); Rejected = true; return;
                }
                var identity = Wire.Bytes(r,16); var source = r.ReadInt32();
                if (room == null)
                {
                    if (type != Message.Welcome || source <= 0) throw new InvalidDataException("Expected Welcome");
                    Wire.End(r); room = identity; peer = source; RequestView(); return;
                }
                if (!ViewPatch.Equal(room,identity) || source != peer) throw new InvalidDataException("Wrong session");
                switch (type)
                {
                    case Message.Begin:
                        var g = r.ReadUInt32(); var from = r.ReadUInt64(); var to = r.ReadUInt64();
                        var length = r.ReadInt32(); var prefix = r.ReadInt32(); var suffix = r.ReadInt32();
                        var shape = new ViewPatch(prefix,suffix,length,Wire.Bytes(r,32)); Wire.End(r);
                        if (g != generation) return;
                        if (from != revision || to != from+1 || staging != null) { RequestView(); return; }
                        staging = shape.Prepare(View ?? Array.Empty<byte>());
                        patch = shape; received = 0;
                        if (shape.Changed == 0) Finish();
                        break;
                    case Message.Chunk:
                        var gen = r.ReadUInt32(); var rev = r.ReadUInt64(); var offset = r.ReadInt32();
                        var size = (int)(r.BaseStream.Length-r.BaseStream.Position);
                        if (gen != generation || staging == null || rev <= revision)
                        { r.BaseStream.Position = r.BaseStream.Length; return; }
                        if (rev != revision+1 || offset != received || size <= 0 || size > Wire.ChunkSize ||
                            size > patch.Changed-received)
                        { r.BaseStream.Position = r.BaseStream.Length; RequestView(); return; }
                        var bytes = Wire.Bytes(r,size);
                        Buffer.BlockCopy(bytes,0,staging,patch.Prefix+offset,size);
                        received += size;
                        if (received == patch.Changed) Finish();
                        else if (received % (4*Wire.ChunkSize) == 0) Ack(Message.Window,rev);
                        break;
                    case Message.Result:
                        var cmd = r.ReadUInt64(); var seq = r.ReadUInt64(); r.ReadUInt64();
                        var result = Wire.Text(r,8192); Wire.End(r);
                        if (pending.TryGetValue(cmd,out var start))
                        {
                            pending.Remove(cmd);
                            CommandLatencyMs?.Invoke((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
                        }
                        Status = result.StartsWith("M1:",StringComparison.Ordinal) ? "Management updated" :
                            "#" + cmd + " [" + seq + "] " + result;
                        ResultReceived?.Invoke(result);
                        break;
                    case Message.Pong: r.ReadInt64(); break;
                    default: throw new InvalidDataException("Unexpected host message");
                }
            });
        }

        void Finish()
        {
            if (!patch.Verify(staging)) { RequestView(); return; }
            var first = View == null;
            View = staging; staging = null; patch = null;
            revision++;
            if (first) Status = "Connected";
            Ack(Message.Applied,revision);
        }
        void Ack(Message type, ulong rev) => Send(type,w => {
            w.Write(generation); w.Write(rev); w.Write(received);
        });
    }
}
