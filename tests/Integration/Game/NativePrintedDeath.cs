using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class NativePrintedDeath
    {
        readonly int id;
        readonly GameObject subject;
        readonly string folder;
        readonly Action<string> log;
        readonly float started;
        int cell;
        internal NativePrintedDeath(GameObject printed,string folder,Action<string> log)
        {
            if (!CoopSession.IsHosting || printed==null || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QY_SMOKE_ROOT")) ||
                !string.Equals(Util.GetKleiRootPath(),Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Printed death requires an isolated host");
            id=printed.GetComponent<KPrefabID>().InstanceID;
            using(var r=new BinaryReader(File.OpenRead(Path.Combine(folder,"printed.bin"))))
                if(r.ReadInt32()!=id) throw new InvalidDataException("Death fixture does not own this printed duplicant");
            this.folder=folder; this.log=log; subject=printed; cell=Grid.PosToCell(printed);
            Save("printed-last-alive.view",true);
            started=Time.realtimeSinceStartup;
            printed.GetSMI<DeathMonitor.Instance>().Kill(Db.Get().Deaths.Slain);
        }
        void Save(string name,bool alive)
        {
            Grid.CellToXY(cell,out int x,out int y);
            for(int sample=0;;sample++)
            {
                var bytes=HostWorld.CapturePage(x-ColonyView.Width/2,y-ColonyView.Height/2,sample);
                var view=ColonyView.Decode(bytes);
                if (!view.CoversDupeId(id)) continue;
                if (view.Dupes.Any(d=>d.SaveId==id)!=alive)
                    throw new InvalidDataException("Duplicant roster differs from the original ONI world during "+name);
                File.WriteAllBytes(Path.Combine(folder,name),bytes);
                return;
            }
        }
        internal bool Tick()
        {
            if (Time.realtimeSinceStartup-started>32) throw new TimeoutException("ONI did not retire printed duplicant "+id);
            if (subject!=null && Grid.IsValidCell(Grid.PosToCell(subject))) cell=Grid.PosToCell(subject);
            if (Components.LiveMinionIdentities.Items.Any(d=>d!=null && d.GetComponent<KPrefabID>().InstanceID==id)) return false;
            Grid.CellToXY(cell,out int x,out int y);
            if (HostWorld.Execute("nm/vitals/"+id,x,y)!="NM1/v/"+id+"/-" ||
                HostWorld.Execute("nm/profile/"+id,x,y)!="NM1/pf/"+id+"/-")
                throw new InvalidOperationException("Deceased printed duplicant still has live remote details");
            Save("printed-dead.view",false);
            log($"NATIVE_PRINTED_DEATH_HOST reason=Slain id={id} cell={cell} missingVitals=true missingProfile=true");
            return true;
        }
    }
}
