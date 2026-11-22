using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    // The reliable viewport owns building and meter lifetimes. Short-lived
    // samples can only animate a body or meter that already exists locally.
    internal static class NativeBuildingAnimation
    {
        internal const int Header=-4;
        const int HeaderSize=12,RowSize=31,RowsPerPacket=34;
        sealed class Track
        {
            internal int Tick,Animation,File;
            internal float Arrival,Speed,OriginalSpeed;
            internal MotionPhase Phase;
        }
        static readonly Dictionary<KBatchedAnimController,Track> tracks=new Dictionary<KBatchedAnimController,Track>();
        static readonly List<KBatchedAnimController> expired=new List<KBatchedAnimController>();
        sealed class RecentBody
        {
            internal int Action,Asset,Frame=-1;
            internal float Changed;
        }
        static ConditionalWeakTable<KBatchedAnimController,RecentBody> bodySamples=new ConditionalWeakTable<KBatchedAnimController,RecentBody>();
        static readonly AccessTools.FieldRef<KBatchedAnimController,bool> rebuild=
            AccessTools.FieldRefAccess<KBatchedAnimController,bool>("_forceRebuild");
        internal static int StreamFrame(KBatchedAnimController animator)=>NativeDupePose.RenderFrame(animator);

        internal static IEnumerable<byte[]> Capture(WorldContainer world,int left,int bottom,bool paused)
        {
            using(var stream=new MemoryStream())
            using(var writer=new BinaryWriter(stream))
            {
                writer.Write(Header);writer.Write(Time.frameCount);writer.Write(Time.unscaledTime);
                foreach(var building in Components.BuildingCompletes.Items)
                {
                    if(building==null) continue;
                    int cell=building.GetCell();
                    if(!Grid.IsValidCell(cell) || Grid.Visible[cell]==0 || Grid.WorldIdx[cell]!=world.id) continue;
                    Grid.CellToXY(cell,out int x,out int y);
                    if(left>=0 && (x<left-4 || x>=left+ColonyView.Width+4 || y<bottom-4 || y>=bottom+ColonyView.Height+4)) continue;
                    int id=building.GetComponent<KPrefabID>().InstanceID;
                    var body=building.GetComponent<KBatchedAnimController>();
                    if(Write(writer,body,id,cell,0,paused) && stream.Length==HeaderSize+RowSize*RowsPerPacket)
                    {
                        yield return stream.ToArray();stream.SetLength(HeaderSize);stream.Position=HeaderSize;
                    }
                    foreach(var part in NativeBuildingMeters.MovingParts(building.gameObject))
                        if(Write(writer,part.Value,id,cell,part.Key,paused) && stream.Length==HeaderSize+RowSize*RowsPerPacket)
                        {
                            yield return stream.ToArray();stream.SetLength(HeaderSize);stream.Position=HeaderSize;
                        }
                }
                if(stream.Length>HeaderSize) yield return stream.ToArray();
            }
        }

        static bool Write(BinaryWriter w,KBatchedAnimController anim,int id,int cell,ushort part,bool paused)
        {
            var clip=anim?.GetCurrentAnim();
            if(clip?.animFile==null || clip.numFrames<1 || clip.numFrames>65536 || id<=0 || anim.currentAnim.HashValue==0) return false;
            int frame=StreamFrame(anim);
            if(frame<0) return false;
            float rate=paused || anim.IsStopped() || anim.PlayMode==KAnim.PlayMode.Paused?0:
                Mathf.Clamp(anim.GetPlaySpeed()*clip.frameRate*Time.timeScale,-600,600);
            int action=anim.currentAnim.HashValue,asset=clip.animFile.hashName.HashValue;
            if(part==0)
            {
                var previous=bodySamples.GetOrCreateValue(anim);
                float now=Time.unscaledTime;
                if(previous.Action!=action || previous.Asset!=asset || previous.Frame!=frame)
                {
                    previous.Action=action;previous.Asset=asset;previous.Frame=frame;previous.Changed=now;
                }
                // Static art is already present in the reliable tile. Repeat clip
                // transitions briefly so a lost datagram cannot leave the old clip on.
                if((rate==0 || clip.numFrames==1) && now-previous.Changed>.5f) return false;
            }
            w.Write(cell);w.Write(id);w.Write(part);w.Write(action);w.Write(asset);
            w.Write(frame);w.Write(clip.numFrames);w.Write(rate);w.Write((byte)(anim.PlayMode==KAnim.PlayMode.Loop?1:0));
            return true;
        }

        internal static void Apply(byte[] packet)
        {
            int length=packet.Length-HeaderSize;
            if(length<=0 || length%RowSize!=0 || length>RowsPerPacket*RowSize)
                throw new InvalidDataException("Invalid building animation packet");
            using(var r=new BinaryReader(new MemoryStream(packet,false)))
            {
                r.ReadInt32();int tick=r.ReadInt32();float sourceTime=r.ReadSingle();
                if(tick<=0 || !(sourceTime>=0 && sourceTime<float.MaxValue)) throw new InvalidDataException("Invalid animation time");
                while(r.BaseStream.Position<packet.Length)
                {
                    int cell=r.ReadInt32(),id=r.ReadInt32();ushort part=r.ReadUInt16();
                    int action=r.ReadInt32(),asset=r.ReadInt32(),frame=r.ReadInt32(),count=r.ReadInt32();
                    float rate=r.ReadSingle();byte flags=r.ReadByte();
                    if(!Grid.IsValidCell(cell) || id<=0 || part>256 || action==0 || count<=0 || count>65536 ||
                        frame<0 || frame>=count || !(rate>=-600 && rate<=600) || flags>1)
                        throw new InvalidDataException("Invalid building animation sample");
                    var target=NativeWorldMirror.GetBuildingSelectable(cell,id);
                    if(target==null) continue;
                    var anim=part==0?target.GetComponent<KBatchedAnimController>():NativeBuildingMeters.GetPart(target,part);
                    if(anim==null) continue;
                    float now=Time.unscaledTime;
                    if(!tracks.TryGetValue(anim,out var t))
                        tracks.Add(anim,t=new Track{OriginalSpeed=anim.PlaySpeedMultiplier});
                    if(tick<=t.Tick) continue;
                    float interval=now-t.Arrival;
                    bool same=t.Animation==action && t.File==asset && interval<.45f;
                    t.Arrival=now;t.Tick=tick;t.Animation=action;t.File=asset;t.Speed=rate;
                    t.Phase.Anchor(frame,count,rate,flags==1,sourceTime,interval,same);
                    Draw(anim,t,now);
                }
            }
        }

        internal static bool Owns(KBatchedAnimController anim,int tick)
        {
            return anim!=null && tracks.TryGetValue(anim,out var track) &&
                (tick<=track.Tick || Time.unscaledTime-track.Arrival<.25f);
        }

        internal static void Snapshot(KBatchedAnimController anim,int action,byte position,int tick)
        {
            if(anim==null || action==0 || Owns(anim,tick)) return;
            var name=new HashedString(action);
            if(!anim.HasAnimation(name)) return;
            if(anim.currentAnim!=name) anim.Play(name);
            var clip=anim.GetCurrentAnim();
            if(clip==null || clip.numFrames<1) return;
            int frame=Mathf.Clamp(Mathf.RoundToInt(position*clip.numFrames/255f),0,clip.numFrames-1);
            SetFrame(anim,frame);
        }

        static void Draw(KBatchedAnimController anim,Track track,float now)
        {
            var name=new HashedString(track.Animation);
            if(anim.currentAnim!=name || anim.GetCurrentAnim()?.animFile?.hashName.HashValue!=track.File)
                NativeDupePose.PlayAsset(anim,name,track.File);
            var clip=anim.GetCurrentAnim();
            int frame=track.Phase.Frame(now-track.Arrival);
            if(clip==null || anim.currentAnim!=name || frame<0 || frame>=clip.numFrames) return;
            anim.PlaySpeedMultiplier=0f;
            SetFrame(anim,frame);
        }

        internal static void SetFrame(KBatchedAnimController anim,int frame)
        {
            if(StreamFrame(anim)==frame) return;
            anim.SetPositionPercent((float)frame/anim.GetCurrentNumFrames());
            rebuild(anim)=true;
            anim.UpdateAnim(0f);
        }

        internal static void Draw(float now)
        {
            expired.Clear();
            foreach(var pair in tracks)
            {
                if(pair.Key==null || now-pair.Value.Arrival>.55f) expired.Add(pair.Key);
                else if(pair.Value.Speed!=0) Draw(pair.Key,pair.Value,now);
            }
            foreach(var anim in expired)
            {
                if(anim!=null) anim.PlaySpeedMultiplier=tracks[anim].OriginalSpeed;
                tracks.Remove(anim);
            }
        }

        internal static void Reset()
        {
            foreach(var pair in tracks)
                if(pair.Key!=null) pair.Key.PlaySpeedMultiplier=pair.Value.OriginalSpeed;
            tracks.Clear();expired.Clear();
            bodySamples=new ConditionalWeakTable<KBatchedAnimController,RecentBody>();
        }
    }
}
