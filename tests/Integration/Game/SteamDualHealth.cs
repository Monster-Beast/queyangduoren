using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamDualHealth
    {
        static readonly Func<NameDisplayScreen,GameObject,NameDisplayScreen.Entry> entry=
            AccessTools.MethodDelegate<Func<NameDisplayScreen,GameObject,NameDisplayScreen.Entry>>(AccessTools.Method(typeof(NameDisplayScreen),"GetEntry"));
        int id,step,changed=-1;
        Health patient;
        FixtureFrame frame;
        float stable,deadline,diagnostic;
        readonly bool critter=Environment.GetEnvironmentVariable("QY_CHECK_CRITTER_HEALTH")=="1";
        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,Action<string> log)
        {
            if((!critter && Environment.GetEnvironmentVariable("QY_CHECK_HEALTH")!="1") || round!=1 || step>=3) return true;
            if(deadline==0) deadline=Time.realtimeSinceStartup+100;
            if(Time.realtimeSinceStartup>deadline) throw new TimeoutException("Health bar step="+step+" id="+id);
            if(id==0)
            {
                if(host)
                {
                    if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                        throw new InvalidOperationException("Health check requires isolated host colony");
                    patient=critter ? SpawnCritter() : PrintDupe();
                    id=patient.GetComponent<KPrefabID>().InstanceID;
                    SteamMatchmaking.SetLobbyData(room,"qy_health_id",id.ToString());
                }
                else if(!int.TryParse(SteamMatchmaking.GetLobbyData(room,"qy_health_id"),out id)) return false;
            }
            var target=host?patient.gameObject:(critter?NativeEntityDisplay.GetSelectable(id):NativeWorldMirror.GetDupeSelectable(id))?.gameObject;
            if(target==null) return false;
            if(host && changed!=step)
            {
                if(!patient.isSpawned || patient.healthBar==null) return false;
                if(step==0) patient.Damage(patient.hitPoints-.15f*patient.maxHitPoints);
                else
                {
                    patient.hitPoints=patient.maxHitPoints*(step==1?.55f:1f);
                    patient.UpdateHealthBar();
                }
                changed=step;
            }
            var camera=CameraController.Instance;var point=target.transform.position;
            point.y+=.65f;point.z=camera.transform.position.z;
            camera.SetTargetPos(point,3.5f,false);camera.SetPosition(point);camera.OrthographicSize=3.5f;
            var info=entry(NameDisplayScreen.Instance,target);
            var bar=info?.healthBar;
            bool expected=step<2;
            bool shown=bar!=null && bar.gameObject.activeInHierarchy && bar.bar.isActiveAndEnabled;
            if(Time.realtimeSinceStartup>=diagnostic)
            {
                log("DUAL_HEALTH_WAIT step="+step+" id="+id+" visible="+shown+" fill="+(bar?.PercentFull??-1));
                diagnostic=Time.realtimeSinceStartup+5;
            }
            if(info==null || bar==null || shown!=expected || (expected && Math.Abs(bar.PercentFull-(step==0?.15f:.55f))>.005f))
            {stable=0;return false;}
            if(stable==0) stable=Time.realtimeSinceStartup;
            if(Time.realtimeSinceStartup-stable<.4f) return false;
            if(frame==null)
            {
                var dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                frame=new FixtureFrame(Path.Combine(dir,"health-"+(host?"host":"guest")+"-"+step+".png"));frame.Capture();return false;
            }
            if(!frame.Complete) return false;
            string key="qy_health_ok_"+step;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,key)!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,key,"1");
            log("DUAL_HEALTH_BAR step="+step+" id="+id+" shown="+shown+" fill="+bar.PercentFull+
                " colour="+ColorUtility.ToHtmlStringRGBA(bar.barColor)+" prefab="+target.PrefabID());
            var body=target.GetComponent<KBatchedAnimController>();
            log("DUAL_HEALTH_BODY step="+step+" id="+id+" name="+info.nameLabel?.text+
                " nameVisible="+(info.nameLabel!=null && info.nameLabel.isActiveAndEnabled)+
                " visible="+body.IsVisible()+" enabled="+body.enabled+" tint="+ColorUtility.ToHtmlStringRGBA(body.TintColour)+
                " action="+body.currentAnim.HashValue+" index="+body.GetCurrentFrameIndex()+" pos="+body.transform.position);
            step++;frame=null;stable=0;
            return step>=3;
        }

        static Health SpawnCritter()
        {
            var camera=CameraController.Instance.transform.position;
            int near=Grid.PosToCell(camera);
            int cell=Enumerable.Range(0,Grid.CellCount).Where(c=>Grid.WorldIdx[c]==Grid.WorldIdx[near] &&
                Grid.Visible[c]>0 && !Grid.Solid[c] && Grid.Element[c].IsGas)
                .OrderBy(c=>Grid.GetCellDistance(near,c)).First();
            var body=Util.KInstantiate(Assets.GetPrefab(LightBugOrangeConfig.ID),Grid.CellToPosCBC(cell,Grid.SceneLayer.Creatures));
            body.SetActive(true);
            return body.GetComponent<Health>();
        }

        static Health PrintDupe()
        {
            int cell=Grid.PosToCell(Components.LiveMinionIdentities.Items[0]);
            Grid.CellToXY(cell,out int x,out int y);
            for(int dx=2;dx<10;dx++)
            {
                int test=Grid.XYToCell(x+dx,y);
                if(Grid.IsValidCell(test) && !Grid.Solid[test] && !Grid.Solid[Grid.CellAbove(test)] &&
                    Grid.Solid[Grid.CellBelow(test)] && Grid.Visible[test]!=0) {x+=dx;break;}
            }
            return NativePrintedDupe.Print(x,y).GetComponent<Health>();
        }
    }
}
