using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using UnityEngine;
using Packet = Queyang.Core.Wire;

namespace Queyang.Integration
{
    internal static class NativePrintedDupe
    {
        internal static GameObject Print(int x,int y)
        {
            var source = Components.LiveMinionIdentities.Items.First(d=>d!=null && ClusterUtil.GetMyWorldId(d)==ClusterManager.Instance.activeWorldId);
            var stats = new MinionStartingStats(Db.Get().Personalities.TryGet(source.personalityResourceId),null,null);
            return stats.Deliver(Grid.CellToPosCBC(Grid.XYToCell(x,y),Grid.SceneLayer.Move));
        }
        internal static bool Record(GameObject obj,string dir,Action<string> log)
        {
            if (obj==null) throw new InvalidDataException("Newly delivered duplicant disappeared");
            int cell = Grid.PosToCell(obj);
            if (!Grid.IsValidCell(cell)) return false;
            Grid.CellToXY(cell,out int x,out int y);
            int id = obj.GetComponent<KPrefabID>().InstanceID;
            var bytes = HostWorld.Capture(x,y);
            if (!ColonyView.Decode(bytes).Dupes.Any(d=>d.SaveId==id))
                return false;
            var vitals = HostWorld.Execute("nm/vitals/"+id,x,y);
            var profile = HostWorld.Execute("nm/profile/"+id,x,y);
            if (!vitals.StartsWith("NM1/v/"+id+"/",StringComparison.Ordinal) || vitals.EndsWith("/-") ||
                !profile.StartsWith("NM1/pf/"+id+"/",StringComparison.Ordinal) || profile.EndsWith("/-"))
                throw new InvalidDataException("Newly delivered duplicant missing authoritative details");
            File.WriteAllBytes(Path.Combine(dir,"printed.view"),bytes);
            using(var w=new BinaryWriter(File.Create(Path.Combine(dir,"printed.bin"))))
            { w.Write(id); Packet.Text(w,vitals,8192); Packet.Text(w,profile,8192); }
            log("NATIVE_PRINTED_HOST afterBaseline=true id="+id);
            return true;
        }
    }
}
