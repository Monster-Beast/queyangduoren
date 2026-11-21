using System;
using System.IO;
using System.Security.Cryptography;

namespace Queyang.Core
{
    // One immutable file handle per joining peer. Only one chunk is held in managed memory.
    internal sealed class NativeSaveSource : IDisposable
    {
        private readonly Stream file;
        private readonly SHA256 hash;
        private readonly byte[] buffer = new byte[Wire.ChunkSize];
        internal readonly int Length;
        internal int Sent { get; private set; }
        internal int Confirmed { get; private set; }

        internal NativeSaveSource(Stream stream)
        {
            file = stream ?? throw new InvalidDataException("Missing native save.");
            try
            {
                if (!file.CanRead || !file.CanSeek || file.Position != 0)
                    throw new InvalidDataException("Expected an immutable save stream at position zero.");
                long length = file.Length;
                if (length < 1 || length > Wire.MaxSnapshot) throw new InvalidDataException("Native save size outside limits.");
                Length = (int)length;
                hash = SHA256.Create();
            }
            catch { file.Dispose(); throw; }
        }

        internal void WriteChunk(BinaryWriter packet)
        {
            int count = Math.Min(Length - Sent, buffer.Length);
            int filled = 0;
            while (filled < count)
            {
                int next = file.Read(buffer,filled,count-filled);
                if (next == 0) throw new EndOfStreamException("Native snapshot ended early.");
                filled += next;
            }
            packet.Write(Sent);
            packet.Write(buffer,0,count);
            hash.TransformBlock(buffer,0,count,buffer,0);
            Sent += count;
        }

        internal bool Confirm(int offset)
        {
            if (offset < 0 || offset > Sent || (offset != Length && offset % Wire.ChunkSize != 0))
                throw new InvalidDataException("Invalid native save acknowledgement.");
            if (offset <= Confirmed) return false;
            Confirmed = offset;
            return true;
        }

        internal byte[] Finish()
        {
            if (Confirmed != Length || file.Length != Length)
                throw new InvalidDataException("Native snapshot changed or was incomplete.");
            hash.TransformFinalBlock(Array.Empty<byte>(),0,0);
            return hash.Hash;
        }

        public void Dispose()
        {
            try { hash.Dispose(); } finally { file.Dispose(); }
        }
    }

    // The peer supplies only bytes. Both the temporary and published names are generated locally.
    internal sealed class NativeSaveSink : IDisposable
    {
        private readonly string partial, complete;
        private readonly SHA256 hash;
        private FileStream file;
        internal readonly int Length;
        internal int Received { get; private set; }

        internal NativeSaveSink(string directory, int length)
        {
            if (length < 1 || length > Wire.MaxSnapshot) throw new InvalidDataException("Invalid native save length.");
            Length = length;
            Directory.CreateDirectory(directory);
            string name = Path.Combine(directory,"guest-" + Guid.NewGuid().ToString("N"));
            partial = name + ".part";
            complete = name + ".sav";
            hash = SHA256.Create();
            try { file = new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,Wire.ChunkSize); }
            catch { hash.Dispose(); throw; }
        }

        internal void Append(BinaryReader r)
        {
            int offset = r.ReadInt32();
            int expected = Math.Min(Wire.ChunkSize,Length-Received);
            if (offset != Received || expected == 0 || r.BaseStream.Length-r.BaseStream.Position != expected)
                throw new InvalidDataException("Unexpected native save chunk.");
            byte[] bytes = Wire.Bytes(r,expected);
            file.Write(bytes,0,bytes.Length);
            hash.TransformBlock(bytes,0,bytes.Length,bytes,0);
            Received += bytes.Length;
        }

        internal string Publish(byte[] digest)
        {
            if (Received != Length) throw new InvalidDataException("Incomplete native save.");
            hash.TransformFinalBlock(Array.Empty<byte>(),0,0);
            if (!ViewPatch.Equal(hash.Hash,digest)) throw new InvalidDataException("Native save SHA-256 mismatch.");
            file.Dispose();
            file = null;
            File.Move(partial,complete);
            return complete;
        }

        public void Dispose()
        {
            try { file?.Dispose(); }
            finally
            {
                try { File.Delete(partial); } finally { hash.Dispose(); }
            }
        }
    }
}
