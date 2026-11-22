using System.IO;

namespace Queyang.Game
{
    public struct ColonyLight
    {
        public int Id,Order;
        public byte Shape;
        public float X,Y,Z,OffsetX,OffsetY,Range,DirectionX,DirectionY,Angle,Red,Green,Blue;
        public bool DrawOverlay;
        public uint Overlay;

        internal void Validate()
        {
            if (Id==0 || Order<0 || Shape>2 || Range<0 || !Finite(Range) || !Finite(X) || !Finite(Y) ||
                !Finite(Z) || !Finite(OffsetX) || !Finite(OffsetY) || !Finite(DirectionX) || !Finite(DirectionY) ||
                !Finite(Angle) || !Finite(Red) || !Finite(Green) || !Finite(Blue)) throw new InvalidDataException("Invalid light source.");
        }
        static bool Finite(float n)=>!float.IsNaN(n) && !float.IsInfinity(n);
    }
    public sealed partial class ColonyView
    {
        public const int MaxLights=32;
        void ValidateLights()
        {
            if(Lights.Length>MaxLights || LightThrough<LightFrom) throw new InvalidDataException("Invalid light range.");
            for(int i=0;i<Lights.Length;i++)
            {
                var s=Lights[i]; s.Validate();
                if(s.Id<LightFrom || s.Id>LightThrough || i>0 && s.Id<=Lights[i-1].Id)
                    throw new InvalidDataException("Light outside coverage.");
            }
        }
        void WriteLighting(BinaryWriter w)
        {
            ValidateLights();w.Write(LightMask);w.Write(LightFrom);w.Write(LightThrough);w.Write((byte)Lights.Length);
            foreach(var s in Lights)
            {
                w.Write(s.Id);w.Write(s.Order);w.Write(s.Shape);w.Write(s.X);w.Write(s.Y);w.Write(s.Z);w.Write(s.OffsetX);w.Write(s.OffsetY);
                w.Write(s.Range);w.Write(s.DirectionX);w.Write(s.DirectionY);w.Write(s.Angle);
                w.Write(s.Red);w.Write(s.Green);w.Write(s.Blue);w.Write(s.DrawOverlay);w.Write(s.Overlay);
            }
        }
        void ReadLighting(BinaryReader r)
        {
            LightMask=r.ReadBytes(Width*Height/8);
            LightFrom=r.ReadInt32();LightThrough=r.ReadInt32();int n=r.ReadByte();
            if (n>MaxLights) throw new InvalidDataException("Too many light sources.");
            Lights=new ColonyLight[n];
            for(int i=0;i<n;i++) Lights[i]=new ColonyLight {Id=r.ReadInt32(),Order=r.ReadInt32(),Shape=r.ReadByte(),
                X=r.ReadSingle(),Y=r.ReadSingle(),Z=r.ReadSingle(),OffsetX=r.ReadSingle(),OffsetY=r.ReadSingle(),Range=r.ReadSingle(),
                DirectionX=r.ReadSingle(),DirectionY=r.ReadSingle(),Angle=r.ReadSingle(),Red=r.ReadSingle(),Green=r.ReadSingle(),
                Blue=r.ReadSingle(),DrawOverlay=r.ReadBoolean(),Overlay=r.ReadUInt32()};
            ValidateLights();
        }
    }
}
