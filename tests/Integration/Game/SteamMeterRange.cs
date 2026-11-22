using System;
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
                building=NativeFacilityChecks.Build(reservoir?LiquidReservoirConfig.ID:BatteryConfig.ID,ClusterManager.Instance.activeWorldId);
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
