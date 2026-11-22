using System;
using System.Collections.Generic;
using System.IO;

namespace Queyang.Game
{
    public sealed class ColonyBuildingWarning
    {
        public ushort Cell;
        public float X,Y,Z;
        public int[] Icons;
    }

    public sealed partial class ColonyView
    {
        const int MaxWarningRows=24,MaxIconsPerBuilding=12;
        public ColonyBuildingWarning[] BuildingWarnings;
        public ushort WarningFirst,WarningLast;

        internal void SetBuildingWarnings(List<ColonyBuildingWarning> rows,int sequence)
        {
            int pages=Math.Max(1,(rows.Count+MaxWarningRows-1)/MaxWarningRows);
            int first=sequence%pages*MaxWarningRows,end=Math.Min(first+MaxWarningRows,rows.Count);
            WarningFirst=(ushort)(first==0?0:rows[first-1].Cell+1);
            WarningLast=(ushort)(end==rows.Count?Cells.Length-1:rows[end-1].Cell);
            BuildingWarnings=rows.GetRange(first,end-first).ToArray();
        }
        void ValidateWarningPage(int count)
        {
            if(WarningFirst>WarningLast || WarningLast>=Cells.Length || count>MaxWarningRows)
                throw new InvalidDataException("Invalid building warning page.");
        }
        void ValidateWarning(ColonyBuildingWarning row,int last)
        {
            if(row.Cell<=last || row.Cell<WarningFirst || row.Cell>WarningLast ||
                (Cells[row.Cell].Flags&1)==0 || Cells[row.Cell].BuildingHash==0 || Cells[row.Cell].BuildingSaveId<=0 ||
                Cells[row.Cell].AnchorDX!=0 || Cells[row.Cell].AnchorDY!=0 ||
                row.Icons.Length==0 || row.Icons.Length>MaxIconsPerBuilding ||
                new HashSet<int>(row.Icons).Count!=row.Icons.Length ||
                !(Math.Abs(row.X)<=20 && Math.Abs(row.Y)<=20 && Math.Abs(row.Z)<=20))
                throw new InvalidDataException("Invalid building warning icons.");
        }
        void WriteBuildingWarnings(BinaryWriter w)
        {
            ValidateWarningPage(BuildingWarnings.Length);
            w.Write(WarningFirst);w.Write(WarningLast);w.Write((byte)BuildingWarnings.Length);
            int last=-1;
            foreach(var row in BuildingWarnings)
            {
                ValidateWarning(row,last);last=row.Cell;
                w.Write(row.Cell);w.Write(row.X);w.Write(row.Y);w.Write(row.Z);w.Write((byte)row.Icons.Length);
                foreach(int icon in row.Icons) w.Write(icon);
            }
        }
        void ReadBuildingWarnings(BinaryReader r)
        {
            WarningFirst=r.ReadUInt16();WarningLast=r.ReadUInt16();int count=r.ReadByte();ValidateWarningPage(count);
            BuildingWarnings=new ColonyBuildingWarning[count];
            int last=-1;
            for(int i=0;i<count;i++)
            {
                var row=new ColonyBuildingWarning{Cell=r.ReadUInt16(),X=r.ReadSingle(),Y=r.ReadSingle(),Z=r.ReadSingle()};
                int size=r.ReadByte();
                if(size>MaxIconsPerBuilding) throw new InvalidDataException("Too many building warning icons.");
                row.Icons=new int[size];
                for(int j=0;j<size;j++) row.Icons[j]=r.ReadInt32();
                ValidateWarning(row,last);last=row.Cell;BuildingWarnings[i]=row;
            }
        }
    }
}
