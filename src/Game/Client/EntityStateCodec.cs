using System;
using System.Collections.Generic;
using System.IO;

namespace Queyang.Game
{
    public enum ColonyEntityKind : byte { Item = 1, Food, Plant, Critter, Construction, Storage, Egg }
    public enum ColonyItemPlace : byte { Ground, Carried, Stored }

    public struct ColonyIngredient
    {
        public int Tag;
        public float Stored, Required, Freshness;
    }

    public sealed class ColonyEntity
    {
        public const byte Worn = 32; // Item-kind flag for an occupied ONI equipment slot.
        public const byte ConstructionBar = 32; // Construction-kind flag for ONI's visible work bar.
        public const byte StorageSweepOnly = 32; // Storage-kind flag for ONI's sweep-only symbol.
        public int Id, Prefab, Element, Animation;
        public ushort Cell;
        public byte SubX, SubY, Frame, Flags;
        // 0..254 represents the native meter fraction, 255 means there is no reading.
        public byte StorageMeter = byte.MaxValue;
        public ColonyEntityKind Kind;
        public ColonyItemPlace Place;
        public int OwnerId = -1;
        public int WearBuild;
        // Four build symbols per byte. Each pair is absent, hidden (1), or visible (2).
        public byte[] WearSymbols = Array.Empty<byte>();
        public bool IsWorn => Kind == ColonyEntityKind.Item && Place == ColonyItemPlace.Carried && (Flags & Worn) != 0;
        public float Mass, Temperature;
        public float Available, Reserved;
        // Item: units / reserved units. Food: kcal / freshness / reserved units.
        // Plant: growth / seconds until harvest. Critter: age / lifespan / health fraction.
        // Construction: work fraction / delivered kg / required kg. Storage: kg / capacity / stacks.
        public float Value, Limit, Extra;
        public ColonyIngredient[] Contents = Array.Empty<ColonyIngredient>();
        public ushort IngredientTypes;
    }

    public sealed class ColonyEntityPage
    {
        public int From, Through = int.MaxValue;
        public ColonyEntity[] Entities = Array.Empty<ColonyEntity>();
    }

    public static class EntityStateCodec
    {
        public const int MaxCount = 52, MaxIngredients = 6;

        public static byte[] Encode(ColonyEntityPage page)
        {
            ValidateRange(page);
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write((byte)4); w.Write(page.From); w.Write(page.Through); w.Write((byte)page.Entities.Length);
                var ids = new HashSet<int>();
                foreach (var e in page.Entities)
                {
                    Validate(e, page, ids);
                    w.Write(e.Id); w.Write((byte)e.Kind); w.Write(e.Prefab); w.Write(e.Cell); w.Write(e.SubX); w.Write(e.SubY);
                    w.Write(e.Element); w.Write(e.Mass); w.Write(e.Temperature);
                    w.Write((byte)e.Place); w.Write(e.OwnerId); w.Write(e.Available); w.Write(e.Reserved);
                    w.Write(e.Animation); w.Write(e.Frame); w.Write(e.Flags);
                    if (e.IsWorn) { w.Write(e.WearBuild); w.Write((byte)e.WearSymbols.Length); w.Write(e.WearSymbols); }
                    if(e.Kind==ColonyEntityKind.Storage) w.Write(e.StorageMeter);
                    w.Write(e.Value); w.Write(e.Limit); w.Write(e.Extra); w.Write(e.IngredientTypes); w.Write((byte)e.Contents.Length);
                    foreach (var row in e.Contents) { w.Write(row.Tag); w.Write(row.Stored); w.Write(row.Required); w.Write(row.Freshness); }
                }
                return stream.ToArray();
            }
        }

        public static ColonyEntityPage Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 8192) throw new InvalidDataException("Invalid entity payload length.");
            using (var stream = new MemoryStream(bytes, false))
            using (var r = new BinaryReader(stream))
            {
                byte version = r.ReadByte();
                if (version < 1 || version > 4) throw new InvalidDataException("Unknown entity payload version.");
                var page = new ColonyEntityPage { From = r.ReadInt32(), Through = r.ReadInt32() };
                int count = r.ReadByte();
                int max = version == 1 ? 56 : MaxCount;
                if (count > max) throw new InvalidDataException("Too many entities.");
                page.Entities = new ColonyEntity[count];
                ValidateRange(page, max);
                var ids = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    var e = new ColonyEntity { Id = r.ReadInt32(), Kind = (ColonyEntityKind)r.ReadByte(), Prefab = r.ReadInt32(),
                        Cell = r.ReadUInt16(), SubX = r.ReadByte(), SubY = r.ReadByte(), Element = r.ReadInt32(),
                        Mass = r.ReadSingle(), Temperature = r.ReadSingle() };
                    if (version >= 2) { e.Place = (ColonyItemPlace)r.ReadByte(); e.OwnerId = r.ReadInt32();
                        e.Available = r.ReadSingle(); e.Reserved = r.ReadSingle(); }
                    e.Animation = r.ReadInt32(); e.Frame = r.ReadByte(); e.Flags = r.ReadByte();
                    if (version >= 3 && e.IsWorn)
                    {
                        e.WearBuild = r.ReadInt32(); int length = r.ReadByte();
                        if (length > 64) throw new InvalidDataException("Too many equipment symbols.");
                        e.WearSymbols = r.ReadBytes(length);
                        if (e.WearSymbols.Length != length) throw new EndOfStreamException();
                    }
                    if(version>=4 && e.Kind==ColonyEntityKind.Storage) e.StorageMeter=r.ReadByte();
                    e.Value = r.ReadSingle(); e.Limit = r.ReadSingle(); e.Extra = r.ReadSingle(); e.IngredientTypes = r.ReadUInt16();
                    int n = r.ReadByte();
                    if (n > MaxIngredients) throw new InvalidDataException("Too many material rows.");
                    e.Contents = new ColonyIngredient[n];
                    for (int j = 0; j < n; j++)
                        e.Contents[j] = new ColonyIngredient { Tag = r.ReadInt32(), Stored = r.ReadSingle(),
                            Required = r.ReadSingle(), Freshness = r.ReadSingle() };
                    Validate(e, page, ids);
                    page.Entities[i] = e;
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing entity payload.");
                return page;
            }
        }

        static void ValidateRange(ColonyEntityPage p, int max = MaxCount)
        {
            if (p == null || p.From < 0 || p.Through < p.From || p.Entities == null || p.Entities.Length > max)
                throw new InvalidDataException("Invalid entity page.");
        }
        static bool Number(float f, float min = 0) => !float.IsNaN(f) && !float.IsInfinity(f) && f >= min;
        static void Validate(ColonyEntity e, ColonyEntityPage page, HashSet<int> ids)
        {
            if (e == null || e.Id < page.From || e.Id > page.Through || !ids.Add(e.Id) ||
                e.Cell >= ColonyView.Width * ColonyView.Height || e.Kind < ColonyEntityKind.Item || e.Kind > ColonyEntityKind.Egg ||
                e.Place > ColonyItemPlace.Stored || (e.Place == ColonyItemPlace.Ground ? e.OwnerId != -1 : e.OwnerId < 0 || e.OwnerId == e.Id) ||
                !Number(e.Available) || !Number(e.Reserved) || (e.Flags & ~63) != 0 ||
                !Number(e.Mass) || !Number(e.Temperature) || !Number(e.Value, -1) || !Number(e.Limit, -1) || !Number(e.Extra, -1) ||
                e.Contents == null || e.Contents.Length > MaxIngredients || e.Contents.Length > e.IngredientTypes)
                throw new InvalidDataException("Invalid entity state.");
            foreach (var row in e.Contents)
                if (!Number(row.Stored) || !Number(row.Required) || !Number(row.Freshness, -1) || row.Freshness > 1)
                    throw new InvalidDataException("Invalid material quantity.");
            if (e.WearSymbols == null || e.WearSymbols.Length > 64 ||
                (e.IsWorn ? e.Contents.Length != 0 : e.WearBuild != 0 || e.WearSymbols.Length != 0) ||
                (e.WearBuild == 0 && e.WearSymbols.Length != 0))
                throw new InvalidDataException("Invalid worn equipment state.");
            foreach (byte value in e.WearSymbols)
                if ((value & (value >> 1) & 0x55) != 0) throw new InvalidDataException("Invalid equipment visibility.");
        }
    }
}
