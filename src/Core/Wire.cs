using System;
using System.IO;
using System.Text;

namespace Queyang.Core
{
    public enum Message : ushort
    {
        Hello = 1, Welcome, Reject, Subscribe, Begin, Chunk, Applied, Resync,
        Command, Result, Ping, Pong, Window
    }

    public static class Wire
    {
        public const int MaxFrame = 65536;
        public const int ChunkSize = 32768;
        public const int MaxSnapshot = 128 * 1024 * 1024;
        internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        const uint Magic = 0x504d5951;

        public static byte[] Pack(Message type, Action<BinaryWriter> payload)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Utf8, true))
            {
                w.Write(Magic); w.Write((ushort)1); w.Write((ushort)0);
                w.Write((ushort)type); w.Write((ushort)0); w.Write(0);
                payload(w);
                if (stream.Length > MaxFrame) throw new InvalidDataException("Frame too large");
                var length = (int)stream.Length - 16;
                stream.Position = 12;
                w.Write(length);
                return stream.ToArray();
            }
        }

        public static void Read(byte[] frame, Action<Message, BinaryReader> handle)
        {
            if (frame == null || frame.Length < 16 || frame.Length > MaxFrame)
                throw new InvalidDataException("Invalid frame size");
            using (var s = new MemoryStream(frame, false))
            using (var r = new BinaryReader(s, Utf8))
            {
                if (r.ReadUInt32() != Magic || r.ReadUInt16() != 1 || r.ReadUInt16() != 0)
                    throw new InvalidDataException("Incompatible protocol");
                var type = (Message)r.ReadUInt16();
                if (!Enum.IsDefined(typeof(Message), type) || r.ReadUInt16() != 0 || r.ReadInt32() != frame.Length - 16)
                    throw new InvalidDataException("Invalid header");
                handle(type, r);
                End(r);
            }
        }

        public static void End(BinaryReader r)
        {
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing payload");
        }

        public static void Text(BinaryWriter w, string value, int limit = 1024)
        {
            var bytes = Utf8.GetBytes(value);
            if (bytes.Length > limit) throw new InvalidDataException("Text exceeds field limit");
            w.Write((ushort)bytes.Length); w.Write(bytes);
        }

        public static string Text(BinaryReader r, int limit = 1024)
        {
            int length = r.ReadUInt16();
            if (length > limit) throw new InvalidDataException("Text exceeds field limit");
            return Utf8.GetString(Bytes(r, length));
        }

        public static byte[] Bytes(BinaryReader r, int length)
        {
            if (length < 0 || length > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Truncated payload");
            return r.ReadBytes(length);
        }
    }
}
