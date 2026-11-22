using System;
using System.Collections.Generic;
using System.IO;

namespace Queyang.Game
{
    public struct ColonyWorkBar
    {
        public ushort Cell;
        public int Owner,Token;
        public float Fill,X,Y,Z;
        public uint Color;
        public bool AutoHide;
        public long Key=>((long)Cell<<32)|(uint)Token;
    }

    public sealed partial class ColonyView
    {
        public ColonyWorkBar[] WorkBars;
        public long WorkBarFrom,WorkBarThrough=long.MaxValue;
        const int WorkPageSize=32;
        internal bool CoversWorkBar(int x,int y,int token)
        {
            x-=X;y-=Y;
            if(WorkBars==null || (uint)x>=Width || (uint)y>=Height) return false;
            int cell=y*Width+x;
            long key=((long)cell<<32)|(uint)token;
            return (Cells[cell].Flags&1)!=0 && key>=WorkBarFrom && key<=WorkBarThrough;
        }
        internal void SetWorkBars(List<ColonyWorkBar> all,int sequence)
        {
            all.Sort((a,b)=>a.Key.CompareTo(b.Key));
            int pages=Math.Max(1,(all.Count+WorkPageSize-1)/WorkPageSize);
            int start=sequence%pages*WorkPageSize,count=Math.Min(WorkPageSize,all.Count-start);
            WorkBarFrom=start==0?0:all[start-1].Key+1;
            WorkBarThrough=start+count==all.Count?long.MaxValue:all[start+count-1].Key;
            WorkBars=all.GetRange(start,count).ToArray();
        }
        void ValidateWorkBars()
        {
            if(WorkBarFrom<0 || WorkBarThrough<WorkBarFrom || WorkBars.Length>WorkPageSize) throw new InvalidDataException("Invalid work bar page");
            long last=-1;
            var tokens=new HashSet<int>();
            foreach(var b in WorkBars)
            {
                if(b.Owner<=0 || b.Token==0 || !tokens.Add(b.Token) || b.Key<=last || b.Key<WorkBarFrom || b.Key>WorkBarThrough || b.Cell>=Cells.Length ||
                    (Cells[b.Cell].Flags&1)==0 || !(b.Fill>=0 && b.Fill<=1) || !(Math.Abs(b.X)<=100000 && Math.Abs(b.Y)<=100000 && Math.Abs(b.Z)<=10000))
                    throw new InvalidDataException("Invalid building work bar");
                last=b.Key;
            }
        }
        void WriteWorkBars(BinaryWriter w)
        {
            ValidateWorkBars();w.Write(WorkBarFrom);w.Write(WorkBarThrough);w.Write((byte)WorkBars.Length);
            foreach(var b in WorkBars)
            {
                w.Write(b.Cell);w.Write(b.Owner);w.Write(b.Token);w.Write(b.Fill);
                w.Write(b.X);w.Write(b.Y);w.Write(b.Z);w.Write(b.Color);w.Write(b.AutoHide);
            }
        }
        void ReadWorkBars(BinaryReader r)
        {
            WorkBarFrom=r.ReadInt64();WorkBarThrough=r.ReadInt64();int count=r.ReadByte();
            if(count>WorkPageSize) throw new InvalidDataException("Too many work bars");
            WorkBars=new ColonyWorkBar[count];
            for(int i=0;i<count;i++)
            {
                WorkBars[i]=new ColonyWorkBar { Cell=r.ReadUInt16(),Owner=r.ReadInt32(),Token=r.ReadInt32(),
                    Fill=r.ReadSingle(),X=r.ReadSingle(),Y=r.ReadSingle(),Z=r.ReadSingle(),Color=r.ReadUInt32() };
                byte flag=r.ReadByte();
                if(flag>1) throw new InvalidDataException("Invalid work bar visibility flag");
                WorkBars[i].AutoHide=flag==1;
            }
            ValidateWorkBars();
        }
    }
}
