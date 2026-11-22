using System;
using System.IO;
using System.Collections.Generic;
using System.IO.Compression;

namespace Queyang.Game
{
    public enum OrderKind : byte { Dig, Mop, Build, Deconstruct, Sweep, Harvest }
    public struct OrderMark
    {
        // Cell is relative to this viewport on the wire.
        public int Cell, Author;
        public OrderKind Kind;
    }

    public sealed partial class ColonyView
    {
        public OrderMark[] OrderMarks;
        public byte OrderFirstRow, OrderRows;
        public readonly Dictionary<int,string> OrderNames = new Dictionary<int,string>();

        public static string CleanOrderName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var name=new System.Text.StringBuilder();
            for (int i=0,n=0; i<raw.Length && n<16; i++)
            {
                var category=System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw,i);
                if (category==System.Globalization.UnicodeCategory.Control || category==System.Globalization.UnicodeCategory.Format ||
                    category==System.Globalization.UnicodeCategory.Surrogate || category==System.Globalization.UnicodeCategory.LineSeparator ||
                    category==System.Globalization.UnicodeCategory.ParagraphSeparator) continue;
                char c=raw[i];
                if (char.IsHighSurrogate(c)) name.Append(c).Append(raw[++i]);
                else name.Append(c=='<' ? '＜' : c=='>' ? '＞' : c);
                n++;
            }
            return name.ToString().Trim();
        }

        void WriteOrderMarks(BinaryWriter w)
        {
            if (OrderRows == 0 || OrderFirstRow + OrderRows > Height || OrderMarks.Length > ushort.MaxValue)
                throw new InvalidDataException("Invalid order ownership page");
            w.Write(OrderFirstRow); w.Write(OrderRows); w.Write((ushort)OrderMarks.Length);
            foreach (var mark in OrderMarks)
            {
                ValidateMark(mark);
                w.Write((ushort)((mark.Cell << 3) | (int)mark.Kind));
                w.Write(mark.Author);
            }
            w.Write((ushort)OrderNames.Count);
            foreach (var player in OrderNames)
            {
                byte[] name=System.Text.Encoding.UTF8.GetBytes(player.Value);
                if (player.Key<1 || name.Length>64 || name.Length==0 || CleanOrderName(player.Value)!=player.Value)
                    throw new InvalidDataException("Invalid order creator name");
                w.Write(player.Key); w.Write((byte)name.Length); w.Write(name);
            }
        }

        void ValidateMark(OrderMark mark)
        {
            if (mark.Cell < OrderFirstRow * Width || mark.Cell >= (OrderFirstRow + OrderRows) * Width ||
                (Cells[mark.Cell].Flags & 1) == 0 || mark.Kind > OrderKind.Harvest || mark.Author <= 0)
                throw new InvalidDataException("Invalid order attribution");
        }

        void ReadOrderMarks(BinaryReader r)
        {
            OrderFirstRow = r.ReadByte(); OrderRows = r.ReadByte();
            int count = r.ReadUInt16();
            if (OrderRows == 0 || OrderFirstRow + OrderRows > Height || count * 6 > r.BaseStream.Length-r.BaseStream.Position)
                throw new InvalidDataException("Invalid order attribution coverage");
            OrderMarks = new OrderMark[count];
            for (int i=0; i<count; i++)
            {
                ushort key = r.ReadUInt16();
                var mark = new OrderMark { Cell = key >> 3, Kind=(OrderKind)(key & 7), Author=r.ReadInt32() };
                ValidateMark(mark);
                OrderMarks[i] = mark;
            }
            int names=r.ReadUInt16();
            if (names>count) throw new InvalidDataException("Invalid creator roster");
            var utf8=new System.Text.UTF8Encoding(false,true);
            for (int i=0;i<names;i++)
            {
                int author=r.ReadInt32(), length=r.ReadByte();
                if (author<1 || length>64 || length==0 || OrderNames.ContainsKey(author))
                    throw new InvalidDataException("Invalid creator name");
                byte[] bytes=r.ReadBytes(length);
                if (bytes.Length!=length) throw new EndOfStreamException();
                string name=utf8.GetString(bytes);
                if (CleanOrderName(name)!=name) throw new InvalidDataException("Invalid creator name");
                OrderNames.Add(author,name);
            }
        }
        public const int MaxEntityPayload = 8192;
        const int MaxViewport = 65520, MaxInflated = 160 * 1024;
        const int EnvironmentPages = 4, CellsPerPage = Width * Height / EnvironmentPages;
        public byte[] EntityPayload;
        public bool HasEnvironment;
        public int EnvironmentPage;
        bool partialEnvironment;

        void WriteOrders(BinaryWriter w)
        {
            foreach (var c in Cells)
            {
                if ((c.Flags & 1) == 0) continue;
                if (c.OrderPriority > 9 || c.OrderTargetId < 0 || c.SeparateOrderPriority > 9 ||
                    (c.DigMaterial!=0 && (c.Flags & 8)==0) || (c.HasBlueprintTint && (c.Flags & 32)==0) ||
                    (c.OrderTargetId == 0 && c.SeparateOrderPriority != 0))
                    throw new InvalidDataException("Invalid order status.");
                w.Write((byte)(c.OrderPriority | (c.MopOrdered ? 16 : 0) | (c.OrderTargetId != 0 ? 32 : 0) |
                    (c.DigMaterial != 0 ? 64 : 0) | (c.HasBlueprintTint ? 128 : 0)));
                if (c.OrderTargetId != 0) { w.Write(c.OrderTargetId); w.Write(c.SeparateOrderPriority); }
                if (c.DigMaterial != 0) { w.Write(c.DigMaterial); w.Write(c.DigTint); }
                if (c.HasBlueprintTint) w.Write(c.BlueprintTint);
            }
        }

        void ReadOrders(BinaryReader r)
        {
            for (int i = 0; i < Cells.Length; i++)
            {
                if ((Cells[i].Flags & 1) == 0) continue;
                byte order = r.ReadByte();
                if ((order & 15) > 9 || ((order & 64) != 0 && (Cells[i].Flags & 8) == 0) ||
                    ((order & 128) != 0 && (Cells[i].Flags & 32) == 0)) throw new InvalidDataException("Invalid order status.");
                Cells[i].OrderPriority = (byte)(order & 15); Cells[i].MopOrdered = (order & 16) != 0;
                if ((order & 32) != 0)
                {
                    Cells[i].OrderTargetId=r.ReadInt32(); Cells[i].SeparateOrderPriority=r.ReadByte();
                    if (Cells[i].OrderTargetId<=0 || Cells[i].SeparateOrderPriority>9) throw new InvalidDataException("Invalid co-located order.");
                }
                if ((order & 64) != 0)
                {
                    Cells[i].DigMaterial=r.ReadByte(); Cells[i].DigTint=r.ReadUInt32();
                    if (Cells[i].DigMaterial==0) throw new InvalidDataException("Invalid dig material.");
                }
                if ((order & 128) != 0) { Cells[i].HasBlueprintTint=true; Cells[i].BlueprintTint=r.ReadUInt32(); }
            }
        }

        public bool CoversEnvironment(int index) => HasEnvironment && (uint)index < Cells.Length &&
            (Cells[index].Flags & 1) != 0 && (!partialEnvironment || index / CellsPerPage == EnvironmentPage);

        internal void WriteExtensions(BinaryWriter w, bool pageEnvironment)
        {
            ValidateNativeIdentities();
            if (HasEnvironment)
            {
                if ((uint)EnvironmentPage >= EnvironmentPages) throw new InvalidDataException("Invalid environment page.");
                bool partial = partialEnvironment || pageEnvironment;
                w.Write((byte)(partial ? EnvironmentPage + 1 : 0));
                for (int i = 0; i < Cells.Length; i++)
                {
                    if ((Cells[i].Flags & 1) == 0 || (partial && i / CellsPerPage != EnvironmentPage)) continue;
                    WriteEnvironment(w, Cells[i]);
                }
                foreach (var pile in Piles) WriteGerms(w, pile.Material.Germs, pile.Material.GermType);
            }
            if (EntityPayload != null)
            {
                if (EntityPayload.Length > MaxEntityPayload) throw new InvalidDataException("Entity page too large.");
                w.Write((ushort)EntityPayload.Length); w.Write(EntityPayload);
            }
        }

        void ReadExtensions(BinaryReader r, byte flags)
        {
            ValidateNativeIdentities();
            HasEnvironment = (flags & 1) != 0;
            if (HasEnvironment)
            {
                int page = r.ReadByte();
                if (page > EnvironmentPages) throw new InvalidDataException("Invalid environment page.");
                partialEnvironment = page != 0;
                EnvironmentPage = Math.Max(0, page - 1);
                for (int i = 0; i < Cells.Length; i++)
                    if (CoversEnvironment(i)) ReadEnvironment(r, ref Cells[i]);
                for (int i = 0; i < Piles.Length; i++) ReadGerms(r, out Piles[i].Material.Germs, out Piles[i].Material.GermType);
            }
            if ((flags & 2) != 0)
            {
                int n = r.ReadUInt16();
                if (n > MaxEntityPayload) throw new InvalidDataException("Entity page too large.");
                EntityPayload = r.ReadBytes(n);
                if (EntityPayload.Length != n) throw new EndOfStreamException();
            }
        }

        static bool HasGerms(MaterialSample s) => s.Germs != 0 || s.GermType != 0;
        static void WriteEnvironment(BinaryWriter w, ColonyCell c)
        {
            byte flags = 0;
            if (c.Germs != 0 || c.GermType != 0) flags |= 1;
            if (c.Lux != 0) flags |= 2;
            if (c.Decor != 0) flags |= 4;
            if (c.RailPresent) flags |= 8;
            if ((c.Utilities & 2) != 0 && HasGerms(c.GasPipe)) flags |= 16;
            if ((c.Utilities & 4) != 0 && HasGerms(c.LiquidPipe)) flags |= 32;
            if (c.BuildingHash != 0 && c.AnchorDX == 0 && c.AnchorDY == 0 && HasGerms(c.Structure)) flags |= 64;
            if (c.RoomType != 0) flags |= 128;
            w.Write(flags);
            if ((flags & 1) != 0) WriteGerms(w, c.Germs, c.GermType);
            if ((flags & 2) != 0) { if (c.Lux < 0) throw new InvalidDataException("Invalid lux."); w.Write(c.Lux); }
            if ((flags & 4) != 0) { CheckFinite(c.Decor); w.Write(c.Decor); }
            if ((flags & 8) != 0)
            {
                CheckFinite(c.RailMass);
                if (c.RailMass < 0 || c.RailConnections > 15) throw new InvalidDataException("Invalid rail data.");
                w.Write(c.RailConnections); w.Write(c.RailItem); w.Write(c.RailMass);
            }
            if ((flags & 16) != 0) WriteGerms(w, c.GasPipe.Germs, c.GasPipe.GermType);
            if ((flags & 32) != 0) WriteGerms(w, c.LiquidPipe.Germs, c.LiquidPipe.GermType);
            if ((flags & 64) != 0) WriteGerms(w, c.Structure.Germs, c.Structure.GermType);
            if ((flags & 128) != 0)
            {
                ValidateRoom(c);
                w.Write(c.RoomType); w.Write(c.RoomAnchor); w.Write(c.RoomSize);
            }
        }

        static void ReadEnvironment(BinaryReader r, ref ColonyCell c)
        {
            byte flags = r.ReadByte();
            if (((flags & 16) != 0 && (c.Utilities & 2) == 0) ||
                ((flags & 32) != 0 && (c.Utilities & 4) == 0) ||
                ((flags & 64) != 0 && (c.BuildingHash == 0 || c.AnchorDX != 0 || c.AnchorDY != 0)))
                throw new InvalidDataException("Invalid environment fields.");
            if ((flags & 1) != 0) ReadGerms(r, out c.Germs, out c.GermType);
            if ((flags & 2) != 0) { c.Lux = r.ReadInt32(); if (c.Lux < 0) throw new InvalidDataException("Invalid lux."); }
            if ((flags & 4) != 0) { c.Decor = r.ReadSingle(); CheckFinite(c.Decor); }
            c.RailPresent = (flags & 8) != 0;
            if (c.RailPresent)
            {
                c.RailConnections = r.ReadByte(); c.RailItem = r.ReadInt32(); c.RailMass = ReadAmount(r);
                if (c.RailConnections > 15) throw new InvalidDataException("Invalid rail directions.");
            }
            if ((flags & 16) != 0) ReadGerms(r, out c.GasPipe.Germs, out c.GasPipe.GermType);
            if ((flags & 32) != 0) ReadGerms(r, out c.LiquidPipe.Germs, out c.LiquidPipe.GermType);
            if ((flags & 64) != 0) ReadGerms(r, out c.Structure.Germs, out c.Structure.GermType);
            if ((flags & 128) != 0)
            {
                c.RoomType = r.ReadInt32(); c.RoomAnchor = r.ReadInt32(); c.RoomSize = r.ReadInt32();
                ValidateRoom(c);
            }
        }
        static void ValidateRoom(ColonyCell c)
        {
            if (c.RoomType == 0 || c.RoomAnchor < 0 || c.RoomSize <= 0)
                throw new InvalidDataException("Invalid room membership.");
        }
        static void WriteGerms(BinaryWriter w, int count, byte type)
        {
            if (count < 0) throw new InvalidDataException("Invalid germ count.");
            w.Write(count); w.Write(type);
        }
        static void ReadGerms(BinaryReader r, out int count, out byte type)
        {
            count = r.ReadInt32(); type = r.ReadByte();
            if (count < 0) throw new InvalidDataException("Invalid germ count.");
        }
        static void CheckFinite(float n)
        {
            if (float.IsNaN(n) || float.IsInfinity(n)) throw new InvalidDataException("Invalid environmental reading.");
        }

        void ValidateNativeIdentities()
        {
            if (DupeFrom<0 || DupeThrough<DupeFrom) throw new InvalidDataException("Invalid duplicant coverage.");
            if (Dupes.Length == 0) return;
            var saves = new HashSet<int>();
            foreach (var dupe in Dupes)
            {
                long dx = (long)dupe.X - X, dy = (long)dupe.Y - Y;
                if (!CoversDupeId(dupe.SaveId) || !saves.Add(dupe.SaveId) || dx < 0 || dx >= Width || dy < 0 || dy >= Height ||
                    (Cells[(int)dy * Width + (int)dx].Flags & 1) == 0)
                    throw new InvalidDataException("Invalid native duplicant identity or visibility.");
            }
        }

        void WriteDupePoses(BinaryWriter w)
        {
            if (DupePoseTick<1) throw new InvalidDataException("Invalid character sample time");
            w.Write(DupePoseTick);
            foreach (var dupe in Dupes)
            {
                ValidatePose(dupe);
                var p=dupe.Pose;
                w.Write(p.X); w.Write(p.Y); w.Write(p.M00); w.Write(p.M01); w.Write(p.M02);
                w.Write(p.M10); w.Write(p.M11); w.Write(p.M12); w.Write(p.File); w.Write(p.Frame);
            }
        }
        void ReadDupePoses(BinaryReader r)
        {
            DupePoseTick=r.ReadInt32();
            if (DupePoseTick<1) throw new InvalidDataException("Invalid character sample time");
            for (int i=0;i<Dupes.Length;i++)
            {
                Dupes[i].Pose=new ColonyDupePose { X=r.ReadSingle(),Y=r.ReadSingle(),M00=r.ReadSingle(),M01=r.ReadSingle(),M02=r.ReadSingle(),
                    M10=r.ReadSingle(),M11=r.ReadSingle(),M12=r.ReadSingle(),File=r.ReadInt32(),Frame=r.ReadInt32() };
                ValidatePose(Dupes[i]);
            }
        }
        internal static void ValidatePose(ColonyDupe dupe)
        {
            var p=dupe.Pose;
            // ONI adds .05 to world Y before finding the containing cell.
            if (!(p.X>=dupe.X-.001f && p.X<=dupe.X+1.001f && p.Y>=dupe.Y-.051f && p.Y<=dupe.Y+1.001f) ||
                p.Frame < -1 || p.Frame > 65535) throw new InvalidDataException($"Invalid character {dupe.SaveId} cell=({dupe.X},{dupe.Y}) root=({p.X:R},{p.Y:R}) frame={p.Frame}");
            CheckFinite(p.M00); CheckFinite(p.M01); CheckFinite(p.M02);
            CheckFinite(p.M10); CheckFinite(p.M11); CheckFinite(p.M12);
        }

        static byte[] CompressViewport(MemoryStream raw, bool dense)
        {
            if (raw.Length > MaxInflated) throw new InvalidDataException("Extended viewport exceeds decoded limit.");
            using (var packed = new MemoryStream())
            using (var w = new BinaryWriter(packed))
            {
                w.Write((ushort)8); w.Write((byte)128); w.Write((int)raw.Length); w.Write((int)0);
                using (var gzip = new GZipStream(packed, dense ? CompressionLevel.Optimal : CompressionLevel.Fastest, true))
                {
                    raw.Position = 0; raw.CopyTo(gzip);
                }
                if (packed.Length > MaxViewport) return null;
                packed.Position = 7; w.Write((int)packed.Length - 11);
                return packed.ToArray();
            }
        }
        static ColonyView InflateViewport(BinaryReader r)
        {
            int length = r.ReadInt32(), packed = r.ReadInt32();
            if (length <= 0 || length > MaxInflated || packed <= 0 || packed != r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Invalid compressed viewport lengths.");
            using (var gzip = new GZipStream(r.BaseStream, CompressionMode.Decompress, true))
            {
                byte[] raw = new byte[length];
                int n = 0;
                while (n < raw.Length)
                {
                    int got = gzip.Read(raw, n, raw.Length - n);
                    if (got == 0) throw new InvalidDataException("Truncated compressed viewport.");
                    n += got;
                }
                if (gzip.ReadByte() != -1) throw new InvalidDataException("Oversized compressed viewport.");
                return Decode(raw, true);
            }
        }
    }
}
