using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Queyang.Game
{
    public struct MaterialSample
    {
        public int Element;
        public float Mass, Temperature;
        public int Germs;
        public byte GermType;
    }

    public struct ColonyPile
    {
        public int SaveId, Animation;
        public bool HasAppearance;
        public int File,Frame;
        public uint Tint;
        public float Z, M00,M01,M02,M10,M11,M12;
        public ushort Cell;
        public byte SubX, SubY;
        public MaterialSample Material;
    }

    public struct ColonyStock
    {
        public int Tag;
        public float Total, Reserved;
    }

    public struct ColonyCell
    {
        public byte Flags;
        public int BuriedAnimation;
        public ushort BuriedRotation;
        public int TargetId;
        public ushort Connections;
        public byte Utilities;
        public byte R, G, B, BuildingStyle, Progress;
        public string Element, Building;
        public float Mass, Temperature;
        public int ElementHash, BuildingHash;
        public int BuildingAnimation, BuildingSaveId;
        public byte BuildingFrame;
        public sbyte AnchorDX, AnchorDY;
        public byte Visibility;
        public bool RenderGround;
        public MaterialSample GasPipe, LiquidPipe, Structure;
        public float WattsUsed, WattsGenerated, WattsSafe, WattsNeeded;
        public int LogicValue;
        public int Germs, Lux, RailItem;
        public int RoomType, RoomAnchor, RoomSize;
        public byte OrderPriority;
        public int OrderTargetId;
        public byte SeparateOrderPriority;
        public bool MopOrdered;
        public byte DigMaterial; // One-based index into the vanilla dig icon material table.
        public uint DigTint, BlueprintTint;
        public bool HasBlueprintTint;
        public byte GermType, RailConnections;
        public float Decor, RailMass;
        public bool RailPresent;
    }

    public struct ColonyDupe
    {
        public int Id, X, Y;
        public byte SubX, SubY;
        public string Name, Activity;
        public int SaveId, Animation;
        public int Personality;
        public byte AnimationPosition;
        public bool FlipX;
        public ColonyDupePose Pose;
        public int Hat,Hair,HatHair;
        public byte HeadVisibility;
        public short HealthFill; // -1 when ONI hides the health bar, otherwise permille.
    }

    public struct ColonyDupePose
    {
        public float X,Y, M00,M01,M02,M10,M11,M12;
        public int File, Frame;
    }

    // A display-only sample. Rows start at the bottom, matching ONI's grid.
    public sealed partial class ColonyView
    {
        public const int Width = 24, Height = 16;
        public const ushort NativeVersion = 5;
        public bool Native;
        public bool Detailed;
        public bool HasResources;
        public bool HasDupeAppearance;
        public bool HasHealthBars;
        public bool HasEntombed;
        public bool HasPileAppearance;
        public byte[] LightMask;
        public ColonyLight[] Lights=Array.Empty<ColonyLight>();
        public int LightFrom=int.MinValue,LightThrough=int.MaxValue;
        public const byte EntombedFlag=128;
        public bool HasColonyHud;
        public int HudWorld,HudLocalPopulation,HudTotalPopulation;
        public float HudCalories,HudStress;
        public const int MaxPiles = 128, MaxStocks = 384;
        public byte[] PileCoverage = new byte[Width * Height / 8];
        public int PileFrom, PileThrough = int.MaxValue;
        public ColonyPile[] Piles = Array.Empty<ColonyPile>();
        public ColonyStock[] Stocks = Array.Empty<ColonyStock>();
        public bool CompleteStock;
        public int StockFrom = int.MinValue, StockThrough = int.MaxValue;

        public bool CoversPiles(int index) => (PileCoverage[index >> 3] & (1 << (index & 7))) != 0;
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
        public int DupeFrom, DupeThrough = int.MaxValue;
        public int DupePoseTick;
        public bool CoversDupeId(int id) => id>=DupeFrom && id<=DupeThrough;

        internal void SetDupePage(List<ColonyDupe> visible,int sample)
        {
            DupeFrom=0; DupeThrough=int.MaxValue;
            if (visible.Count<=64) { Dupes=visible.ToArray(); return; }
            visible.Sort((a,b)=>a.SaveId.CompareTo(b.SaveId));
            int pages=(visible.Count+63)/64, first=(sample%pages)*64, count=Math.Min(64,visible.Count-first);
            if (first>0) DupeFrom=visible[first-1].SaveId+1;
            if (first+count<visible.Count) DupeThrough=visible[first+count-1].SaveId;
            Dupes=visible.GetRange(first,count).ToArray();
        }

        internal ColonyDupe[] DisplayDupes(ColonyDupe[] previous)
        {
            if (previous==null || (DupeFrom==0 && DupeThrough==int.MaxValue)) return Dupes;
            var merged=new List<ColonyDupe>(Dupes);
            foreach (var d in previous)
            {
                long dx=(long)d.X-X, dy=(long)d.Y-Y;
                if (!CoversDupeId(d.SaveId) && dx>=0 && dx<Width && dy>=0 && dy<Height &&
                    (Cells[(int)dy*Width+(int)dx].Flags&1)!=0) merged.Add(d);
            }
            return merged.ToArray();
        }

        public byte[] Encode() => Encode(Native ? 50 : MaxNameBytes, false);
        void ValidateHud()
        {
            if (HudWorld<0 || HudWorld>=255 || HudLocalPopulation<0 || HudTotalPopulation<HudLocalPopulation ||
                !(HudCalories>=0) || float.IsInfinity(HudCalories) || float.IsNaN(HudStress) || float.IsInfinity(HudStress))
                throw new InvalidDataException("Invalid colony HUD");
        }

        private byte[] Encode(int nameBytes, bool pageEnvironment)
        {
            if (Cells == null || Cells.Length != Width * Height || Dupes == null || Dupes.Length > 64)
                throw new InvalidDataException("Invalid viewport size.");
            ValidateClock();
            var ids = new Dictionary<string, ushort>(StringComparer.Ordinal);
            var names = new List<byte[]>();
            AddName(string.Empty, ids, names);
            foreach (var c in Cells)
            {
                Validate(c,HasEntombed);
                AddName(c.Element, ids, names, nameBytes);
                AddName(c.Building, ids, names, nameBytes);
            }
            foreach (var d in Dupes) { AddName(d.Name, ids, names, nameBytes); AddName(d.Activity, ids, names, nameBytes); }
            ValidateDupes(Dupes);
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                if (Detailed && !Native) throw new InvalidDataException("Detailed view requires native state.");
                if (HasResources && !Detailed) throw new InvalidDataException("Resource state requires detailed view.");
                // Flag 128 alone is the compressed v8 envelope. Include the explicit
                // identity range when publishing authoritative buried-item markers.
                bool dupeRange = DupeFrom!=0 || DupeThrough!=int.MaxValue || HasEntombed;
                bool extended = HasEnvironment || EntityPayload != null || dupeRange || DupePoseTick!=0 || OrderMarks!=null || HasDupeAppearance;
                if (extended && !HasResources) throw new InvalidDataException("Extended state requires native resource state.");
                if (HasColonyHud && !extended) throw new InvalidDataException("HUD requires a native viewport");
                if(HasPileAppearance && (!HasColonyHud || !HasEntombed)) throw new InvalidDataException("Pile appearance requires a full host viewport");
                if (LightMask!=null && (!extended || LightMask.Length!=Width*Height/8)) throw new InvalidDataException("Invalid light coverage.");
                w.Write(LightMask!=null ? (ushort)11 : HasPileAppearance ? (ushort)10 : HasColonyHud ? (ushort)9 : extended ? (ushort)8 : HasResources ? (ushort)7 : Detailed ? (ushort)6 : Native ? NativeVersion : (ushort)4);
                bool orders = extended && Array.Exists(Cells,c=>c.DigMaterial!=0 || c.HasBlueprintTint || c.MopOrdered || c.OrderPriority!=0 || c.OrderTargetId!=0 || c.SeparateOrderPriority!=0);
                if (extended) w.Write((byte)((HasEnvironment ? 1 : 0) | (EntityPayload != null ? 2 : 0) | (orders ? 4 : 0) | (dupeRange ? 8 : 0) | (DupePoseTick!=0 ? 16 : 0) | (OrderMarks!=null ? 32 : 0) | (HasDupeAppearance ? 64 : 0) | (HasEntombed ? 128 : 0)));
                if(HasHealthBars && LightMask==null) throw new InvalidDataException("Health bars require the native v11 viewport");
                if (LightMask!=null) w.Write((byte)((HasColonyHud?1:0)|(HasPileAppearance?2:0)|(HasHealthBars?4:0)));
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
                    if (Native)
                    {
                        w.Write(c.ElementHash); w.Write(c.BuildingHash);
                        w.Write(c.AnchorDX); w.Write(c.AnchorDY); w.Write(c.Visibility); w.Write(c.RenderGround);
                        w.Write(c.BuildingAnimation); w.Write(c.BuildingFrame);
                        if (Detailed)
                        {
                            if ((c.Utilities & 1) != 0)
                            {
                                w.Write(c.WattsUsed); w.Write(c.WattsGenerated); w.Write(c.WattsSafe); w.Write(c.WattsNeeded);
                            }
                            if ((c.Utilities & 2) != 0) WriteMaterial(w, c.GasPipe);
                            if ((c.Utilities & 4) != 0) WriteMaterial(w, c.LiquidPipe);
                            if ((c.Utilities & 8) != 0) w.Write(c.LogicValue);
                            if (c.BuildingHash != 0 && c.AnchorDX == 0 && c.AnchorDY == 0)
                            {
                                WriteMaterial(w, c.Structure);
                                if (HasResources) w.Write(c.BuildingSaveId);
                            }
                        }
                    }
                }
                w.Write((byte)Dupes.Length);
                foreach (var d in Dupes)
                {
                    w.Write(d.Id); w.Write(d.X); w.Write(d.Y);
                    w.Write(d.SubX); w.Write(d.SubY);
                    w.Write(ids[d.Name ?? string.Empty]);
                    w.Write(ids[d.Activity ?? string.Empty]);
                    if (Native)
                    {
                        w.Write(d.SaveId); w.Write(d.Animation); w.Write(d.AnimationPosition); w.Write(d.FlipX);
                        if (Detailed) w.Write(d.Personality);
                    }
                }
                // Keep ordinary localized names intact. The synthetic worst case has 896
                // unique names plus all four utilities and a building in every cell.
                if (HasResources) WriteResources(w);
                if (extended) WriteExtensions(w, pageEnvironment);
                if (orders) WriteOrders(w);
                if (dupeRange) { w.Write(DupeFrom); w.Write(DupeThrough); }
                if (DupePoseTick!=0) WriteDupePoses(w);
                if (OrderMarks!=null) WriteOrderMarks(w);
                if (HasDupeAppearance)
                    foreach (var d in Dupes)
                    {
                        if (d.HeadVisibility>7) throw new InvalidDataException("Invalid head symbols");
                        w.Write(d.Hat); w.Write(d.Hair); w.Write(d.HatHair); w.Write(d.HeadVisibility);
                    }
                if (HasColonyHud)
                {
                    ValidateHud();
                    w.Write((byte)HudWorld); w.Write(HudLocalPopulation); w.Write(HudTotalPopulation); w.Write(HudCalories); w.Write(HudStress);
                }
                if(HasPileAppearance)
                {
                    foreach(var cell in Cells) if((cell.Flags&EntombedFlag)!=0) { w.Write(cell.BuriedAnimation);w.Write(cell.BuriedRotation); }
                    foreach(var pile in Piles)
                    {
                        w.Write(pile.HasAppearance);
                        if(!pile.HasAppearance) continue;
                        ValidatePileAppearance(pile);
                        w.Write(pile.File);w.Write(pile.Frame);w.Write(pile.Tint);w.Write(pile.Z);
                        w.Write(pile.M00);w.Write(pile.M01);w.Write(pile.M02);w.Write(pile.M10);w.Write(pile.M11);w.Write(pile.M12);
                    }
                }
                if (LightMask!=null) WriteLighting(w);
                if (HasHealthBars) foreach(var dupe in Dupes)
                {
                    if(dupe.HealthFill< -1 || dupe.HealthFill>1000) throw new InvalidDataException("Invalid health bar");
                    w.Write(dupe.HealthFill);
                }
                if (extended && stream.Length > MaxViewport)
                {
                    // Keep all readings and stock labels when a dense full tile compresses well.
                    var compact = CompressViewport(stream, nameBytes==0);
                    if (compact != null) return compact;
                    // Incompressible worst case: retain all entity bytes, rotate environmental coverage.
                    if (!pageEnvironment) return Encode(4, true);
                    if (nameBytes > 2) return Encode(2, true);
                    if (nameBytes != 0) return Encode(0, true);
                }
                int shortNames = HasResources ? 14 : 24;
                if (stream.Length > MaxViewport && Detailed && nameBytes > shortNames) return Encode(shortNames, false);
                if (stream.Length > MaxViewport) throw new InvalidDataException("Viewport exceeds the frame limit.");
                return stream.ToArray();
            }
        }

        private static void AddName(string name, Dictionary<string, ushort> ids, List<byte[]> names, int nameBytes = MaxNameBytes)
        {
            name = name ?? string.Empty;
            if (ids.ContainsKey(name)) return;
            if (nameBytes == 0) { ids.Add(name,0); return; }
            if (nameBytes == 2 && name.Length != 0 && name[0] >= 0x800) { ids.Add(name, 0); return; }
            var encoder = Encoding.UTF8.GetEncoder();
            var buffer = new byte[nameBytes];
            // Convert truncates at a complete UTF-8 character, even for non-BMP names.
            int count = Math.Min(name.Length, MaxNameBytes);
            encoder.Convert(name.ToCharArray(0, count), 0, count, buffer, 0, buffer.Length,
                count == name.Length, out _, out int used, out _);
            Array.Resize(ref buffer, used);
            ids.Add(name, (ushort)names.Count);
            names.Add(buffer);
        }

        public static ColonyView Decode(byte[] payload) => Decode(payload, false);

        private static ColonyView Decode(byte[] payload, bool inflated)
        {
            if (payload == null || payload.Length > (inflated ? MaxInflated : MaxViewport))
                throw new InvalidDataException("Invalid viewport payload size.");
            using (var stream = new MemoryStream(payload, false))
            using (var r = new BinaryReader(stream))
            {
                ushort version = r.ReadUInt16();
                if (version < 4 || version > 11 || (inflated && version < 8)) throw new InvalidDataException("Unknown viewport version.");
                byte extensions = version >= 8 ? r.ReadByte() : (byte)0;
                if (version==8 && extensions == 128 && !inflated) return InflateViewport(r);
                if (version >= 8 && (extensions & 123) == 0) throw new InvalidDataException("Invalid viewport extensions.");
                int flags=version==11 ? r.ReadByte() : version==10 ? 3 : version==9 ? 1 : 0;
                if ((flags&3)==2 || flags>7) throw new InvalidDataException("Invalid viewport features");
                var v = new ColonyView { Native = version >= 5, Detailed = version >= 6, HasResources = version >= 7,
                    X = r.ReadInt32(), Y = r.ReadInt32(), HasEntombed=(extensions & 128)!=0 };
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
                    if (v.Native)
                    {
                        cell.ElementHash = r.ReadInt32(); cell.BuildingHash = r.ReadInt32();
                        cell.AnchorDX = r.ReadSByte(); cell.AnchorDY = r.ReadSByte(); cell.Visibility = r.ReadByte();
                        byte ground = r.ReadByte();
                        if (ground > 1) throw new InvalidDataException("Invalid ground rendering flag.");
                        cell.RenderGround = ground == 1;
                        cell.BuildingAnimation = r.ReadInt32(); cell.BuildingFrame = r.ReadByte();
                        if (v.Detailed)
                        {
                            if ((cell.Utilities & 1) != 0)
                            {
                                cell.WattsUsed = ReadAmount(r); cell.WattsGenerated = ReadAmount(r);
                                cell.WattsSafe = ReadAmount(r); cell.WattsNeeded = ReadAmount(r);
                            }
                            if ((cell.Utilities & 2) != 0) cell.GasPipe = ReadMaterial(r);
                            if ((cell.Utilities & 4) != 0) cell.LiquidPipe = ReadMaterial(r);
                            if ((cell.Utilities & 8) != 0) cell.LogicValue = r.ReadInt32();
                            if (cell.BuildingHash != 0 && cell.AnchorDX == 0 && cell.AnchorDY == 0)
                            {
                                cell.Structure = ReadMaterial(r);
                                if (v.HasResources) cell.BuildingSaveId = r.ReadInt32();
                            }
                        }
                    }
                    Validate(cell,v.HasEntombed);
                    v.Cells[i] = cell;
                }
                int n = r.ReadByte();
                if (n > 64) throw new InvalidDataException("Invalid duplicant count.");
                v.Dupes = new ColonyDupe[n];
                for (int i = 0; i < n; i++)
                {
                    v.Dupes[i] = new ColonyDupe { Id = r.ReadInt32(), X = r.ReadInt32(), Y = r.ReadInt32(),
                        SubX = r.ReadByte(), SubY = r.ReadByte(), Name = ReadName(r, names), Activity = ReadName(r, names) };
                    if (v.Native)
                    {
                        v.Dupes[i].SaveId = r.ReadInt32(); v.Dupes[i].Animation = r.ReadInt32();
                        v.Dupes[i].AnimationPosition = r.ReadByte();
                        byte flip = r.ReadByte();
                        if (flip > 1) throw new InvalidDataException("Invalid duplicant orientation.");
                        v.Dupes[i].FlipX = flip == 1;
                        if (v.Detailed) v.Dupes[i].Personality = r.ReadInt32();
                    }
                }
                ValidateDupes(v.Dupes);
                if (v.HasResources) v.ReadResources(r);
                if (version >= 8) v.ReadExtensions(r, extensions);
                if ((extensions & 4) != 0) v.ReadOrders(r);
                if ((extensions & 8) != 0)
                {
                    v.DupeFrom=r.ReadInt32(); v.DupeThrough=r.ReadInt32();
                    v.ValidateNativeIdentities();
                }
                if ((extensions & 16)!=0) v.ReadDupePoses(r);
                if ((extensions & 32)!=0) v.ReadOrderMarks(r);
                v.HasDupeAppearance=(extensions & 64)!=0;
                if (v.HasDupeAppearance)
                    for (int i=0;i<v.Dupes.Length;i++)
                    {
                        v.Dupes[i].Hat=r.ReadInt32(); v.Dupes[i].Hair=r.ReadInt32(); v.Dupes[i].HatHair=r.ReadInt32();
                        v.Dupes[i].HeadVisibility=r.ReadByte();
                        if (v.Dupes[i].HeadVisibility>7) throw new InvalidDataException("Invalid head symbols");
                    }
                if ((flags&1)!=0)
                {
                    v.HasColonyHud=true; v.HudWorld=r.ReadByte(); v.HudLocalPopulation=r.ReadInt32(); v.HudTotalPopulation=r.ReadInt32();
                    v.HudCalories=r.ReadSingle(); v.HudStress=r.ReadSingle(); v.ValidateHud();
                }
                if((flags&2)!=0)
                {
                    if(!v.HasEntombed) throw new InvalidDataException("Missing buried-item data");
                    v.HasPileAppearance=true;
                    for(int i=0;i<v.Cells.Length;i++) if((v.Cells[i].Flags&EntombedFlag)!=0)
                    { v.Cells[i].BuriedAnimation=r.ReadInt32(); v.Cells[i].BuriedRotation=r.ReadUInt16(); }
                    for(int i=0;i<v.Piles.Length;i++)
                    {
                        ref var p=ref v.Piles[i];
                        p.HasAppearance=r.ReadBoolean();
                        if(!p.HasAppearance) continue;
                        p.File=r.ReadInt32();p.Frame=r.ReadInt32();p.Tint=r.ReadUInt32();p.Z=r.ReadSingle();
                        p.M00=r.ReadSingle();p.M01=r.ReadSingle();p.M02=r.ReadSingle();
                        p.M10=r.ReadSingle();p.M11=r.ReadSingle();p.M12=r.ReadSingle();
                        ValidatePileAppearance(p);
                    }
                }
                if (version==11) v.ReadLighting(r);
                if((flags&4)!=0)
                {
                    v.HasHealthBars=true;
                    for(int i=0;i<v.Dupes.Length;i++)
                    {
                        short fill=r.ReadInt16();
                        if(fill< -1 || fill>1000) throw new InvalidDataException("Invalid health bar");
                        v.Dupes[i].HealthFill=fill;
                    }
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing viewport bytes.");
                return v;
            }
        }

        private static float ReadAmount(BinaryReader r)
        {
            float value = r.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0)
                throw new InvalidDataException("Invalid physical reading.");
            return value;
        }

        static void ValidatePileAppearance(ColonyPile p)
        {
            bool Bounded(float n,float limit) => n>=-limit && n<=limit;
            if(p.Frame< -1 || p.Frame>65535 || !Bounded(p.Z,10000) || !Bounded(p.M00,128) ||
                !Bounded(p.M01,128) || !Bounded(p.M10,128) || !Bounded(p.M11,128) ||
                !Bounded(p.M02,100000) || !Bounded(p.M12,100000)) throw new InvalidDataException("Invalid ore render state");
        }

        private void WriteResources(BinaryWriter w)
        {
            if (PileCoverage.Length != Width * Height / 8 || Piles.Length > MaxPiles || Stocks.Length > MaxStocks)
                throw new InvalidDataException("Invalid resource sample size.");
            w.Write(PileCoverage); w.Write(PileFrom); w.Write(PileThrough); w.Write((byte)Piles.Length);
            foreach (var pile in Piles)
            {
                w.Write(pile.SaveId); w.Write(pile.Cell); w.Write(pile.SubX); w.Write(pile.SubY);
                w.Write(pile.Animation); WriteMaterial(w, pile.Material);
            }
            w.Write(CompleteStock); w.Write(StockFrom); w.Write(StockThrough); w.Write((ushort)Stocks.Length);
            foreach (var stock in Stocks) { w.Write(stock.Tag); w.Write(stock.Total); w.Write(stock.Reserved); }
        }

        private void ReadResources(BinaryReader r)
        {
            PileCoverage = r.ReadBytes(Width * Height / 8);
            if (PileCoverage.Length != Width * Height / 8) throw new EndOfStreamException();
            PileFrom = r.ReadInt32(); PileThrough = r.ReadInt32();
            if (PileFrom < 0 || PileFrom > PileThrough) throw new InvalidDataException("Invalid pile range.");
            int count = r.ReadByte();
            if (count > MaxPiles) throw new InvalidDataException("Invalid pile count.");
            var ids = new HashSet<int>();
            Piles = new ColonyPile[count];
            for (int i = 0; i < count; i++)
            {
                var pile = new ColonyPile { SaveId = r.ReadInt32(), Cell = r.ReadUInt16(), SubX = r.ReadByte(),
                    SubY = r.ReadByte(), Animation = r.ReadInt32(), Material = ReadMaterial(r) };
                if (pile.SaveId < PileFrom || pile.SaveId > PileThrough || pile.Cell >= Cells.Length || (Cells[pile.Cell].Flags & 1) == 0 ||
                    !ids.Add(pile.SaveId)) throw new InvalidDataException("Invalid pile identity or position.");
                Piles[i] = pile;
            }
            byte complete = r.ReadByte();
            if (complete > 1) throw new InvalidDataException("Invalid inventory flag.");
            CompleteStock = complete != 0; StockFrom = r.ReadInt32(); StockThrough = r.ReadInt32();
            if (StockFrom > StockThrough || (CompleteStock && (StockFrom != int.MinValue || StockThrough != int.MaxValue)))
                throw new InvalidDataException("Invalid inventory range.");
            count = r.ReadUInt16();
            if (count > MaxStocks) throw new InvalidDataException("Invalid inventory size.");
            ids.Clear(); Stocks = new ColonyStock[count];
            for (int i = 0; i < count; i++)
            {
                Stocks[i] = new ColonyStock { Tag = r.ReadInt32(), Total = ReadAmount(r), Reserved = ReadAmount(r) };
                if (Stocks[i].Tag < StockFrom || Stocks[i].Tag > StockThrough || !ids.Add(Stocks[i].Tag))
                    throw new InvalidDataException("Invalid inventory tag.");
            }
        }

        private static MaterialSample ReadMaterial(BinaryReader r) =>
            new MaterialSample { Element = r.ReadInt32(), Mass = ReadAmount(r), Temperature = ReadAmount(r) };

        private static void WriteMaterial(BinaryWriter w, MaterialSample value)
        {
            w.Write(value.Element); w.Write(value.Mass); w.Write(value.Temperature);
        }

        private static string ReadName(BinaryReader r, string[] names)
        {
            int i = r.ReadUInt16();
            if (i >= names.Length) throw new InvalidDataException("Invalid name reference.");
            return names[i];
        }

        private static void Validate(ColonyCell c,bool hasEntombed)
        {
            if (((c.Flags & EntombedFlag)!=0 && (!hasEntombed || (c.Flags&3)!=3)) || (c.Utilities & ~31) != 0 || c.TargetId < 0 || c.Progress > 100 ||
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
