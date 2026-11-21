using System;
using System.IO;
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
        readonly string recording=Environment.GetEnvironmentVariable("QY_NATIVE_RECORDING");
        int mopId, step;
        int overlapCell, anchorCell;
        Clearable overlapSeed;

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
        void RecordAccepted(int index,int cell,string cmd,int entityId=-1)
        {
            if(string.IsNullOrEmpty(recording)) { Expect(cell,cmd,true); return; }
            Grid.CellToXY(cell,out int x,out int y);
            var stem=Path.Combine(recording,"order-"+index);
            byte[] Capture()
            {
                for(int sample=0;;sample++)
                {
                    var bytes=HostWorld.CapturePage(x-ColonyView.Width/2,y-ColonyView.Height/2,sample);
                    if(entityId<0) return bytes;
                    var page=EntityStateCodec.Decode(ColonyView.Decode(bytes).EntityPayload);
                    if(entityId>=page.From && entityId<=page.Through)
                    {
                        Require(page.Entities.Any(e=>e.Id==entityId),"recorded target missing "+entityId);
                        return bytes;
                    }
                }
            }
            var before=Capture();
            string reply=Command(cell,cmd);
            Require(reply.StartsWith("OK ",StringComparison.Ordinal),cmd+" => "+reply);
            File.WriteAllBytes(stem+"-before.view",before);
            File.WriteAllBytes(stem+"-after.view",Capture());
            using(var w=new BinaryWriter(File.Create(stem+".bin")))
            { w.Write(x); w.Write(y); Queyang.Core.Wire.Text(w,cmd,160); Queyang.Core.Wire.Text(w,reply,8192); }
        }
        static ColonyCell Sample(int cell)
        {
            Grid.CellToXY(cell,out int x,out int y);
            var view=ColonyView.Decode(HostWorld.Capture(x,y));
            return view.Cells[(y-view.Y)*ColonyView.Width+x-view.X];
        }
        int ChangePriority(int cell,Prioritizable target,bool record=false)
        {
            var view=Sample(cell);
            int old=target.GetMasterPriority().priority_value;
            int next=old==7?6:7;
            Require(view.TargetId>0 && view.OrderPriority==old,"snapshot priority mismatch at "+cell);
            string cmd=$"priority{next}/{old}:{view.TargetId}";
            if(record) RecordAccepted(1,cell,cmd,target.GetComponent<KPrefabID>().InstanceID);
            else Expect(cell,cmd,true);
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
                if (!Components.HarvestDesignatables.Items.Contains(crop) || NativeResources.First(sweepCell)==null) return false;
                RecordAccepted(0,sweepCell,"sweep",clearable.GetComponent<KPrefabID>().InstanceID);
                Require(NativeEntitySampler.IsMarked(clearable),"seed not marked for sweeping");
                int sweep=ChangePriority(sweepCell,clearable.GetComponent<Prioritizable>(),true);
                Expect(sweepCell,"cancel:"+sweep,true);
                Require(!NativeEntitySampler.IsMarked(clearable),"sweep still active after cancellation");
                Expect(sweepCell,"cancel:"+sweep,false);
                Expect(sweepCell,"sweep",true);
                int replacement=Sample(sweepCell).TargetId;
                Require(replacement>0 && replacement!=sweep,"reissued sweep reused cancelled target ID");
                Expect(sweepCell,"cancel:"+sweep,false);
                Require(NativeEntitySampler.IsMarked(clearable),"late cancellation removed replacement order");
                Expect(sweepCell,"cancel:"+replacement,true);
                RecordAccepted(2,harvestCell,"harvest",crop.GetComponent<KPrefabID>().InstanceID);
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
                RecordAccepted(3,mopCell,"mop");
                var mop=Grid.Objects[mopCell,(int)ObjectLayer.MopPlacer];
                Require(mop!=null && mop.GetComponent<Moppable>()!=null && Sample(mopCell).MopOrdered,"missing ONI mop marker");
                mopId=ChangePriority(mopCell,mop.GetComponent<Prioritizable>());
                Expect(mopCell,"mop",false);
                Expect(mopCell,"cancel:"+mopId,true);
                step++;
            }
            if(step==2)
            {
                if(Grid.Objects[mopCell,(int)ObjectLayer.MopPlacer]!=null) return false;
                Require(!Sample(mopCell).MopOrdered,"cancelled mop remains in snapshot");
                Expect(mopCell,"cancel:"+mopId,false);
                log($"NATIVE_MOP_HOST mop={mopId} duplicateRejected=true cancel=true staleRejected=true gridMarkerGone=true");
                PrepareOverlap();
                step++;
            }
            if (NativeResources.First(overlapCell)==null) return false;
            var building=Sample(anchorCell);
            Expect(overlapCell,"sweep",true);
            var overlap=Sample(overlapCell);
            Require(overlap.AnchorDX!=0 && overlap.AnchorDY==0 && building.TargetId==overlap.TargetId &&
                overlap.OrderTargetId>0 && overlap.OrderTargetId!=building.TargetId,
                "multicell building and overlapping sweep did not receive distinct tokens");
            string menu="building/"+building.TargetId+"/0";
            Require(Command(overlapCell,menu).StartsWith("BLD1 ") && Command(anchorCell,menu).StartsWith("BLD1 "),
                "building menu did not resolve the same building from its footprint");
            int next=overlap.SeparateOrderPriority==7?6:7;
            Expect(overlapCell,$"priority{next}/{overlap.SeparateOrderPriority}:{overlap.OrderTargetId}",true);
            Require(overlapSeed.GetComponent<Prioritizable>().GetMasterPriority().priority_value==next &&
                Sample(anchorCell).OrderPriority==building.OrderPriority,"overlapping priority edited the wrong object");
            int seedId=overlapSeed.GetComponent<KPrefabID>().InstanceID;
            Expect(overlapCell,$"object/{seedId}/cancel:{building.TargetId}",false);
            Require(NativeEntitySampler.IsMarked(overlapSeed),"building token cancelled the selected seed");
            Expect(overlapCell,"cancel:"+overlap.OrderTargetId,true);
            Expect(overlapCell,"cancel:"+overlap.OrderTargetId,false);
            Require(!NativeEntitySampler.IsMarked(overlapSeed) && Sample(overlapCell).OrderTargetId==0 &&
                Sample(overlapCell).TargetId==building.TargetId && Command(anchorCell,menu).StartsWith("BLD1 "),
                "cancelling a sweep changed the underlying building");
            log($"NATIVE_OVERLAP_HOST footprint={overlapCell} anchor={anchorCell} building={building.TargetId} sweep={overlap.OrderTargetId} wrongObjectRejected=true buildingMenuIntact=true");
            return true;
        }
        void PrepareOverlap()
        {
            foreach(var b in Components.BuildingCompletes.Items)
            {
                if (b==null || ClusterUtil.GetMyWorldId(b)!=ClusterManager.Instance.activeWorldId) continue;
                var structure=b.GetComponent<Building>();
                int anchor=structure.GetCell();
                foreach(var offset in b.Def.PlacementOffsets)
                {
                    var delta=Rotatable.GetRotatedCellOffset(offset,structure.Orientation);
                    if(delta.x==0 || delta.y!=0) continue;
                    int cell=Grid.OffsetCell(anchor,delta);
                    if (!Grid.IsValidCell(cell) || Grid.Visible[cell]==0 || Grid.Solid[cell] || !Grid.Solid[Grid.CellBelow(cell)] ||
                        HostWorld.FindBuilding(cell)!=b.gameObject || NativeResources.First(cell)!=null) continue;
                    var seed=Util.KInstantiate(Assets.GetPrefab(BasicSingleHarvestPlantConfig.SEED_ID),Grid.CellToPosCBC(cell,Grid.SceneLayer.Ore));
                    seed.SetActive(true);
                    overlapSeed=seed.GetComponent<Clearable>(); overlapCell=cell; anchorCell=anchor;
                    return;
                }
            }
            throw new InvalidOperationException("No empty floor cell in a visible multi-cell building footprint");
        }
    }
}
