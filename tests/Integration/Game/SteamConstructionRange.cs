using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Runs against a private copy. ONI creates the plan and advances its native chore.
    internal sealed class SteamConstructionRange
    {
        const string Prefix="qy_range_";
        static readonly CultureInfo Culture=CultureInfo.InvariantCulture;
        int cell=-1,stage,planId;
        float deadline,nextLog;
        Constructable plan;
        FixtureFrame frame;
        Vector3 previousCamera;
        float previousZoom;
        bool frozen,started;
        static string Read(CSteamID room,string key)=>SteamMatchmaking.GetLobbyData(room,Prefix+key);
        static void Publish(CSteamID room,string key,string value)
        {
            if(!SteamMatchmaking.SetLobbyData(room,Prefix+key,value)) throw new IOException("Could not publish construction checkpoint");
        }
        static int StableId(GameObject obj)=>obj.GetComponent<KPrefabID>().InstanceID;

        void Place()
        {
            if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                throw new InvalidOperationException("Construction range requires its own isolated host save");
            var world=ClusterManager.Instance.activeWorld;
            var def=Assets.GetBuildingDef(StorageLockerConfig.ID);
            var builder=Components.LiveMinionIdentities.Items.First(d=>d!=null && ClusterUtil.GetMyWorldId(d)==world.id);
            int origin=Grid.PosToCell(builder);
            var category=def.MaterialCategory[0];
            var inventory=world.worldInventory.GetAccessibleAmounts();
            var material=MaterialSelector.GetValidMaterials(category,true)
                .FirstOrDefault(tag=>inventory.TryGetValue(tag,out float available) && available>=def.Mass[0]);
            if(!material.IsValid) throw new InvalidDataException("No accessible material for the construction range");
            var choices=Enumerable.Range(0,Grid.CellCount).Where(c=>Grid.WorldIdx[c]==world.id &&
                Grid.Visible[c]!=0 && Grid.IsValidBuildingCell(c) && !Grid.Solid[c] &&
                Grid.IsValidCell(Grid.CellBelow(c)) && Grid.Solid[Grid.CellBelow(c)] &&
                Grid.GetCellDistance(c,origin)<=10).OrderBy(c=>Grid.GetCellDistance(c,origin));
            foreach(int c in choices)
            {
                if(!HostWorld.VacantFootprint(def,c,Orientation.Neutral,world) ||
                    def.PlacementOffsets.Any(off=>!Grid.IsValidCell(Grid.OffsetCell(c,off)) || Grid.Solid[Grid.OffsetCell(c,off)])) continue;
                var obj=def.TryPlace(def.BuildingPreview,Grid.CellToPosCBC(c,Grid.SceneLayer.Building),Orientation.Neutral,new[]{material});
                if(obj==null) continue;
                cell=c;plan=obj.GetComponent<Constructable>();planId=StableId(obj);
                return;
            }
            throw new InvalidDataException("No nearby free floor for the construction range");
        }

        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,Action<string> log)
        {
            if(Environment.GetEnvironmentVariable("QY_CONSTRUCTION_RANGE")!="1" || round!=1 || stage==3) return true;
            var camera=CameraController.Instance;
            if(deadline==0)
            {
                deadline=Time.realtimeSinceStartup+145;
                previousCamera=camera.transform.position;previousZoom=camera.OrthographicSize;
            }
            if(Time.realtimeSinceStartup>=deadline) throw new TimeoutException("Construction range role="+(host?"host":"guest")+" stage="+stage+" cell="+cell);
            if(cell<0)
            {
                if(host) {Place();Publish(room,"cell",cell.ToString(Culture));}
                else if(!int.TryParse(Read(room,"cell"),out cell)) {cell=-1;return false;}
            }
            var center=Grid.CellToPosCCC(cell,Grid.SceneLayer.Building);
            center.y+=1;
            center.z=camera.transform.position.z;
            camera.SetTargetPos(center,6f,false);camera.SetPosition(center);camera.OrthographicSize=6f;
            string step=stage.ToString(Culture);
            string expected=Read(room,"step_"+step);
            if(host && string.IsNullOrEmpty(expected))
            {
                if(stage==0 && !started)
                {
                    if(!plan.GetComponent<KPrefabID>().isSpawned) return false;
                    plan.GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority,9));
                    started=true;
                }
                if(stage==0) Publish(room,"step_"+step,Snapshot(0));
                if(stage==1)
                {
                    if(frozen || SpeedControlScreen.Instance.IsPaused)
                    {
                        SpeedControlScreen.Instance.Unpause(false);frozen=false;
                    }
                    if(plan==null) throw new InvalidDataException("Native construction completed without an intermediate sample");
                    float fraction=plan.GetPercentComplete();
                    if(fraction<.28f) return false;
                    SpeedControlScreen.Instance.Pause(false);frozen=true;
                    Publish(room,"step_"+step,Snapshot(Mathf.RoundToInt(fraction*1000)));
                }
                if(stage==2)
                {
                    if(frozen || SpeedControlScreen.Instance.IsPaused)
                    {
                        SpeedControlScreen.Instance.Unpause(false);frozen=false;
                    }
                    var built=HostWorld.FindBuilding(cell);
                    if(built==null || built.GetComponent<BuildingComplete>()==null) return false;
                    SpeedControlScreen.Instance.Pause(false);
                    Publish(room,"step_"+step,StableId(built)+":1000:0");
                }
                return false;
            }
            if(string.IsNullOrEmpty(expected)) return false;
            var record=expected.Split(':');
            int id=int.Parse(record[0],Culture),fractionExpected=int.Parse(record[1],Culture);
            float massExpected=float.Parse(record[2],Culture);
            var target=host ? HostWorld.FindBuilding(cell)?.GetComponent<KSelectable>() : NativeWorldMirror.GetBuildingSelectable(cell,id);
            if(target==null || !target.IsSelectable || target.GetComponent<KBatchedAnimController>()?.IsVisible()!=true) return false;
            bool worldIcon=SteamAlertRange.HasEmergencyIcon(target);
            if(worldIcon!=(stage<2))
            {
                if(Time.realtimeSinceStartup>=nextLog)
                {
                    nextLog=Time.realtimeSinceStartup+4;
                    log($"CONSTRUCTION_ICON_WAIT stage={stage} cell={cell} id={id} worldIcon={worldIcon}");
                }
                return false;
            }
            int fractionObserved=fractionExpected;
            float massObserved=0;
            ProgressBar workBar=null;
            var status=host?null:NativeEntityDisplay.GetObservedState(id);
            if(stage<2)
            {
                if(host)
                {
                    fractionObserved=Mathf.RoundToInt(plan.GetPercentComplete()*1000);
                    massObserved=DeliveredMass();
                    workBar=NativeConstructionProgress.Source(plan);
                }
                else if(status?.Kind==ColonyEntityKind.Construction)
                {
                    fractionObserved=Mathf.RoundToInt(status.Value*1000);
                    massObserved=status.Limit;
                    workBar=NativeEntityDisplay.GetConstructionBar(id);
                }
                else return false;
            }
            else if(!host && (NativeWorldMirror.GetBuildingSelectable(cell,planId)!=null ||
                target.GetComponent<NativeBuildingIdentity>()?.SaveId!=id ||
                NativeEntityDisplay.GetConstructionBar(planId)!=null)) return false;
            bool barVisible=workBar!=null && workBar.gameObject.activeInHierarchy && workBar.bar.isActiveAndEnabled;
            int barValue=workBar==null?-1:Mathf.RoundToInt(workBar.PercentFull*1000);
            if(stage==0) planId=id;
            if(Math.Abs(fractionObserved-fractionExpected)>5 || Math.Abs(massObserved-massExpected)>.05f ||
                (stage==1 ? !barVisible || Math.Abs(barValue-fractionExpected)>5 : barVisible))
            {
                if(Time.realtimeSinceStartup>=nextLog)
                {
                    log($"CONSTRUCTION_RANGE_WAIT stage={stage} id={id} expected={fractionExpected}/{massExpected} observed={fractionObserved}/{massObserved} barVisible={barVisible} bar={barValue}");
                    nextLog=Time.realtimeSinceStartup+4;
                }
                return false;
            }
            SelectTool.Instance.Select(target);
            if(frame==null)
            {
                var dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                frame=new FixtureFrame(Path.Combine(dir,$"range-{(host?"host":"guest")}-{stage}.png"));
                frame.Capture();return false;
            }
            if(!frame.Complete) return false;
            string ack=Prefix+"seen_"+step;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,ack)!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,ack,"1");
            log(FormattableString.Invariant($"CONSTRUCTION_RANGE stage={stage} cell={cell} id={id} workPermille={fractionObserved} nativeVisible=True deliveredKg={massObserved} barVisible={barVisible} barPermille={barValue} worldIcon={worldIcon}"));
            stage++;frame=null;
            if(stage!=3) return false;
            SelectTool.Instance.Select(null,false);
            camera.SetTargetPos(previousCamera,previousZoom,false);camera.SetPosition(previousCamera);camera.OrthographicSize=previousZoom;
            return true;
        }
        float DeliveredMass()=>plan.GetComponent<Storage>().MassStored();
        string Snapshot(int fraction)=>FormattableString.Invariant($"{planId}:{fraction}:{DeliveredMass():R}");
    }
}
