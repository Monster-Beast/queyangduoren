using System;
using System.Globalization;
using System.IO;
using Packet=Queyang.Core.Wire;
using Queyang.Game;

namespace Queyang.Integration
{
    internal sealed class StockOrderTape
    {
        internal int X, Y;
        internal string Command, Reply;
        internal byte[] Before, After;
        internal static StockOrderTape[] Load(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir,"order-0.bin"))) return null;
            var orders=new StockOrderTape[4];
            for(int i=0;i<orders.Length;i++)
            {
                string file=Path.Combine(dir,"order-"+i.ToString(CultureInfo.InvariantCulture));
                var step=new StockOrderTape();
                using(var r=new BinaryReader(File.OpenRead(file+".bin")))
                {
                    step.X=r.ReadInt32(); step.Y=r.ReadInt32();
                    step.Command=Packet.Text(r,160); step.Reply=Packet.Text(r,8192); Packet.End(r);
                }
                byte[] Read(string suffix)
                {
                    var info=new FileInfo(file+suffix+".view");
                    if(info.Length<=0 || info.Length>Packet.MaxFrame) throw new InvalidDataException("Invalid stock tool viewport");
                    var bytes=File.ReadAllBytes(info.FullName);
                    var v=ColonyView.Decode(bytes);
                    if(step.X<v.X || step.X>=v.X+ColonyView.Width || step.Y<v.Y || step.Y>=v.Y+ColonyView.Height ||
                        (v.Cells[(step.Y-v.Y)*ColonyView.Width+step.X-v.X].Flags&1)==0)
                        throw new InvalidDataException("Stock tool target outside host viewport");
                    return bytes;
                }
                step.Before=Read("-before"); step.After=Read("-after");
                if(!step.Reply.StartsWith("OK ",StringComparison.Ordinal) ||
                    (i==0 && step.Command!="sweep") || (i==1 && !step.Command.StartsWith("priority",StringComparison.Ordinal)) ||
                    (i==2 && step.Command!="harvest") || (i==3 && step.Command!="mop")) throw new InvalidDataException("Unexpected recorded tool or reply");
                orders[i]=step;
            }
            return orders;
        }
    }
}
