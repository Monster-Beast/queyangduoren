using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Queyang.Core
{
    public sealed class RoomClient : IDisposable
    {
        readonly string fingerprint;
        public Guid Installation { private get; set; }
        readonly Action<byte[]> send;
        readonly string nativeSaveDirectory;
        readonly Action<string> nativeSaveReady;
        NativeSaveSink save;
        bool worldReady, disposed;
        public bool WaitingForNativeWorld { get; private set; }
        public int NativeBytesExpected { get; private set; }
        public int NativeBytesReceived { get; private set; }
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
        public event Action<byte[]> ViewReceived;
        public event Action<byte[]> PeripheralReceived;
        public event Action<byte[]> MotionReceived;
        bool peripheralPending;
        uint peripheralGeneration;
        byte[] peripheral;
        int peripheralOffset;
        public bool RequestPeripheral(int x,int y)
        {
            if (!worldReady || !Live || peripheralPending || x < 0 || y < 0) return false;
            peripheralPending = true;
            peripheralGeneration = generation;
            Send(Message.PeripheralRequest,w => { w.Write(x); w.Write(y); });
            return true;
        }
        readonly Dictionary<ulong,long> pending = new Dictionary<ulong,long>();

        // The game owns a verified .sav once nativeSaveReady returns; incomplete files belong to this client.
        public RoomClient(string fingerprint, Action<byte[]> send, string nativeSaveDirectory = null, Action<string> nativeSaveReady = null)
        {
            if ((nativeSaveDirectory == null) != (nativeSaveReady == null)) throw new ArgumentException("Supply both native save directory and loader.");
            this.fingerprint = fingerprint; this.send = send;
            this.nativeSaveDirectory = nativeSaveDirectory == null ? null : Path.GetFullPath(nativeSaveDirectory);
            this.nativeSaveReady = nativeSaveReady;
        }
        public void Connected() => send(Wire.Pack(Message.Hello,w => {
            Wire.Text(w,fingerprint);
            byte mode = (byte)((nativeSaveReady != null ? 1 : 0) | (Installation != Guid.Empty ? 2 : 0));
            if (mode != 0) w.Write(mode);
            if (Installation != Guid.Empty) w.Write(Installation.ToByteArray());
        }));

        public void NativeWorldReady()
        {
            if (worldReady) return;
            if (!WaitingForNativeWorld || disposed) throw new InvalidOperationException("No verified native world awaiting load.");
            WaitingForNativeWorld = false; worldReady = true;
            Send(Message.NativeReady,w => {});
            RequestView();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed=true; WaitingForNativeWorld=false;
            save?.Dispose(); save=null;
            room=null; View=null; staging=null; peripheral=null; peripheralPending=false; peripheralOffset=0; pending.Clear();
        }
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
            if (room != null && (nativeSaveReady == null || worldReady)) RequestView();
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
            if (disposed) throw new ObjectDisposedException(nameof(RoomClient));
            try { ReadFrame(frame); }
            catch { save?.Dispose(); save=null; throw; }
        }

        private void ReadFrame(byte[] frame)
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
                    Wire.End(r); room = identity; peer = source;
                    if (nativeSaveReady == null) RequestView();
                    else Status = "Waiting for native save";
                    return;
                }
                if (!ViewPatch.Equal(room,identity) || source != peer) throw new InvalidDataException("Wrong session");
                switch (type)
                {
                    case Message.Motion:
                        var motionGeneration=r.ReadUInt32();
                        int motionBytes=checked((int)(r.BaseStream.Length-r.BaseStream.Position));
                        if(!worldReady || motionBytes>1100) throw new InvalidDataException("Unexpected character motion");
                        var motion=Wire.Bytes(r,motionBytes);
                        if(motionGeneration==generation) MotionReceived?.Invoke(motion);
                        break;
                    case Message.PeripheralChunk:
                        int total = r.ReadInt32(), at = r.ReadInt32();
                        if (!peripheralPending || at != peripheralOffset || total < 1 || total > Wire.MaxFrame ||
                            at >= total || (peripheral != null && peripheral.Length != total))
                            throw new InvalidDataException("Invalid peripheral chunk");
                        int count = Math.Min(Wire.ChunkSize,total-at);
                        var piece = Wire.Bytes(r,count); Wire.End(r);
                        if (peripheral == null) peripheral = new byte[total];
                        Buffer.BlockCopy(piece,0,peripheral,at,count); peripheralOffset += count;
                        if (peripheralOffset == total)
                        {
                            var complete = peripheral;
                            peripheral = null; peripheralOffset = 0; peripheralPending = false;
                            if (peripheralGeneration == generation) PeripheralReceived?.Invoke(complete);
                        }
                        break;
                    case Message.NativeBegin:
                        var nativeLength=r.ReadInt32(); Wire.End(r);
                        if (nativeSaveReady==null || NativeBytesExpected!=0) throw new InvalidDataException("Unexpected native save manifest.");
                        save=new NativeSaveSink(nativeSaveDirectory,nativeLength);
                        NativeBytesExpected=nativeLength; Status="Receiving native save"; break;
                    case Message.NativeChunk:
                        if (save==null) throw new InvalidDataException("Native save chunk without a transfer.");
                        save.Append(r);
                        NativeBytesReceived=save.Received;
                        if (save.Received==save.Length || save.Received % (4*Wire.ChunkSize)==0)
                            Send(Message.NativeWindow,w=>w.Write(NativeBytesReceived));
                        break;
                    case Message.NativeEnd:
                        var saveHash=Wire.Bytes(r,32); Wire.End(r);
                        if (save==null) throw new InvalidDataException("Native save end without a transfer.");
                        var localPath=save.Publish(saveHash);
                        save.Dispose(); save=null;
                        WaitingForNativeWorld=true; Status="Loading native world";
                        try { nativeSaveReady(localPath); }
                        catch { WaitingForNativeWorld=false; File.Delete(localPath); throw; }
                        break;
                    case Message.NativeError:
                        if (nativeSaveReady==null || worldReady) throw new InvalidDataException("Unexpected native save failure.");
                        var failure=Wire.Text(r); Wire.End(r);
                        save?.Dispose(); save=null; Rejected=true; Status=failure;
                        break;
                    case Message.Begin:
                        if (nativeSaveReady!=null && !worldReady) throw new InvalidDataException("View before native world load.");
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
                        if (!pending.TryGetValue(cmd,out var start)) return;
                        pending.Remove(cmd);
                        CommandLatencyMs?.Invoke((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
                        if (!result.StartsWith("NM1/",StringComparison.Ordinal))
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
            ViewReceived?.Invoke(View);
        }
        void Ack(Message type, ulong rev) => Send(type,w => {
            w.Write(generation); w.Write(rev); w.Write(received);
        });
    }
}
