using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamCritterMotion
    {
        internal static bool Enabled=>Environment.GetEnvironmentVariable("QY_CHECK_CRITTERS")=="1";
        static GameObject animal;
        internal static int HostId=>animal.GetComponent<KPrefabID>().InstanceID;
        internal static bool Prepare(int near,Action<string> log)
        {
            if (!Enabled) return true;
            if (animal!=null) return animal.GetComponent<KPrefabID>().isSpawned;
            var start=Grid.CellToPosCBC(near,Grid.SceneLayer.Creatures);
            var natural=Components.Brains.Items.OfType<CreatureBrain>().FirstOrDefault(b=>b!=null &&
                b.PrefabID()==LightBugConfig.ID && Vector2.Distance(start,b.transform.position)<6);
            if (natural!=null) animal=natural.gameObject;
            else
            {
                if (Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Creature fixture requires the isolated colony");
                int cell=Enumerable.Range(0,Grid.CellCount).Where(c=>Grid.WorldIdx[c]==Grid.WorldIdx[near] && Grid.Visible[c]>0 &&
                    !Grid.Solid[c] && Grid.Element[c].IsGas).OrderBy(c=>Grid.GetCellDistance(near,c)).First();
                animal=Util.KInstantiate(Assets.GetPrefab(LightBugConfig.ID),Grid.CellToPosCBC(cell,Grid.SceneLayer.Creatures));
                animal.SetActive(true);
            }
            log("DUAL_CRITTER_SETUP naturalExisting="+(natural!=null)+" prefab="+animal.PrefabID()+" movement=ONI");
            return false;
        }

        static SteamCritterMotion active;
        readonly int id;
        readonly bool host;
        readonly string dir;
        readonly List<string> rows=new List<string>{"qpc,qpc_hz,time,host_tick,id,source_x,source_y,render_x,render_y,gpu_x,gpu_y,gpu_frame,viewport_x,viewport_y,visible,host_moving,animated_between_packets"};
        int tick,previousTick=-1, movingFrames, intermediate, deliveries, movingSamples, rendered;
        Vector2 source,previous;
        float lastMoveAt=-100,stillSince=-1,maxStill,lastTime;
        FixtureFrame photo;
        bool photoSaved,hasPrevious,hasSource;
        static readonly CultureInfo culture=CultureInfo.InvariantCulture;
        internal SteamCritterMotion(int id,bool host,string dir)
        { this.id=id;this.host=host;this.dir=dir;active=this; }

        [HarmonyPatch(typeof(NativeDupePose),"Capture")]
        static class HostPose
        {
            static void Postfix(KBatchedAnimController anim,Vector3 root)
            {
                if (active==null || !active.host || anim?.gameObject!=animal || SpeedControlScreen.Instance.IsPaused) return;
                active.Sample(Time.frameCount,root);
            }
        }
        [HarmonyPatch(typeof(NativeDupePose),"Apply")]
        static class GuestPose
        {
            static void Postfix(ColonyDupe dupe,int tick,bool paused,bool fast)
            {
                if (active==null || active.host || !fast || paused || dupe.SaveId!=active.id) return;
                active.Sample(tick,new Vector2(dupe.Pose.X,dupe.Pose.Y));
            }
        }
        void Sample(int newTick,Vector2 position)
        {
            if (newTick<=tick) return;
            if (hasSource && (position-source).sqrMagnitude>.000004f)
            { movingSamples++;lastMoveAt=Time.realtimeSinceStartup; }
            tick=newTick;source=position;deliveries++;hasSource=true;
        }
        internal void Frame(long qpc,long frequency,bool running)
        {
            var a=host ? animal?.GetComponent<KBatchedAnimController>() : NativeEntityDisplay.GetSelectable(id)?.GetComponent<KBatchedAnimController>();
            if (a==null || a.GetBatchInstanceData()==null) return;
            var p=SteamDupePoseChecks.Read(a);
            Vector2 xy=new Vector2(p[4],p[7]);
            var vp=CameraController.Instance.baseCamera.WorldToViewportPoint(a.transform.position);
            bool visible=a.IsVisible() && vp.z>0 && vp.x>0 && vp.y>0 && vp.x<1 && vp.y<1;
            float now=Time.realtimeSinceStartup;
            bool mobile=running && visible && now-lastMoveAt<.20f;
            bool changed=hasPrevious && (xy-previous).sqrMagnitude>.000001f;
            bool interpolated=mobile && changed && tick==previousTick;
            if (mobile && changed)
            {
                movingFrames++;if(interpolated) intermediate++;
                if (photo==null)
                {
                    photo=new FixtureFrame(Path.Combine(dir,"critter-"+(host?"host":"guest")+".png"));
                    photo.Capture();
                }
            }
            if (mobile && !changed)
            {
                if(stillSince<0) stillSince=lastTime;
                maxStill=Math.Max(maxStill,now-stillSince);
            }
            else stillSince=-1;
            if (photo!=null && !photoSaved) photoSaved=photo.Complete;
            if (visible && running) rendered++;
            rows.Add(string.Format(culture,"{0},{1},{2:R},{3},{4},{5:R},{6:R},{7:R},{8:R},{9:R},{10:R},{11:R},{12:R},{13:R},{14},{15},{16}",
                qpc,frequency,now,tick,id,source.x,source.y,p[0],p[1],xy.x,xy.y,p[8],vp.x,vp.y,visible?1:0,mobile?1:0,interpolated?1:0));
            previous=xy;previousTick=tick;lastTime=now;hasPrevious=true;
        }
        internal void End(Action<string> log)
        {
            active=null;
            File.WriteAllLines(Path.Combine(dir,"critter-motion.csv"),rows);
            log($"DUAL_CRITTER_MOTION id={id} role={(host?"host":"guest")} sourceTicks={deliveries} movingSourceTicks={movingSamples} framesInView={rendered} movingRenderFrames={movingFrames} betweenPackets={intermediate} maxMovingHoldMs={maxStill*1000:F0} screenshot={photoSaved}");
            if (deliveries<15 || movingSamples<5 || movingFrames<5 || !photoSaved) throw new InvalidDataException("Insufficient native critter motion evidence");
        }
    }
}
