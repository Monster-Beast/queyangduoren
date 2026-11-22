using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Packet = Queyang.Core.Wire;

namespace Queyang.Game
{
    public sealed class ColonyMeter
    {
        public int Key,Animation;
        public string Target,Clip;
        public string[] Hidden=Array.Empty<string>();
        public byte Offset;
        public int Layer;
        public float X,Y,Z,Rotation,Position;
        internal ColonyMeter Copy()=>(ColonyMeter)MemberwiseClone();
        public bool Visible;
        public int[] TintSymbols=Array.Empty<int>();
        public uint[] Tints=Array.Empty<uint>();

        internal void Write(BinaryWriter w)
        {
            Validate();w.Write(Key); Packet.Text(w,Target,120);Packet.Text(w,Clip,120);w.Write(Offset);w.Write(Layer);
            w.Write(X);w.Write(Y);w.Write(Z);w.Write((byte)Hidden.Length);
            foreach(string h in Hidden) Packet.Text(w,h,120);
            w.Write(Animation);w.Write(Position);w.Write(Rotation);w.Write(Visible);
            w.Write((byte)Tints.Length);
            for(int i=0;i<Tints.Length;i++) {w.Write(TintSymbols[i]);w.Write(Tints[i]);}
        }
        internal static ColonyMeter Read(BinaryReader r)
        {
            var s=new ColonyMeter{Key=r.ReadInt32(),Target=Packet.Text(r,120),Clip=Packet.Text(r,120),
                Offset=r.ReadByte(),Layer=r.ReadInt32(),X=r.ReadSingle(),Y=r.ReadSingle(),Z=r.ReadSingle()};
            int n=r.ReadByte();if(n>32) throw new InvalidDataException("Too many hidden meter symbols");
            s.Hidden=new string[n];for(int i=0;i<n;i++) s.Hidden[i]=Packet.Text(r,120);
            s.Animation=r.ReadInt32();s.Position=r.ReadSingle();s.Rotation=r.ReadSingle();s.Visible=r.ReadBoolean();
            n=r.ReadByte();if(n>16) throw new InvalidDataException("Too many meter colors");
            s.TintSymbols=new int[n];s.Tints=new uint[n];for(int i=0;i<n;i++){s.TintSymbols[i]=r.ReadInt32();s.Tints[i]=r.ReadUInt32();}
            s.Validate();return s;
        }
        static bool NameFits(string name,bool empty=false)
        {
            if(name==null || !empty && name.Length==0 || name.Length>120 || Encoding.UTF8.GetByteCount(name)>120)return false;
            foreach(char c in name)if(char.IsControl(c))return false;
            return true;
        }
        internal bool SupportsRecipe()
        {
            if(!NameFits(Clip) || !NameFits(Target,true) || Hidden==null || Hidden.Length>32)return false;
            foreach(var h in Hidden)if(!NameFits(h))return false;
            return true;
        }
        internal int WireBytes()
        {
            Validate();
            int size=40+Encoding.UTF8.GetByteCount(Clip)+Encoding.UTF8.GetByteCount(Target)+Tints.Length*8;
            foreach(string h in Hidden)size+=2+Encoding.UTF8.GetByteCount(h);
            return size;
        }
        void Validate()
        {
            if(Key<0 || Key>=ColonyView.Width*ColonyView.Height*256 || !SupportsRecipe() ||
                Offset>3 || Layer< -3 || Layer>255 || Tints==null || TintSymbols==null || Tints.Length>16 || TintSymbols.Length!=Tints.Length ||
                !Finite(Position) || Position<0 || Position>1 || !Finite(Rotation) || !Finite(X) || !Finite(Y) || !Finite(Z))
                throw new InvalidDataException("Invalid building meter");
        }
        static bool Finite(float n)=>!float.IsNaN(n) && !float.IsInfinity(n);
    }
    public sealed partial class ColonyView
    {
        public ColonyMeter[] Meters;
        public int MeterFirst,MeterLast=Width*Height*256-1;
        public const int MaxMeterPageBytes=4608, MaxMeterPageEntries=8;
        internal void SelectMeterPage(List<ColonyMeter> all,int sequence)
        {
            var starts=new List<int>{0};int used=9;
            for(int i=0;i<all.Count;i++)
            {
                int size=all[i].WireBytes();
                if (i-starts[starts.Count-1]>=MaxMeterPageEntries || used+size>MaxMeterPageBytes)
                {starts.Add(i);used=9;}
                used+=size;
            }
            int page=(sequence&int.MaxValue)%starts.Count;
            int start=starts[page],end=page+1<starts.Count?starts[page+1]:all.Count;
            MeterFirst=start==0?0:all[start-1].Key+1;
            MeterLast=end==all.Count?Cells.Length*256-1:all[end-1].Key;
            Meters=all.GetRange(start,end-start).ToArray();
        }
        void CheckMeterRange()
        {
            if(MeterFirst<0 || MeterLast<MeterFirst || MeterLast>=Width*Height*256 || Meters.Length>MaxMeterPageEntries)
                throw new InvalidDataException("Invalid building meter page");
            int prev=MeterFirst-1,size=9;
            foreach(var s in Meters)
            {
                if(s==null || s.Key<=prev || s.Key>MeterLast || Cells[s.Key/256].BuildingSaveId<=0)
                    throw new InvalidDataException("Meter is outside authoritative building coverage");
                prev=s.Key;
                size+=s.WireBytes();
            }
            if(size>MaxMeterPageBytes)throw new InvalidDataException("Oversized building meter page");
        }
        void WriteMeters(BinaryWriter w)
        {
            CheckMeterRange();w.Write(MeterFirst);w.Write(MeterLast);w.Write((byte)Meters.Length);foreach(var s in Meters)s.Write(w);
        }
        void ReadMeters(BinaryReader r)
        {
            MeterFirst=r.ReadInt32();MeterLast=r.ReadInt32();int n=r.ReadByte();
            if(n>MaxMeterPageEntries)throw new InvalidDataException("Too many building meters");
            long start=r.BaseStream.Position;
            Meters=new ColonyMeter[n];for(int i=0;i<n;i++)
            {
                Meters[i]=ColonyMeter.Read(r);
                if(r.BaseStream.Position-start+9>MaxMeterPageBytes)throw new InvalidDataException("Oversized building meter page");
            }
            CheckMeterRange();
        }
    }
}
