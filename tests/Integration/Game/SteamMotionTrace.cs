using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Queyang.Game;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Integration
{
    [DefaultExecutionOrder(32500)]
    internal sealed class SteamMotionTrace : MonoBehaviour
    {
        // ONI's embedded Mono implements Stopwatch with a process-local origin.
        // Win32 QPC uses the same hardware epoch in both Unity processes.
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long value);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long value);
        static long Counter()
        {
            if(!QueryPerformanceCounter(out long value)) throw new InvalidOperationException("Windows QPC unavailable");
            return value;
        }
        static long Frequency()
        {
            if(!QueryPerformanceFrequency(out long value) || value<=0) throw new InvalidOperationException("Windows QPC frequency unavailable");
            return value;
        }
        static readonly long frequency=Frequency();
        static SteamMotionTrace active;
        static readonly System.Reflection.FieldInfo ticks=AccessTools.Field(typeof(NativeDupePose),"ticks");
        int sampledFrame;
        long sampledQpc;
        [HarmonyPatch(typeof(NativeDupePose),"Capture")]
        static class Captured
        {
            static void Postfix(KBatchedAnimController anim)
            {
                if(active==null || !active.host || !active.recording || anim==null) return;
                if(hit!=null && hostWork?.workable!=null)
                {
                    int cell=Grid.PosToCell(hostWork.workable);
                    if(anim.gameObject==hit) active.Effect(anim,"contact_sample",cell,Time.frameCount);
                    else
                    {
                        foreach(var t in trackers)
                            if(t!=null && t.useTargetPoint && t.gameObject==anim.gameObject)
                            { active.Effect(anim,"beam_sample",cell,Time.frameCount); break; }
                    }
                }
                if(anim.GetComponent<KPrefabID>()?.InstanceID!=active.id) return;
                active.sampledFrame=Time.frameCount;
                active.sampledQpc=Counter();
            }
        }
        static readonly System.Type displayType=typeof(CoopSession).Assembly.GetType("Queyang.Game.NativeDigVisuals");
        static readonly System.Reflection.MethodInfo displayed=displayType==null?null:AccessTools.Method(displayType,"Get");
        static MultitoolController.Instance hostWork;
        static GameObject hit;
        static KBatchedAnimTracker[] trackers;
        [HarmonyPatch(typeof(MultitoolController.Instance),"CreateHitEffect")]
        static class WorkStarted
        {
            static void Postfix(MultitoolController.Instance __instance,GameObject ___hitEffect)
            {
                var w=__instance.workable?.worker;
                if(active==null || !active.host || !(__instance.workable is Diggable) || w==null ||
                    w.GetComponent<KPrefabID>().InstanceID!=active.id) return;
                hostWork=__instance; hit=___hitEffect; trackers=w.GetComponentsInChildren<KBatchedAnimTracker>(true);
            }
        }
        readonly List<string> fxRows=new List<string>{"qpc,tick,cell,kind,file,anim,frame,root_x,root_y,m00,m01,m02,m10,m11,m12"};
        int sparks,beams;
        void MeasureEffects(int tick)
        {
            KBatchedAnimController contact=null,beam=null; int cell=-1;
            if(host && hit!=null && hostWork?.workable!=null)
            {
                cell=Grid.PosToCell(hostWork.workable);
                contact=hit.GetComponent<KBatchedAnimController>();
                foreach(var t in trackers)
                {
                    if(t==null || !t.useTargetPoint || !t.gameObject.activeInHierarchy) continue;
                    var controller=t.GetComponent<KBatchedAnimController>();
                    if(controller!=null && controller.enabled && controller.IsVisible()) { beam=controller; break; }
                }
            }
            else if(!host && displayed!=null)
            {
                var current=displayed.Invoke(null,new object[]{id});
                if(current!=null)
                {
                    var type=current.GetType();
                    cell=(int)AccessTools.Field(type,"Cell").GetValue(current);
                    contact=(KBatchedAnimController)AccessTools.Field(type,"Contact").GetValue(current);
                    beam=(KBatchedAnimController)AccessTools.Field(type,"Beam").GetValue(current);
                }
            }
            if(Effect(contact,"contact",cell,tick)) sparks++;
            if(Effect(beam,"beam",cell,tick)) beams++;
        }
        bool Effect(KBatchedAnimController a,string kind,int cell,int tick)
        {
            if(a==null || !a.enabled || !a.IsVisible() || !a.gameObject.activeInHierarchy || a.GetBatchInstanceData()==null) return false;
            var p=SteamDupePoseChecks.Read(a);
            fxRows.Add(string.Format(culture,"{0},{1},{2},{3},{4},{5},{6},{7:R},{8:R},{9:R},{10:R},{11:R},{12:R},{13:R},{14:R}",
                Counter(),tick,cell,kind,a.GetCurrentAnim()?.animFile?.hashName.HashValue,a.currentAnim.HashValue,p[8],
                p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7]));
            return true;
        }
        readonly List<string> rows=new List<string>();
        readonly static CultureInfo culture=CultureInfo.InvariantCulture;
        int id;
        bool host, recording;
        float since=-1;
        string path;
        KSelectable target;
        internal static SteamMotionTrace Create(string folder,int id,bool host)
        {
            var trace=new GameObject("Co-op motion observer").AddComponent<SteamMotionTrace>();
            active=trace; trace.id=id; trace.host=host; trace.path=Path.Combine(folder,"dupe-motion.csv");
            trace.rows.Add("qpc,qpc_hz,time,unity_frame,id,pose_tick,root_x,root_y,render_x,render_y,animation,asset,gpu_frame,in_view,world_running,sample_qpc,asset_first,asset_frames,play_mode,play_speed");
            return trace;
        }
        internal float Seconds => since<0?0:Time.realtimeSinceStartup-since;
        internal void Begin() { recording=true; }
        void LateUpdate()
        {
            if(target==null)
            {
                if(host) target=KPrefabIDTracker.Get().GetInstance(id)?.GetComponent<KSelectable>();
                else target=NativeWorldMirror.GetDupeSelectable(id);
            }
            if(target==null) return;
            var a=target.GetComponent<KBatchedAnimController>();
            if(a==null || a.GetBatchInstanceData()==null) return;
            var camera=CameraController.Instance;
            var root=target.transform.position; root.z=camera.transform.position.z;
            camera.SetTargetPos(root,camera.OrthographicSize,false);
            if(!recording) return;
            bool running=host ? !SpeedControlScreen.Instance.IsPaused : CoopSession.NativeView?.Paused==false;
            if(since<0)
            {
                if(!running) return;
                since=Time.realtimeSinceStartup;
            }
            var p=SteamDupePoseChecks.Read(a);
            var v=camera.baseCamera.WorldToViewportPoint(target.transform.position);
            int poseTick=sampledFrame;
            if(!host) ((Dictionary<int,int>)ticks.GetValue(null)).TryGetValue(id,out poseTick);
            MeasureEffects(host?Time.frameCount:poseTick);
            rows.Add(string.Format(culture,"{0},{1},{2:R},{3},{4},{5},{6:R},{7:R},{8:R},{9:R},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19:R}",
                Counter(),frequency,Time.realtimeSinceStartup,Time.frameCount,id,poseTick,
                p[0],p[1],p[4],p[7],a.currentAnim.HashValue,a.GetCurrentAnim()?.animFile?.hashName.HashValue,p[8],
                a.IsVisible() && v.z>0 && v.x>0 && v.x<1 && v.y>0 && v.y<1?1:0,running?1:0,sampledQpc,a.GetFirstFrameIndex(),a.GetCurrentNumFrames(),(int)a.PlayMode,a.GetPlaySpeed()));
        }
        internal void End(Action<string> log,int x,int y)
        {
            if(!recording) return;
            recording=false; active=null;
            if(Seconds<30 || rows.Count<31) throw new InvalidDataException("Insufficient rendered motion trace");
            File.WriteAllLines(path,rows);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(path),"dig-fx.csv"),fxRows);
            log("DUAL_DIG_FX id="+id+" contactFrames="+sparks+" beamFrames="+beams);
            if(displayed!=null && sparks==0) throw new InvalidDataException("No actual digging contact animation was rendered");
            log("DUAL_MOTION_END id="+id+" seconds="+Seconds.ToString("F2",culture)+" samples="+(rows.Count-1)+" file=dupe-motion.csv");
            var camera=CameraController.Instance;
            camera.SetTargetPos(new Vector3(x+.5f,y+.5f,camera.transform.position.z),camera.OrthographicSize,false);
            enabled=false;
            Destroy(gameObject);
        }
    }
}
