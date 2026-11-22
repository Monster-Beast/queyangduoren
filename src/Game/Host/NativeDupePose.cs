using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    // KAnim writes a world-space affine matrix into its instance texture. Navigation
    // transitions and working offsets make that matrix different from the minion root.
    [DefaultExecutionOrder(32000)]
    internal sealed class NativeDupePose : MonoBehaviour
    {
        sealed class Track
        {
            internal KBatchedAnimController Anim;
            internal ColonyDupe Dupe;
            internal Vector2 Start, MatrixStart, Velocity;
            internal float Rate, LastFast;
            internal bool Loop, Fast;
            internal MotionPhase Phase;
            internal float Since, Duration, OriginalSpeed;
            internal KAnimFile Added;
        }
        static readonly Dictionary<int,Track> tracks=new Dictionary<int,Track>();
        static readonly Dictionary<int,int> ticks=new Dictionary<int,int>();
        static readonly AccessTools.FieldRef<KBatchedAnimInstanceData,bool> Overridden=
            AccessTools.FieldRefAccess<KBatchedAnimInstanceData,bool>("isTransformOverriden");
        static readonly FieldInfo Converter=AccessTools.Field(typeof(KBatchedAnimInstanceData),"converter");
        static readonly FieldInfo Bytes=AccessTools.Field(Converter.FieldType,"bytes");
        static readonly Type Entry=AccessTools.Inner(typeof(KBatchedAnimInstanceData),"AnimInstanceData");
        static int MatrixOffset(string component) => (int)Marshal.OffsetOf(Entry,"transformMatrix")+
            (int)Marshal.OffsetOf(typeof(Matrix2x3),component);
        static readonly int M00=MatrixOffset("m00"), M01=MatrixOffset("m01"), M02=MatrixOffset("m02");
        static readonly int M10=MatrixOffset("m10"), M11=MatrixOffset("m11"), M12=MatrixOffset("m12");

        internal static Matrix2x3 RenderMatrix(KBatchedAnimController anim)
        {
            var data=anim.GetBatchInstanceData();
            if(data!=null && Overridden(data))
            {
                // The native union allocates byte[112] and aliases a struct[] over it.
                // Reflection returns the physical byte[] for both overlapping fields.
                var bytes=(byte[])Bytes.GetValue(Converter.GetValue(data));
                return new Matrix2x3 {
                    m00=BitConverter.ToSingle(bytes,M00), m01=BitConverter.ToSingle(bytes,M01), m02=BitConverter.ToSingle(bytes,M02),
                    m10=BitConverter.ToSingle(bytes,M10), m11=BitConverter.ToSingle(bytes,M11), m12=BitConverter.ToSingle(bytes,M12)
                };
            }
            return anim.GetTransformMatrix();
        }
        internal static ColonyDupePose Capture(KBatchedAnimController anim,Vector3 root)
        {
            var m=anim==null?Matrix2x3.Translate(root):RenderMatrix(anim);
            return new ColonyDupePose { X=root.x,Y=root.y,M00=m.m00,M01=m.m01,M02=m.m02,M10=m.m10,M11=m.m11,M12=m.m12,
                File=anim?.GetCurrentAnim()?.animFile?.hashName.HashValue ?? 0,Frame=RenderFrame(anim) };
        }
        // StartQueuedAnim briefly stores a *global* batch index in currentFrame,
        // whereas UpdateFrame normally stores a relative index in that property.
        // The texture consistently takes GetCurrentFrameIndex() in batch space.
        static int RenderFrame(KBatchedAnimController anim)
        {
            if(anim==null) return -1;
            int relative=anim.GetCurrentFrameIndex()-anim.GetFirstFrameIndex();
            return relative>=0 && relative<anim.GetCurrentNumFrames() ? relative : -1;
        }

        internal static bool Current(int id,int tick) => tick==0 || !ticks.TryGetValue(id,out int last) || tick>=last;
        internal static bool Knows(int id) => tracks.ContainsKey(id);
        internal static Vector2 LastPosition(int id,Vector3 fallback) => tracks.TryGetValue(id,out var track)
            ? new Vector2(track.Dupe.X,track.Dupe.Y) : (Vector2)fallback;

        internal static void Apply(KBatchedAnimController anim,ColonyDupe dupe,int tick,bool paused,bool fast=false,float rate=0,bool loop=false,int frames=0,float sourceTime=-1)
        {
            int id=dupe.SaveId;
            if(!tracks.TryGetValue(id,out var track) || track.Anim!=anim)
            {
                Forget(id,tick);
                track=new Track { Anim=anim, OriginalSpeed=anim.PlaySpeedMultiplier }; tracks[id]=track;
            }
            float now=Time.unscaledTime;
            if(track.Since>0 && track.Dupe.SaveId==id &&
                ticks.TryGetValue(id,out int previous) && tick==previous && (!fast || track.Fast)) return;
            var p=dupe.Pose;
            var root=anim.transform.position;
            var m=RenderMatrix(anim);
            float interval=now-track.Since;
            bool nearby=!paused && track.Since>0 && interval<.5f &&
                Vector2.Distance(root,new Vector2(p.X,p.Y))<2.5f;
            // Do not cut diagonally across the solid corner of a turn.
            var delta=new Vector2(p.X-track.Dupe.Pose.X,p.Y-track.Dupe.Pose.Y);
            if(nearby && Mathf.Abs(delta.x)>.01f && Mathf.Abs(delta.y)>.01f)
            {
                int cornerA=Grid.PosToCell(new Vector2(p.X,root.y));
                int cornerB=Grid.PosToCell(new Vector2(root.x,p.Y));
                nearby=Grid.IsValidCell(cornerA) && Grid.IsValidCell(cornerB) && !Grid.Solid[cornerA] && !Grid.Solid[cornerB];
            }
            if(fast) track.LastFast=now;
            bool recentFast=track.LastFast>0 && now-track.LastFast<.15f;
            bool sameAnim=track.Dupe.Animation==dupe.Animation && track.Dupe.Pose.File==p.File;
            track.Rate=paused?0:fast?rate:recentFast && sameAnim?track.Rate:0;
            track.Loop=fast?loop:sameAnim && track.Loop;
            track.Fast=fast;
            if(!fast && anim.currentAnim.HashValue==dupe.Animation) frames=anim.GetCurrentNumFrames();
            track.Phase.Anchor(p.Frame,frames,track.Rate,track.Loop,sourceTime,interval,
                !paused && sameAnim && interval<.5f);
            track.Velocity=nearby && recentFast && interval>.015f ? Vector2.ClampMagnitude(delta/interval,16f) : Vector2.zero;
            track.Start=root; track.MatrixStart=new Vector2(m.m02,m.m12);
            // Blend from the image already on screen. No queued snapshots or fixed playback delay.
            track.Duration=nearby? Mathf.Min(interval*.65f,recentFast?.035f:.18f):0;
            track.Since=now; track.Dupe=dupe; ticks[id]=tick;
            Draw(track,now,true);
        }
        static void Draw(Track t,float now,bool newPose)
        {
            var anim=t.Anim;
            if(anim==null) return;
            var d=t.Dupe; var p=d.Pose;
            var f=t.Duration>0?Mathf.Clamp01((now-t.Since)/t.Duration):1f;
            float lead=Mathf.Clamp(now-t.Since-t.Duration,0,.045f);
            var advance=Vector2.ClampMagnitude(t.Velocity*lead,.25f);
            // Predict only inside the last confirmed grid cell. A ladder/wall transition,
            // teleport or a change of direction must first be seen at the authority.
            advance.x=Mathf.Clamp(p.X+advance.x,d.X,d.X+.99999f)-p.X;
            advance.y=Mathf.Clamp(p.Y+advance.y,d.Y-.05f,d.Y+.94999f)-p.Y;
            var root=anim.transform.position;
            var next=new Vector3(Mathf.Lerp(t.Start.x,p.X,f)+advance.x,Mathf.Lerp(t.Start.y,p.Y,f)+advance.y,root.z);
            bool moved=root!=next;
            if(moved) anim.transform.position=next;
            if(anim.FlipX!=d.FlipX) anim.FlipX=d.FlipX;
            int desired=t.Phase.Frame(now-t.Since);
            if(newPose || anim.currentAnim.HashValue!=d.Animation || RenderFrame(anim)!=desired)
            {
                var name=new HashedString(d.Animation);
                if(t.Added!=null && new HashedString(t.Added.name).HashValue!=p.File)
                { anim.RemoveAnimOverrides(t.Added); t.Added=null; }
                if(p.File!=0 && anim.GetCurrentAnim()?.animFile?.hashName.HashValue!=p.File && t.Added==null &&
                    Assets.TryGetAnim(new HashedString(p.File),out var file) && !Contains(anim,file))
                { anim.AddAnimOverrides(file,10000f); t.Added=file; }
                if(d.Animation!=0 && anim.HasAnimation(name))
                {
                    if(anim.currentAnim!=name || anim.GetCurrentAnim()?.animFile?.hashName.HashValue!=p.File) anim.Play(name);
                    var current=anim.GetCurrentAnim();
                    if(current!=null && current.numFrames>0 && desired>=0 && desired<current.numFrames)
                    {
                        // ONI rounds elapsed time to the nearest frame.
                        anim.PlaySpeedMultiplier=0;
                        anim.SetPositionPercent((float)desired/current.numFrames);
                        anim.UpdateAnim(0f);
                    }
                }
            }
            var data=anim.GetBatchInstanceData();
            if(data==null) return;
            data.SetOverrideTransformMatrix(new Matrix2x3 { m00=p.M00, m01=p.M01, m02=Mathf.Lerp(t.MatrixStart.x,p.M02,f)+advance.x,
                m10=p.M10, m11=p.M11, m12=Mathf.Lerp(t.MatrixStart.y,p.M12,f)+advance.y });
            if(newPose || moved || f<1 || t.Duration>0) anim.SetDirty();

        }
        static bool Contains(KBatchedAnimController anim,KAnimFile file)
        {
            if(anim.AnimFiles!=null) foreach(var asset in anim.AnimFiles) if(asset==file) return true;
            if(anim.OverrideAnimFiles!=null) foreach(var asset in anim.OverrideAnimFiles) if(asset.file==file) return true;
            return false;
        }
        void LateUpdate()
        {
            if(!NativeGuestScene.Ready) return;
            float now=Time.unscaledTime;
            foreach(var t in tracks.Values) Draw(t,now,false);
            NativeDigVisuals.Expire(now);
        }
        internal static void Forget(int id,int tick)
        {
            if(!Current(id,tick)) return;
            if(tick!=0) ticks[id]=tick;
            if(!tracks.TryGetValue(id,out var t)) return;
            tracks.Remove(id); NativeDigVisuals.Clear(id);
            if(t.Anim==null) return;
            t.Anim.PlaySpeedMultiplier=t.OriginalSpeed;
            t.Anim.GetBatchInstanceData()?.ClearOverrideTransformMatrix();
            if(t.Added!=null) t.Anim.RemoveAnimOverrides(t.Added);
        }
        internal static void ClearTracks()
        {
            foreach(int id in new List<int>(tracks.Keys)) Forget(id,0);
            ticks.Clear(); NativeDigVisuals.Reset();
        }
    }
}
