using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamMeterRange
    {
        static readonly AccessTools.FieldRef<Battery,MeterController> batteryMeter=
            AccessTools.FieldRefAccess<Battery,MeterController>("meter");
        static readonly AccessTools.FieldRef<Reservoir,MeterController> reservoirMeter=
            AccessTools.FieldRefAccess<Reservoir,MeterController>("meter");
        readonly bool reservoir;
        readonly string name;
        readonly string logName;
        static readonly CultureInfo culture=CultureInfo.InvariantCulture;
        int stage,prepared=-1,cell,id;
        float timeout,nextLog;
        Battery battery;
        Storage water;
        GameObject building;
        int bay=-1;
        bool restorePause;
        float bayPreparedAt;
        FixtureFrame frame;
        Vector3 oldCamera;
        float oldZoom;
        internal SteamMeterRange(bool reservoir=false)
        {
            this.reservoir=reservoir;
            name=reservoir?"reservoir":"battery";
            logName=name.ToUpperInvariant()+"_RANGE";
        }
        string Key(string field)=>"qy_"+name+"_range_"+stage+"_"+field;
        static KBatchedAnimController DisplayedMeter(KSelectable target)=>
            target.GetComponentsInChildren<KBatchedAnimController>(true)
                .FirstOrDefault(anim=>anim!=null && anim.gameObject.name.EndsWith(".meter",StringComparison.Ordinal));
        KBatchedAnimController HostMeter()=>(reservoir?reservoirMeter(building.GetComponent<Reservoir>()):batteryMeter(battery))?.meterController;

        bool PrepareReservoir(Action<string> log)
        {
            var world=ClusterManager.Instance.activeWorld;
            var def=Assets.GetBuildingDef(LiquidReservoirConfig.ID);
            if(bay<0)
            {
                var origin=Grid.CellToXY(Grid.PosToCell(Components.LiveMinionIdentities.Items.First(d=>d!=null && ClusterUtil.GetMyWorldId(d)==world.id)));
                var occupiedPorts=new HashSet<int>(global::Game.Instance.logicCircuitManager.GetVisElements().Select(port=>port.GetLogicUICell()));
                var portOffsets=(def.LogicInputPorts?.Select(port=>port.cellOffset)??Enumerable.Empty<CellOffset>())
                    .Concat(def.LogicOutputPorts?.Select(port=>port.cellOffset)??Enumerable.Empty<CellOffset>()).ToArray();
                int best=int.MaxValue;
                var lo=world.WorldOffset;var hi=world.WorldOffset+world.WorldSize;
                // Keep every modified cell, including the two floor cells, within this world.
                for(int y=Math.Max(lo.y+3,origin.y-25);y<Math.Min(hi.y-5,origin.y+26);y++)
                    for(int x=Math.Max(lo.x+3,origin.x-35);x<Math.Min(hi.x-5,origin.x+36);x++)
                    {
                        int dist=Math.Abs(origin.x-x)+Math.Abs(origin.y-y);
                        if(dist<6 || dist>37)continue;
                        int anchor=Grid.XYToCell(x,y);
                        if(portOffsets.Any(offset=>occupiedPorts.Contains(Grid.OffsetCell(anchor,offset)))) continue;
                        int edits=0;bool free=true;
                        for(int dy=-1;dy<def.HeightInCells && free;dy++)for(int dx=0;dx<def.WidthInCells;dx++)
                        {
                            int c=Grid.XYToCell(x+dx,y+dy);
                            if(Grid.WorldIdx[c]!=world.id || !Grid.IsValidBuildingCell(c) || Grid.Element[c]==null ||
                                Grid.Element[c].hardness==byte.MaxValue || HostWorld.FindBuilding(c)!=null || NativeResources.First(c)!=null ||
                                Grid.Objects[c,(int)ObjectLayer.Building]!=null || Grid.Objects[c,(int)ObjectLayer.FoundationTile]!=null ||
                                Components.Brains.Items.Any(b=>b!=null && Grid.PosToCell(b)==c))
                            {free=false;break;}
                            if(dy<0 ? !Grid.Solid[c] : !Grid.Element[c].IsGas)edits++;
                        }
                        int score=edits*20+dist;
                        if(!free || score>=best)continue;
                        best=score;bay=anchor;
                    }
                if(bay<0)throw new InvalidDataException("No empty bounded reservoir test bay");
                var corner=Grid.CellToXY(bay);
                for(int dy=-1;dy<def.HeightInCells;dy++)for(int dx=0;dx<def.WidthInCells;dx++)
                {
                    int c=Grid.XYToCell(corner.x+dx,corner.y+dy);Grid.Reveal(c,255,true);
                    if(dy<0 && !Grid.Solid[c]) SimMessages.ReplaceElement(c,SimHashes.Granite,CellEventLogger.Instance.SandBoxTool,1600,295);
                    if(dy>=0 && !Grid.Element[c].IsGas) SimMessages.ReplaceElement(c,SimHashes.Oxygen,CellEventLogger.Instance.SandBoxTool,1,295);
                }
                restorePause=SpeedControlScreen.Instance.IsPaused;
                bayPreparedAt=Time.realtimeSinceStartup;
                log("RESERVOIR_TEST_BAY cell="+bay+" width="+def.WidthInCells+" height="+def.HeightInCells+" isolated=True");
                return false;
            }
            var pos=Grid.CellToXY(bay);
            bool floorReady=Enumerable.Range(0,def.WidthInCells).All(dx=>Grid.Solid[Grid.XYToCell(pos.x+dx,pos.y-1)]);
            bool empty=def.PlacementOffsets.All(o=>!Grid.Solid[Grid.OffsetCell(bay,o)]);
            if(!floorReady || !empty)
            {
                if(Time.realtimeSinceStartup-bayPreparedAt>12)throw new InvalidDataException("Reservoir test bay failed to settle");
                if(SpeedControlScreen.Instance.IsPaused)SpeedControlScreen.Instance.Unpause(false);
                return false;
            }
            if(restorePause && !SpeedControlScreen.Instance.IsPaused)SpeedControlScreen.Instance.Pause(false);
            if(!HostWorld.VacantFootprint(def,bay,Orientation.Neutral,world))throw new InvalidDataException("Reservoir test bay occupied");
            var preview=def.BuildingPreview;
            var oldPreviewPosition=preview.transform.position;
            try
            {
                // Vanilla evaluates automation visualizers at the preview transform.
                preview.transform.position=Grid.CellToPosCBC(bay,Grid.SceneLayer.Building);
                if(!def.IsValidPlaceLocation(preview,bay,Orientation.Neutral,false,out string reason) ||
                    !def.IsValidBuildLocation(preview,bay,Orientation.Neutral,false,out reason))
                    throw new InvalidDataException("Native reservoir placement rejected the prepared bay "+bay+": "+reason);
            }
            finally
            {
                preview.transform.position=oldPreviewPosition;
                preview.GetComponent<LogicPorts>()?.HackRefreshVisualizers();
            }
            var materials=def.MaterialCategory.Select(kind=>MaterialSelector.GetValidMaterials(kind,true).First()).ToArray();
            building=def.Build(bay,Orientation.Neutral,null,materials,295f,false,-1f);
            if(building==null)throw new InvalidDataException("Native reservoir failed to spawn in its valid test bay");
            return true;
        }

        string ChangeReading()
        {
            if(reservoir)
            {
                float desired=water.capacityKg*(stage==0?.2f:stage==1?.72f:.1f);
                float delta=desired-water.MassStored();
                if(delta>0) water.AddLiquid(SimHashes.Water,delta,295,byte.MaxValue,0);
                else water.ConsumeIgnoringDisease(ElementLoader.FindElementByHash(SimHashes.Water).tag,-delta);
                return GameUtil.GetFormattedMass(water.MassStored());
            }
            float charge=battery.Capacity*(stage==0?.2f:stage==1?.72f:0f);
            if(charge>=battery.JoulesAvailable) battery.AddEnergy(charge-battery.JoulesAvailable);
            else battery.ConsumeEnergy(battery.JoulesAvailable-charge);
            battery.EnergySim200ms(0);
            return FormattableString.Invariant($"{battery.JoulesAvailable:0}/{battery.Capacity:0} J");
        }

        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,Action<string> log)
        {
            if(Environment.GetEnvironmentVariable("QY_"+logName)!="1" || round!=1 || stage>=3) return true;
            var camera=CameraController.Instance;
            if(timeout==0)
            {
                timeout=Time.realtimeSinceStartup+100;
                oldCamera=camera.transform.position;oldZoom=camera.OrthographicSize;
            }
            if(Time.realtimeSinceStartup>timeout) throw new TimeoutException(name+" range step="+stage+" id="+id);
            if(host && building==null)
            {
                if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Meter range requires an isolated host");
                if(reservoir) {if(!PrepareReservoir(log))return false;}
                else building=NativeFacilityChecks.Build(BatteryConfig.ID,ClusterManager.Instance.activeWorldId);
                battery=building.GetComponent<Battery>();
                water=building.GetComponent<Storage>();
                cell=building.GetComponent<Building>().GetCell();
                id=building.GetComponent<KPrefabID>().InstanceID;
            }
            if(host && prepared!=stage)
            {
                var meter=HostMeter();
                if(meter==null || meter.GetCurrentNumFrames()==0) return false;
                string amount=ChangeReading();
                meter.UpdateAnim(0);
                string data=FormattableString.Invariant($"{cell}|{id}|{amount}|{meter.GetCurrentFrameIndex()-meter.GetFirstFrameIndex()}|{meter.GetPositionPercent():R}");
                if(!SteamMatchmaking.SetLobbyData(room,Key("state"),data)) throw new IOException("Meter range state unavailable");
                prepared=stage;
            }
            var state=SteamMatchmaking.GetLobbyData(room,Key("state")).Split('|');
            if(state.Length!=5) return false;
            cell=int.Parse(state[0],culture);id=int.Parse(state[1],culture);
            int expected=int.Parse(state[3],culture);
            var target=host?building.GetComponent<KSelectable>():NativeWorldMirror.GetBuildingSelectable(cell,id);
            if(target==null) return false;
            var center=Grid.CellToPosCCC(cell,Grid.SceneLayer.Building);
            center.y+=1;center.z=camera.transform.position.z;
            camera.SetTargetPos(center,6f,false);camera.SetPosition(center);camera.OrthographicSize=6f;
            if(DetailsScreen.Instance?.target!=target.gameObject)
            {
                SelectTool.Instance.Select(target,false);
                return false;
            }
            var actual=host?HostMeter():DisplayedMeter(target);
            int localFrame=actual==null?-1:actual.GetCurrentFrameIndex()-actual.GetFirstFrameIndex();
            bool showReadout=host||UnityEngine.Object.FindObjectsByType<SimpleInfoScreen>(FindObjectsSortMode.None)
                .Where(info=>info.isActiveAndEnabled).SelectMany(info=>info.GetComponentsInChildren<LocText>(false))
                .Any(label=>label.text.Contains(state[2]));
            if(!showReadout || actual==null || !actual.IsVisible() || expected!=localFrame)
            {
                if(Time.realtimeSinceStartup>=nextLog)
                {
                    log($"{logName}_WAIT stage={stage} id={id} readout={showReadout} frame={localFrame}/{expected} position={actual?.GetPositionPercent()}/{state[4]} frames={actual?.GetCurrentNumFrames()} stopped={actual?.IsStopped()} clip={actual?.currentAnim}");
                    nextLog=Time.realtimeSinceStartup+5;
                }
                return false;
            }
            if(frame==null)
            {
                var dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                frame=new FixtureFrame(Path.Combine(dir,$"{name}-{(host?"host":"guest")}-{stage}.png"));
                frame.Capture();return false;
            }
            if(!frame.Complete) return false;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,Key("ok"))!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,Key("ok"),"1");
            log($"{logName} stage={stage} cell={cell} id={id} amount={state[2]} meterFrame={localFrame}");
            stage++;frame=null;
            if(stage!=3) return false;
            SelectTool.Instance.Select(null,false);
            camera.SetTargetPos(oldCamera,oldZoom,false);camera.SetPosition(oldCamera);camera.OrthographicSize=oldZoom;
            return true;
        }
    }
}
