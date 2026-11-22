using System;
using System.IO;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamSweepSymbolRange
    {
        int stage,prepared=-1,id,cell;
        float deadline;
        Storage storage;
        FixtureFrame photo;
        Vector3 oldCamera;
        float oldZoom;

        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,Action<string> log)
        {
            if(Environment.GetEnvironmentVariable("QY_SWEEP_RANGE")!="1" || round!=1 || stage==3) return true;
            var camera=CameraController.Instance;
            if(deadline==0)
            {
                deadline=Time.realtimeSinceStartup+70;
                oldCamera=camera.transform.position;oldZoom=camera.OrthographicSize;
            }
            if(Time.realtimeSinceStartup>=deadline) throw new TimeoutException("Storage sweep symbol stage "+stage+" id="+id);
            if(host && storage==null)
            {
                if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Storage sweep test requires an isolated host");
                storage=NativeFacilityChecks.Build(StorageLockerConfig.ID,ClusterManager.Instance.activeWorldId).GetComponent<Storage>();
                id=storage.GetComponent<KPrefabID>().InstanceID;
                cell=storage.GetComponent<Building>().GetCell();
            }
            bool expected=stage==1;
            string key="qy_sweep_"+stage;
            if(host && prepared!=stage)
            {
                if(!storage.GetComponent<KPrefabID>().isSpawned) return false;
                storage.SetOnlyFetchMarkedItems(expected);
                if(!SteamMatchmaking.SetLobbyData(room,key,id+"|"+cell)) throw new IOException("Could not publish storage sweep checkpoint");
                prepared=stage;
            }
            var item=SteamMatchmaking.GetLobbyData(room,key).Split('|');
            if(item.Length!=2) return false;
            id=int.Parse(item[0]);cell=int.Parse(item[1]);
            var target=host?storage.GetComponent<KSelectable>():NativeWorldMirror.GetBuildingSelectable(cell,id);
            var state=host?null:NativeEntityDisplay.GetObservedState(id);
            if(!host && (state?.Kind!=ColonyEntityKind.Storage || ((state.Flags&ColonyEntity.StorageSweepOnly)!=0)!=expected)) return false;
            var anim=target?.GetComponent<KBatchedAnimController>();
            if(anim==null || !anim.IsVisible() || anim.GetSymbolVisiblity("sweep")!=expected) return false;
            var point=Grid.CellToPosCCC(cell,Grid.SceneLayer.Building);
            point.y+=.8f;point.z=camera.transform.position.z;
            camera.SetTargetPos(point,5f,false);camera.SetPosition(point);camera.OrthographicSize=5f;
            if(photo==null)
            {
                string dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                photo=new FixtureFrame(Path.Combine(dir,$"sweep-{(host?"host":"guest")}-{stage}.png"));
                photo.Capture();return false;
            }
            if(!photo.Complete) return false;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,key)!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,key,"1");
            log($"SWEEP_RANGE stage={stage} cell={cell} id={id} sweepSymbol={expected} originalVisual=True");
            stage++;photo=null;
            if(stage!=3) return false;
            camera.SetTargetPos(oldCamera,oldZoom,false);camera.SetPosition(oldCamera);camera.OrthographicSize=oldZoom;
            return true;
        }
    }
}
