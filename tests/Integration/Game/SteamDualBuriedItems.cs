using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamDualBuriedItems
    {
        static readonly bool enabled=Environment.GetEnvironmentVariable("QY_CHECK_BURIED")=="1";
        static readonly FieldInfo markers=AccessTools.Field(typeof(EntombedItemVisualizer),"cellEntombedCounts");
        static readonly FieldInfo renderer=AccessTools.Field(AccessTools.Inner(typeof(EntombedItemVisualizer),"Data"),"controller");
        static readonly AccessTools.FieldRef<EntombedItemManager,List<int>> cells=AccessTools.FieldRefAccess<EntombedItemManager,List<int>>("cells");
        static readonly AccessTools.FieldRef<EntombedItemManager,List<int>> materials=AccessTools.FieldRefAccess<EntombedItemManager,List<int>>("elementIds");
        static SteamDualBuriedItems waiting;
        static bool releasing;
        int source=-1, tag, id, stage;
        bool paused,done;
        float since;
        Pickupable original;
        KBatchedAnimController initialMarker;
        FixtureFrame photo;

        static void Set(CSteamID room,string field,int value)
        {
            if(!SteamMatchmaking.SetLobbyData(room,"qy_buried_"+field,value.ToString())) throw new IOException("Buried item Steam metadata");
        }
        static bool Get(CSteamID room,string field,out int n)=>int.TryParse(SteamMatchmaking.GetLobbyData(room,"qy_buried_"+field),out n);

        static KBatchedAnimController Marker(EntombedItemVisualizer visual,int cell)
        {
            var data=((IDictionary)markers.GetValue(visual))[cell];
            return data==null ? null : (KBatchedAnimController)renderer.GetValue(data);
        }
        static bool Visible(KBatchedAnimController a)=>a!=null && a.enabled && a.gameObject.activeInHierarchy &&
            a.IsVisible() && a.GetBatchInstanceData()!=null;

        void Choose(EntombedItemVisualizer visual,int origin)
        {
            int world=ClusterManager.Instance.activeWorldId,best=int.MaxValue;
            bool Suitable(int cell) => Grid.IsValidCell(cell) && Grid.WorldIdx[cell]==world && Grid.Solid[cell] &&
                visual.IsEntombedItem(cell) && !Diggable.Undiggable(Grid.Element[cell]) && HostWorld.FindBuilding(cell)==null;
            int Score(int cell)=>Grid.GetCellDistance(origin,cell)+(Grid.Visible[cell]==0 ? 500 : 0);
            foreach(var p in Components.Pickupables.Items)
            {
                if(p==null || !p.IsEntombed || p.storage!=null || !p.gameObject.activeInHierarchy ||
                    p.GetComponent<CreatureBrain>()!=null || p.KPrefabID==null) continue;
                int cell=Grid.PosToCell(p.transform.position);
                if(!Suitable(cell) || Score(cell)>=best) continue;
                best=Score(cell); source=cell;tag=p.PrefabID().GetHash(); original=p;
            }
            var manager=SaveGame.Instance.entombedItemManager;
            var positions=cells(manager);
            for(int i=0;i<positions.Count;i++)
            {
                int cell=positions[i];
                if(!Suitable(cell) || Score(cell)>=best) continue;
                var element=ElementLoader.FindElementByHash((SimHashes)materials(manager)[i]);
                if(element==null || !element.IsSolid || element==Grid.Element[cell]) continue;
                best=Score(cell);source=cell;tag=element.tag.GetHash();original=null;
            }
            if(source<0) throw new InvalidDataException("No native entombed item in the isolated colony");
            // A buried item can be outside the initially surveyed region of this save.
            Grid.CellToXY(source,out int x,out int y);
            for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=1;dx++)
            {
                int c=Grid.XYToCell(x+dx,y+dy);
                if(Grid.IsValidCell(c) && Grid.WorldIdx[c]==world) Grid.Reveal(c,255,true);
            }
        }

        [HarmonyPatch(typeof(EntombedItemManager),"SpawnObjects")]
        static class ManagerRelease
        {
            static void Prefix()=>releasing=waiting!=null;
            static void Finalizer()=>releasing=false;
        }
        [HarmonyPatch(typeof(Substance),"SpawnResource",new[]{typeof(Vector3),typeof(float),typeof(float),typeof(byte),typeof(int),typeof(bool),typeof(bool),typeof(bool)})]
        static class ResourceRelease
        {
            static void Postfix(Vector3 __0,GameObject __result)
            {
                if(!releasing || waiting==null || waiting.original!=null || Grid.PosToCell(__0)!=waiting.source ||
                    __result==null || __result.PrefabID().GetHash()!=waiting.tag) return;
                waiting.original=__result.GetComponent<Pickupable>();
            }
        }

        bool Photograph(bool host,string step)
        {
            if(photo==null)
            {
                string dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                photo=new FixtureFrame(Path.Combine(dir,"buried-"+(host?"host":"guest")+"-"+step+".png"));
                photo.Capture();return false;
            }
            return photo.Complete;
        }

        internal bool Check(CSteamID room,CSteamID guest,bool host,int round,Action<string> log)
        {
            if(!enabled || round!=1 || done) return true;
            if(since==0) since=Time.unscaledTime;
            if(Time.unscaledTime-since>80) throw new TimeoutException($"Buried item host={host} stage={stage} cell={source} id={id} tag={tag}");
            var visual=global::Game.Instance.GetComponent<EntombedItemVisualizer>();
            if(source<0)
            {
                if(host)
                {
                    if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                        throw new InvalidOperationException("Buried item fixture requires isolated save");
                    int x=int.Parse(SteamMatchmaking.GetLobbyData(room,"qy_dual_dig_x"));
                    int y=int.Parse(SteamMatchmaking.GetLobbyData(room,"qy_dual_dig_y"));
                    Choose(visual,Grid.XYToCell(x,y));paused=SpeedControlScreen.Instance.IsPaused;
                    Set(room,"tag",tag);Set(room,"cell",source);
                }
                else
                {
                    if(!Get(room,"tag",out tag) || !Get(room,"cell",out int c)) return false;
                    source=c;
                }
            }
            CameraController.Instance.SetTargetPos(Grid.CellToPosCCC(source,Grid.SceneLayer.Move),6,false);
            if(stage==0)
            {
                initialMarker=Marker(visual,source);
                if(!Grid.Solid[source] || !Visible(initialMarker) || !Photograph(host,"before")) return false;
                if(host && SteamMatchmaking.GetLobbyMemberData(room,guest,"qy_buried_seen")!="1") return false;
                log($"DUAL_BURIED_MARKER cell={source} tag={tag} markerVisible=True");
                if(host)
                {
                    waiting=this;SimMessages.Dig(source);
                    SpeedControlScreen.Instance.Unpause(false);
                }
                else SteamMatchmaking.SetLobbyMemberData(room,"qy_buried_seen","1");
                stage=1;photo=null;
            }
            if(Grid.Solid[source] || visual.IsEntombedItem(source) || Marker(visual,source)!=null ||
                (Visible(initialMarker) && Grid.PosToCell(initialMarker.transform.position)==source)) return false;
            KSelectable shown;
            if(host)
            {
                if(original==null || original.IsEntombed || original.storage!=null) return false;
                shown=original.GetComponent<KSelectable>();
                if(!Visible(shown?.GetComponent<KBatchedAnimController>())) return false;
                if(id==0) {id=original.KPrefabID.InstanceID;Set(room,"id",id);}
            }
            else
            {
                if(!Get(room,"id",out id)) return false;
                shown=NativeResources.GetSelectable(id)??NativeEntityDisplay.GetSelectable(id);
            }
            if(shown==null || shown.PrefabID().GetHash()!=tag || !Visible(shown.GetComponent<KBatchedAnimController>()) ||
                !Photograph(host,"after")) return false;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,guest,"qy_buried_done")!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,"qy_buried_done","1");
            if(host && paused) SpeedControlScreen.Instance.Pause(false);
            log($"DUAL_BURIED_RELEASE cell={source} id={id} tag={tag} markerVisible=False productVisible=True");
            waiting=null;done=true;return true;
        }
    }
}
