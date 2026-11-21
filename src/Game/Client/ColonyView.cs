using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Queyang.Game
{
    public struct ColonyCell
    {
        public byte Flags;
        public int TargetId;
        public ushort Connections;
        public byte Utilities;
        public byte R, G, B, BuildingStyle, Progress;
        public string Element, Building;
        public float Mass, Temperature;
    }

    public struct ColonyDupe
    {
        public int Id, X, Y;
        public byte SubX, SubY;
        public string Name, Activity;
    }

    // A display-only sample. Rows start at the bottom, matching ONI's grid.
    public sealed class ColonyView
    {
        public const int Width = 24, Height = 16;
        private const ushort Version = 4;
        private const int MaxNames = Width * Height * 2 + 64 * 2 + 1;
        private const int MaxNameBytes = 58;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public int X, Y;
        public int Cycle;
        public float CycleFraction;
        public bool Paused;
        public byte Speed;
        public ColonyCell[] Cells = new ColonyCell[Width * Height];
        public ColonyDupe[] Dupes = Array.Empty<ColonyDupe>();

        public byte[] Encode()
        {
            if (Cells == null || Cells.Length != Width * Height || Dupes == null || Dupes.Length > 64)
                throw new InvalidDataException("Invalid viewport size.");
            ValidateClock();
            var ids = new Dictionary<string, ushort>(StringComparer.Ordinal);
            var names = new List<byte[]>();
            AddName(string.Empty, ids, names);
            foreach (var c in Cells)
            {
                Validate(c);
                AddName(c.Element, ids, names);
                AddName(c.Building, ids, names);
            }
            foreach (var d in Dupes) { AddName(d.Name, ids, names); AddName(d.Activity, ids, names); }
            ValidateDupes(Dupes);
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Version);
                w.Write(X); w.Write(Y);
                w.Write(Cycle); w.Write(CycleFraction); w.Write(Paused); w.Write(Speed);
                w.Write((ushort)names.Count);
                foreach (var name in names) { w.Write((byte)name.Length); w.Write(name); }
                foreach (var c in Cells)
                {
                    w.Write(c.Flags);
                    w.Write(c.TargetId);
                    w.Write(c.Connections);
                    w.Write(c.Utilities);
                    w.Write(c.R); w.Write(c.G); w.Write(c.B); w.Write(c.BuildingStyle); w.Write(c.Progress);
                    w.Write(ids[c.Element ?? string.Empty]);
                    w.Write(ids[c.Building ?? string.Empty]);
                    w.Write(c.Mass);
                    w.Write(c.Temperature);
                }
                w.Write((byte)Dupes.Length);
                foreach (var d in Dupes)
                {
                    w.Write(d.Id); w.Write(d.X); w.Write(d.Y);
                    w.Write(d.SubX); w.Write(d.SubY);
                    w.Write(ids[d.Name ?? string.Empty]);
                    w.Write(ids[d.Activity ?? string.Empty]);
                }
                return stream.ToArray();
            }
        }

        private static void AddName(string name, Dictionary<string, ushort> ids, List<byte[]> names)
        {
            name = name ?? string.Empty;
            if (ids.ContainsKey(name)) return;
            var encoder = Encoding.UTF8.GetEncoder();
            var buffer = new byte[MaxNameBytes];
            // Convert truncates at a complete UTF-8 character, even for non-BMP names.
            int count = Math.Min(name.Length, MaxNameBytes);
            encoder.Convert(name.ToCharArray(0, count), 0, count, buffer, 0, buffer.Length,
                count == name.Length, out _, out int used, out _);
            Array.Resize(ref buffer, used);
            ids.Add(name, (ushort)names.Count);
            names.Add(buffer);
        }

        public static ColonyView Decode(byte[] payload)
        {
            if (payload == null || payload.Length > 65520)
                throw new InvalidDataException("Invalid viewport payload size.");
            using (var stream = new MemoryStream(payload, false))
            using (var r = new BinaryReader(stream))
            {
                if (r.ReadUInt16() != Version) throw new InvalidDataException("Unknown viewport version.");
                var v = new ColonyView { X = r.ReadInt32(), Y = r.ReadInt32() };
                if (v.X < 0 || v.Y < 0 || v.X > int.MaxValue - Width || v.Y > int.MaxValue - Height)
                    throw new InvalidDataException("Invalid viewport origin.");
                v.Cycle = r.ReadInt32(); v.CycleFraction = r.ReadSingle();
                byte paused = r.ReadByte();
                if (paused > 1) throw new InvalidDataException("Invalid clock pause flag.");
                v.Paused = paused == 1;
                v.Speed = r.ReadByte();
                v.ValidateClock();
                int count = r.ReadUInt16();
                if (count < 1 || count > MaxNames) throw new InvalidDataException("Invalid viewport string count.");
                var names = new string[count];
                for (int i = 0; i < count; i++)
                {
                    int bytes = r.ReadByte();
                    if (bytes > MaxNameBytes) throw new InvalidDataException("Invalid viewport string length.");
                    byte[] encoded = r.ReadBytes(bytes);
                    if (encoded.Length != bytes) throw new EndOfStreamException();
                    try { names[i] = Utf8.GetString(encoded); }
                    catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF-8.", e); }
                }
                for (int i = 0; i < v.Cells.Length; i++)
                {
                    var cell = new ColonyCell
                    {
                        Flags = r.ReadByte(), TargetId = r.ReadInt32(),
                        Connections = r.ReadUInt16(), Utilities = r.ReadByte(),
                        R = r.ReadByte(), G = r.ReadByte(), B = r.ReadByte(), BuildingStyle = r.ReadByte(), Progress = r.ReadByte(),
                        Element = ReadName(r, names), Building = ReadName(r, names),
                        Mass = r.ReadSingle(), Temperature = r.ReadSingle()
                    };
                    Validate(cell);
                    v.Cells[i] = cell;
                }
                int n = r.ReadByte();
                if (n > 64) throw new InvalidDataException("Invalid duplicant count.");
                v.Dupes = new ColonyDupe[n];
                for (int i = 0; i < n; i++)
                    v.Dupes[i] = new ColonyDupe { Id = r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(),
                        SubX = r.ReadByte(), SubY = r.ReadByte(), Name = ReadName(r, names), Activity = ReadName(r, names) };
                ValidateDupes(v.Dupes);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing viewport bytes.");
                return v;
            }
        }

        private static string ReadName(BinaryReader r, string[] names)
        {
            int i = r.ReadUInt16();
            if (i >= names.Length) throw new InvalidDataException("Invalid name reference.");
            return names[i];
        }

        private static void Validate(ColonyCell c)
        {
            if ((c.Flags & ~127) != 0 || (c.Utilities & ~31) != 0 || c.TargetId < 0 || c.Progress > 100 ||
                float.IsNaN(c.Mass) || float.IsInfinity(c.Mass) ||
                float.IsNaN(c.Temperature) || float.IsInfinity(c.Temperature))
                throw new InvalidDataException("Invalid viewport cell.");
        }

        private void ValidateClock()
        {
            if (Cycle < 0 || Speed > 10 || float.IsNaN(CycleFraction) || CycleFraction < 0 || CycleFraction > 1)
                throw new InvalidDataException("Invalid colony clock.");
        }

        private static void ValidateDupes(ColonyDupe[] dupes)
        {
            if (dupes.Length == 0) return;
            var ids = new HashSet<int>();
            foreach (var dupe in dupes)
                if (dupe.Id <= 0 || !ids.Add(dupe.Id)) throw new InvalidDataException("Invalid duplicant identity.");
        }
    }
}
