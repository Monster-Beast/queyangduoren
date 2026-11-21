using System;
using System.Linq;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // All commands enter HostWorld.Execute, just as they do after a peer's frame is decoded.
    // The disposable host supplies one fresh seed, a crop, and a small puddle as tool targets.
    internal sealed class NativeOrderChecks
    {
        readonly Action<string> log;
        readonly int sweepCell, harvestCell, mopCell;
        readonly Clearable clearable;
        readonly HarvestDesignatable crop;
        readonly float started;
        int mopId, step;

        internal NativeOrderChecks(Action<string> log)
        {
            Require(CoopSession.IsHosting && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QY_SMOKE_ROOT")) &&
                string.Equals(Util.GetKleiRootPath(),Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"),StringComparison.OrdinalIgnoreCase),
                "isolated host required");
            this.log=log; started=Time.realtimeSinceStartup;
            int world=ClusterManager.Instance.activeWorldId;
            int origin=Grid.PosToCell(Components.LiveMinionIdentities.Items.First(d=>d!=null && ClusterUtil.GetMyWorldId(d)==world));
            var cells=Enumerable.Range(0,Grid.CellCount).Where(c=>Grid.WorldIdx[c]==world && Grid.Visible[c]!=0 &&
                Grid.IsValidBuildingCell(c) && Grid.IsValidCell(Grid.CellBelow(c)) && Grid.Solid[Grid.CellBelow(c)] &&
                Grid.IsValidCell(Grid.CellAbove(c)) && !Grid.Solid[Grid.CellAbove(c)] &&
                Grid.Objects[Grid.CellAbove(c),(int)ObjectLayer.Building]==null &&
                !Grid.Solid[c] && Grid.Element[c].IsGas && Grid.Objects[c,(int)ObjectLayer.Building]==null &&
                Grid.Objects[c,(int)ObjectLayer.Plants]==null && Grid.Objects[c,(int)ObjectLayer.Pickupables]==null &&
                Grid.Objects[c,(int)ObjectLayer.MopPlacer]==null && HostWorld.FindBuilding(c)==null)
                .OrderBy(c=>Grid.GetCellDistance(c,origin)).Take(3).ToArray();
            Require(cells.Length==3,"need three visible empty floor positions");
            sweepCell=cells[0]; harvestCell=cells[1]; mopCell=cells[2];
            GameObject Spawn(string prefab,int cell)
            {
                var obj=Util.KInstantiate(Assets.GetPrefab(prefab),Grid.CellToPosCBC(cell,Grid.SceneLayer.Ore));
                obj.SetActive(true); return obj;
            }
            clearable=Spawn(BasicSingleHarvestPlantConfig.SEED_ID,sweepCell).GetComponent<Clearable>();
            crop=Spawn(BasicSingleHarvestPlantConfig.ID,harvestCell).GetComponent<HarvestDesignatable>();
            Require(clearable!=null && crop!=null,"missing native fixture components");
            crop.SetHarvestWhenReady(false);
            SimMessages.ReplaceElement(mopCell,SimHashes.Water,CellEventLogger.Instance.SandBoxTool,8f,294f);
        }
        static void Require(bool value,string error)
        { if(!value) throw new InvalidOperationException("Native tools: "+error); }
        static string Command(int cell,string cmd)
        {
            Grid.CellToXY(cell,out int x,out int y);
            return HostWorld.Execute(cmd,x,y);
        }
        static void Expect(int cell,string cmd,bool accepted)
        {
            string answer=Command(cell,cmd);
            Require(answer.StartsWith(accepted?"OK ":"ERROR ",StringComparison.Ordinal),cmd+" => "+answer);
        }
        static ColonyCell Sample(int cell)
        {
            Grid.CellToXY(cell,out int x,out int y);
            var view=ColonyView.Decode(HostWorld.Capture(x,y));
            return view.Cells[(y-view.Y)*ColonyView.Width+x-view.X];
        }
        static int ChangePriority(int cell,Prioritizable target)
        {
            var view=Sample(cell);
            int old=target.GetMasterPriority().priority_value;
            int next=old==7?6:7;
            Require(view.TargetId>0 && view.OrderPriority==old,"snapshot priority mismatch at "+cell);
            Expect(cell,$"priority{next}/{old}:{view.TargetId}",true);
            // A second editor still holds the same snapshot. Its write must not replace the winner.
            Expect(cell,$"priority2/{old}:{view.TargetId}",false);
            Require(target.GetMasterPriority().priority_value==next && Sample(cell).OrderPriority==next,
                "losing editor overwrote ONI master priority");
            return view.TargetId;
        }
        internal bool Tick()
        {
            if(Time.realtimeSinceStartup-started>8)
                throw new TimeoutException($"native tools step={step} mopCell={mopCell} element={Grid.Element[mopCell].id} mass={Grid.Mass[mopCell]}");
            if(step==0)
            {
                Expect(sweepCell,"sweep",true);
                Require(NativeEntitySampler.IsMarked(clearable),"seed not marked for sweeping");
                int sweep=ChangePriority(sweepCell,clearable.GetComponent<Prioritizable>());
                Expect(sweepCell,"cancel:"+sweep,true);
                Require(!NativeEntitySampler.IsMarked(clearable),"sweep still active after cancellation");
                Expect(sweepCell,"cancel:"+sweep,false);
                Expect(harvestCell,"harvest",true);
                Require(crop.HarvestWhenReady,"harvest setting did not change");
                int harvest=Sample(harvestCell).TargetId;
                Require(harvest>0,"crop not addressable");
                Expect(harvestCell,"cancel:"+harvest,true);
                Require(!crop.HarvestWhenReady,"harvest setting still enabled");
                Expect(harvestCell,"cancel:"+harvest,false);
                log($"NATIVE_ORDER_HOST sweep={sweep} harvest={harvest} cancel=true competingPriority=true");
                step++;
            }
            if(step==1)
            {
                if(!Grid.Element[mopCell].IsLiquid) return false;
                Expect(mopCell,"mop",true);
                var mop=Grid.Objects[mopCell,(int)ObjectLayer.MopPlacer];
                Require(mop!=null && mop.GetComponent<Moppable>()!=null && Sample(mopCell).MopOrdered,"missing ONI mop marker");
                mopId=ChangePriority(mopCell,mop.GetComponent<Prioritizable>());
                Expect(mopCell,"mop",false);
                Expect(mopCell,"cancel:"+mopId,true);
                step++;
            }
            if(Grid.Objects[mopCell,(int)ObjectLayer.MopPlacer]!=null) return false;
            Require(!Sample(mopCell).MopOrdered,"cancelled mop remains in snapshot");
            Expect(mopCell,"cancel:"+mopId,false);
            log($"NATIVE_MOP_HOST mop={mopId} duplicateRejected=true cancel=true staleRejected=true gridMarkerGone=true");
            return true;
        }
    }
}
