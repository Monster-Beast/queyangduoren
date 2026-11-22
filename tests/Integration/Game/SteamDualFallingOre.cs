using System;
using System.Globalization;
using System.IO;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamDualFallingOre
    {
        const float TestMass=12001f;
        const int Height=6;
        static readonly bool enabled=Environment.GetEnvironmentVariable("QY_CHECK_FALLING_ORE")=="1";
        static SteamDualFallingOre awaitingDig;
        static bool inCallback;
        int source=-1, id, stage, moves, speed, measuredCell=-1;
        float firstY, lastY, stillSince, started;
        bool paused, gravity, done;
        Pickupable hostOre;

        static string Get(CSteamID lobby,string key)=>SteamMatchmaking.GetLobbyData(lobby,"qy_ore_"+key);
        static void Set(CSteamID lobby,string key,string value)
        {
            if(!SteamMatchmaking.SetLobbyData(lobby,"qy_ore_"+key,value)) throw new IOException("Could not publish falling ore "+key);
        }

        static int PickShaft(int x,int y)
        {
            int world=ClusterManager.Instance.activeWorldId;
            for(int offset=11;offset<=30;offset++)
                foreach(int sign in new[]{1,-1})
                    for(int dy=-8;dy<=8;dy++)
                    {
                        int cx=x+sign*offset,cy=y+dy;
                        if(cx<2 || cx>=Grid.WidthInCells-2 || cy<Height+3 || cy>=Grid.HeightInCells-2) continue;
                        bool clear=true;
                        for(int column=-1;column<=1 && clear;column++)
                            for(int row=-Height;row<=0;row++)
                            {
                                int cell=Grid.XYToCell(cx+column,cy+row);
                                if(Grid.WorldIdx[cell]!=world || !Grid.IsValidBuildingCell(cell) ||
                                    HostWorld.FindBuilding(cell)!=null || NativeResources.First(cell)!=null ||
                                    Grid.Element[cell].hardness==byte.MaxValue) { clear=false;break; }
                            }
                        if(clear) return Grid.XYToCell(cx,cy);
                    }
            throw new InvalidDataException("No unoccupied shaft for the isolated falling-ore check");
        }

        void Seed()
        {
            Grid.CellToXY(source,out int x,out int y);
            for(int dx=-1;dx<=1;dx++)
                for(int dy=-Height;dy<=0;dy++)
                {
                    int cell=Grid.XYToCell(x+dx,y+dy);
                    Grid.Reveal(cell,255,true);
                    var element=dx!=0 || dy==-Height ? SimHashes.Granite : dy==0 ? SimHashes.Obsidian : SimHashes.Oxygen;
                    SimMessages.ReplaceElement(cell,element,CellEventLogger.Instance.SandBoxTool,element==SimHashes.Oxygen?1:1600,295);
                }
        }

        [HarmonyPatch(typeof(WorldDamage),"OnDigComplete")]
        static class Dug
        {
            static void Prefix(int __0) { inCallback=awaitingDig!=null && awaitingDig.source==__0; }
            static void Finalizer() { inCallback=false; }
        }
        [HarmonyPatch(typeof(Substance),"SpawnResource",new[]{typeof(Vector3),typeof(float),typeof(float),typeof(byte),typeof(int),typeof(bool),typeof(bool),typeof(bool)})]
        static class Spawned
        {
            static void Postfix(GameObject __result)
            {
                if(!inCallback || awaitingDig==null || __result==null) return;
                var ore=__result.GetComponent<Pickupable>();
                if(!NativeResources.IsOre(ore) || ore.PrimaryElement.ElementID!=SimHashes.Obsidian) return;
                awaitingDig.hostOre=ore;
                ore.PrimaryElement.Mass=TestMass;
                SpeedControlScreen.Instance.Pause(false);
            }
        }

        int CountVisible()
        {
            int count=0;
            var orig=KPrefabIDTracker.Get().GetInstance(id);
            if(orig!=null && NativeResources.IsOre(orig.GetComponent<Pickupable>()) &&
                orig.GetComponent<KBatchedAnimController>().IsVisible()) count++;
            foreach(var p in UnityEngine.Object.FindObjectsByType<NativeEntityIdentity>(FindObjectsSortMode.None))
                if(p.SaveId==id && p.GetComponent<KBatchedAnimController>().IsVisible()) count++;
            return count;
        }

        internal bool Check(CSteamID lobby,CSteamID guest,bool host,int round,Action<string> log)
        {
            if(!enabled || round!=1 || done) return true;
            if(started==0) started=Time.unscaledTime;
            if(Time.unscaledTime-started>100) throw new TimeoutException($"Falling ore host={host} stage={stage} source={source} id={id} moves={moves}");
            if(source<0)
            {
                if(host)
                {
                    if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                        throw new InvalidOperationException("Falling ore fixture requires isolated host save");
                    int x=int.Parse(SteamMatchmaking.GetLobbyData(lobby,"qy_dual_dig_x"));
                    int y=int.Parse(SteamMatchmaking.GetLobbyData(lobby,"qy_dual_dig_y"));
                    source=PickShaft(x,y);
                    speed=SpeedControlScreen.Instance.GetSpeed();paused=SpeedControlScreen.Instance.IsPaused;
                    SpeedControlScreen.Instance.SetSpeed(0);
                    log("DUAL_ORE_SEED source="+source);
                    Set(lobby,"source",source.ToString());Seed();
                    SpeedControlScreen.Instance.Unpause(false);
                }
                else if(int.TryParse(Get(lobby,"source"),out int c)) source=c;
                else return false;
            }
            CameraController.Instance.SetTargetPos(Grid.CellToPosCCC(source-(Height/2)*Grid.WidthInCells,Grid.SceneLayer.Move),8f,false);
            if(host && stage==0)
            {
                if(Grid.Element[source].id!=SimHashes.Obsidian || !Grid.Solid[source-Height*Grid.WidthInCells]) return false;
                for(int i=1;i<Height;i++) if(Grid.Solid[source-i*Grid.WidthInCells]) return false;
                if(SteamMatchmaking.GetLobbyMemberData(lobby,guest,"qy_ore_armed")!="1") return false;
                awaitingDig=this;SimMessages.Dig(source);stage=1;log("DUAL_ORE_DIG source="+source);return false;
            }
            if(!host && stage==0)
            {
                if(Grid.Element[source].id!=SimHashes.Obsidian) return false;
                SteamMatchmaking.SetLobbyMemberData(lobby,"qy_ore_armed","1");stage=1;
            }
            if(stage==1)
            {
                if(host)
                {
                    if(hostOre==null) return false;
                    id=hostOre.KPrefabID.InstanceID;Set(lobby,"id",id.ToString());awaitingDig=null;
                    log("DUAL_ORE_NATIVE_SPAWN id="+id+" source="+source);
                }
                else if(!int.TryParse(Get(lobby,"id"),out id)) return false;
                stage=2;
            }
            var selected=host ? hostOre?.GetComponent<KSelectable>() : NativeResources.GetSelectable(id);
            if(selected==null) return false;
            var anim=selected.GetComponent<KBatchedAnimController>();
            if(anim==null || !anim.IsVisible() || anim.GetBatchInstanceData()==null || !selected.IsSelectable) return false;
            var primary=selected.GetComponent<PrimaryElement>();
            if(primary.ElementID!=SimHashes.Obsidian || Math.Abs(primary.Mass-TestMass)>.01f) return false;
            var pos=selected.transform.position;
            int landing=Grid.PosToCell(pos);
            if(measuredCell!=landing)
            {
                int count=CountVisible();
                if(count!=1) throw new InvalidDataException($"Falling ore id={id} visible={count}");
                log(FormattableString.Invariant($"DUAL_ORE_POSITION id={id} cell={landing} x={pos.x:0.000} y={pos.y:0.000} visible={count}"));
                measuredCell=landing;
            }
            if(stage==2)
            {
                firstY=lastY=pos.y;
                if(!host) SteamMatchmaking.SetLobbyMemberData(lobby,"qy_ore_start","1");
                if(host && SteamMatchmaking.GetLobbyMemberData(lobby,guest,"qy_ore_start")!="1") return false;
                if(host) SpeedControlScreen.Instance.Unpause(false);
                stage=3;
            }
            if(pos.y<lastY-.015f) { moves++;stillSince=Time.unscaledTime; }
            lastY=pos.y;
            if(host) gravity|=GameComps.Gravities.Has(hostOre.gameObject);
            if(moves<2 || firstY-pos.y<3) return false;
            if(host && stage==3 && gravity && landing==source-(Height-1)*Grid.WidthInCells && Time.unscaledTime-stillSince>.5f)
            {
                Set(lobby,"x",pos.x.ToString("R",CultureInfo.InvariantCulture));
                Set(lobby,"y",pos.y.ToString("R",CultureInfo.InvariantCulture));
                stage=4;
            }
            if(!float.TryParse(Get(lobby,"x"),NumberStyles.Float,CultureInfo.InvariantCulture,out float endX) ||
                !float.TryParse(Get(lobby,"y"),NumberStyles.Float,CultureInfo.InvariantCulture,out float endY) ||
                Vector2.Distance(pos,new Vector2(endX,endY))>.015f || Time.unscaledTime-stillSince<.5f) return false;
            int visible=CountVisible();
            if(visible!=1) throw new InvalidDataException($"Falling ore id={id} visible={visible}");
            if(host && SteamMatchmaking.GetLobbyMemberData(lobby,guest,"qy_ore_done")!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(lobby,"qy_ore_done","1");
            if(host)
            {
                SpeedControlScreen.Instance.SetSpeed(speed);
                if(paused) SpeedControlScreen.Instance.Pause(false);
            }
            log(FormattableString.Invariant($"DUAL_FALLING_ORE id={id} source={source} landing={landing} x={pos.x:0.000} y={pos.y:0.000} drop={firstY-pos.y:0.00} moves={moves} kg={primary.Mass} visible={visible}"));
            done=true;return true;
        }
    }
}
