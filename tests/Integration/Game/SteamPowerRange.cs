using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Controlled power input to ONI's actual EnergyConsumer and RequireInputs.
    // This isolates status projection from circuit construction and worker AI.
    internal sealed class SteamPowerRange
    {
        static readonly AccessTools.FieldRef<EnergyConsumer,ushort> circuit=
            AccessTools.FieldRefAccess<EnergyConsumer,ushort>("<CircuitID>k__BackingField");
        int stage,prepared=-1,cell,id;
        float until,reportAt;
        ResearchCenter station;
        FixtureFrame photo;
        Vector3 previousCamera;
        float previousZoom;
        bool testedStale;
        string Key(string name)=>"qy_power_range_"+stage+"_"+name;

        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,Action<string> log)
        {
            if(Environment.GetEnvironmentVariable("QY_POWER_RANGE")!="1" || round!=1 || stage>=3) return true;
            var camera=CameraController.Instance;
            if(until==0)
            {
                until=Time.realtimeSinceStartup+90;
                previousCamera=camera.transform.position;previousZoom=camera.OrthographicSize;
            }
            if(Time.realtimeSinceStartup>until) throw new TimeoutException("Power warnings stage="+stage+" id="+id);
            var statuses=Db.Get().BuildingStatusItems;
            var noWire=statuses.NoWireConnected;
            var needPower=statuses.NeedPower;
            if(host && station==null)
            {
                if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Power range requires an isolated colony");
                station=NativeFacilityChecks.Build(ResearchCenterConfig.ID,ClusterManager.Instance.activeWorldId).GetComponent<ResearchCenter>();
                cell=station.GetComponent<Building>().GetCell();
                id=station.GetComponent<KPrefabID>().InstanceID;
            }
            if(host && prepared!=stage)
            {
                if(!station.GetComponent<KPrefabID>().isSpawned) return false;
                var energy=station.GetComponent<EnergyConsumer>();
                circuit(energy)=stage==0?ushort.MaxValue:(ushort)1;
                var power=stage==0?CircuitManager.ConnectionStatus.NotConnected:stage==1?
                    CircuitManager.ConnectionStatus.Unpowered:CircuitManager.ConnectionStatus.Powered;
                energy.SetConnectionStatus(power);
                station.GetComponent<RequireInputs>().Sim200ms(0);
                if(!SteamMatchmaking.SetLobbyData(room,Key("device"),id+"|"+cell))
                    throw new IOException("Could not publish power fixture device");
                prepared=stage;
            }
            var state=SteamMatchmaking.GetLobbyData(room,Key("device")).Split('|');
            if(state.Length!=2) return false;
            id=int.Parse(state[0]);cell=int.Parse(state[1]);
            var target=host?station.GetComponent<KSelectable>():NativeWorldMirror.GetBuildingSelectable(cell,id);
            if(target==null) return false;
            var center=Grid.CellToPosCCC(cell,Grid.SceneLayer.Building);
            center.y+=.7f;center.z=camera.transform.position.z;
            camera.SetTargetPos(center,6,false);camera.SetPosition(center);camera.OrthographicSize=6;
            if(DetailsScreen.Instance?.target!=target.gameObject)
            {
                SelectTool.Instance.Select(target,false);
                return false;
            }
            if(!host && stage==2 && !testedStale)
            {
                testedStale=true;
                // Reproduce the stale local warning arriving after the host corrected it.
                target.AddStatusItem(noWire);
            }
            var renderer=global::Game.Instance.statusItemRenderer;
            bool worldWire=false,worldPower=false;
            foreach(var entry in renderer.visibleEntries)
            {
                if(entry.selectable!=target || !entry.hasVisibleStatusItems || entry.mesh==null || entry.mesh.vertexCount==0) continue;
                worldWire|=entry.statusItems.Any(s=>s.Id==noWire.Id);
                worldPower|=entry.statusItems.Any(s=>s.Id==needPower.Id);
            }
            bool localWire=false,localPower=false;
            foreach(var entry in target.GetStatusItemGroup())
            {
                localWire|=entry.item.Id==noWire.Id;
                localPower|=entry.item.Id==needPower.Id;
            }
            if(worldWire!=(stage==0) || worldPower!=(stage==1) || worldWire!=localWire || worldPower!=localPower)
            {
                if(Time.realtimeSinceStartup>=reportAt)
                {
                    log($"POWER_RANGE_WAIT stage={stage} id={id} wireMesh={worldWire}/{localWire} powerMesh={worldPower}/{localPower}");
                    reportAt=Time.realtimeSinceStartup+4;
                }
                return false;
            }
            if(photo==null)
            {
                string dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                photo=new FixtureFrame(Path.Combine(dir,$"power-{(host?"host":"guest")}-{stage}.png"));
                photo.Capture();return false;
            }
            if(!photo.Complete) return false;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,Key("ok"))!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,Key("ok"),"1");
            log($"POWER_RANGE stage={stage} cell={cell} id={id} noWireIcon={worldWire} needPowerIcon={worldPower}");
            stage++;photo=null;
            if(stage!=3) return false;
            SelectTool.Instance.Select(null,false);
            camera.SetTargetPos(previousCamera,previousZoom,false);camera.SetPosition(previousCamera);camera.OrthographicSize=previousZoom;
            return true;
        }
    }
}
