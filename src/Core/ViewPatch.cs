using System;
using System.IO;
using System.Security.Cryptography;

namespace Queyang.Core
{
    // Replace the changed middle range of the previous immutable view; the rest is shared on the wire.
    public sealed class ViewPatch
    {
        public readonly int Prefix, Suffix, Length;
        public readonly byte[] Digest;
        public ViewPatch(int prefix, int suffix, int length, byte[] digest)
        {
            if (length < 0 || length > Wire.MaxSnapshot || prefix < 0 || suffix < 0 ||
                prefix > length || suffix > length - prefix || digest.Length != 32)
                throw new InvalidDataException("Invalid view patch");
            Prefix = prefix; Suffix = suffix; Length = length; Digest = digest;
        }

        public int Changed => Length - Prefix - Suffix;
        public static ViewPatch Compare(byte[] old, byte[] next)
        {
            int prefix = 0, suffix = 0, min = Math.Min(old.Length, next.Length);
            while (prefix < min && old[prefix] == next[prefix]) prefix++;
            while (suffix < min - prefix && old[old.Length-1-suffix] == next[next.Length-1-suffix]) suffix++;
            using (var sha = SHA256.Create()) return new ViewPatch(prefix, suffix, next.Length, sha.ComputeHash(next));
        }

        public byte[] Prepare(byte[] old)
        {
            if (Prefix > old.Length || Suffix > old.Length - Prefix) throw new InvalidDataException("Wrong patch base");
            var buffer = new byte[Length];
            Buffer.BlockCopy(old, 0, buffer, 0, Prefix);
            Buffer.BlockCopy(old, old.Length - Suffix, buffer, Length - Suffix, Suffix);
            return buffer;
        }

        public bool Verify(byte[] buffer)
        {
            using (var sha = SHA256.Create()) return Equal(sha.ComputeHash(buffer), Digest);
        }

        public static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
