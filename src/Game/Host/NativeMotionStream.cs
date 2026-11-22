using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Queyang.Game
{
    // Ephemeral, single-datagram actor samples. The reliable viewport owns identities,
    // visibility and deletion. These messages can only move an already projected actor.
    internal static class NativeMotionStream
    {
        const int WithTools=-2, PerPacket=4, MinRowBytes=59;
        internal static IEnumerable<byte[]> Capture(int left,int bottom)
        {
            bool paused=SpeedControlScreen.Instance.IsPaused;
            var world=ClusterManager.Instance?.activeWorld;
            if(world==null) yield break;
            foreach (var packet in NativeFallingOre.Capture(world,left,bottom)) yield return packet;
            int tick=Time.frameCount;
            using(var stream=new MemoryStream())
            using(var w=new BinaryWriter(stream))
            {
                w.Write(WithTools); w.Write(tick); w.Write(paused); w.Write(Time.unscaledTime);
                int count=0;
                foreach(var dupe in Components.LiveMinionIdentities.Items)
                {
                    if(dupe==null) continue;
                    var root=dupe.transform.position;
                    // Include nearby peripheral tiles while keeping distant colonies independent.
                    if(left>=0 && (root.x<left-48 || root.x>left+ColonyView.Width+48 ||
                        root.y<bottom-32 || root.y>bottom+ColonyView.Height+32)) continue;
                    int cell=Grid.PosToCell(root);
                    if(!Grid.IsValidCell(cell) || Grid.Visible[cell]==0 || Grid.WorldIdx[cell]!=world.id) continue;
                    var a=dupe.GetComponent<KBatchedAnimController>();
                    if(a==null) continue;
                    var pose=NativeDupePose.Capture(a,root);
                    var animation=a.GetCurrentAnim();
                    float rate=paused || a.IsStopped() || a.PlayMode==KAnim.PlayMode.Paused ||
                        animation==null ? 0 : Mathf.Clamp(a.GetPlaySpeed()*animation.frameRate*Time.timeScale,-600,600);
                    w.Write(dupe.GetComponent<KPrefabID>().InstanceID); w.Write(a.currentAnim.HashValue);
                    w.Write(a.FlipX); w.Write(pose.X); w.Write(pose.Y);
                    w.Write(pose.M00); w.Write(pose.M01); w.Write(pose.M02);
                    w.Write(pose.M10); w.Write(pose.M11); w.Write(pose.M12);
                    w.Write(pose.File); w.Write(pose.Frame); w.Write(a.GetCurrentNumFrames()); w.Write(rate); w.Write(a.PlayMode==KAnim.PlayMode.Loop);
                    NativeDigVisuals.Write(w,a);
                    NativeToolSymbols.Write(w,a);
                    if(++count<PerPacket) continue;
                    yield return stream.ToArray();
                    count=0; stream.SetLength(13); stream.Position=13;
                }
                if(count>0) yield return stream.ToArray();
            }
        }
        internal static void Apply(byte[] packet)
        {
            if (NativeFallingOre.IsPacket(packet)) { NativeFallingOre.Apply(packet); return; }
            if(packet.Length<9+MinRowBytes || packet.Length>1100)
                throw new InvalidDataException("Invalid character motion payload");
            using(var r=new BinaryReader(new MemoryStream(packet,false)))
            {
                int tick=r.ReadInt32();
                bool tools=tick==WithTools;
                if(tools) tick=r.ReadInt32();
                bool paused=r.ReadBoolean(); float sourceTime=r.ReadSingle();
                if(tick<1 || !(sourceTime>=0 && sourceTime<float.MaxValue)) throw new InvalidDataException("Invalid character motion time");
                while(r.BaseStream.Position<packet.Length)
                {
                    var d=new ColonyDupe { SaveId=r.ReadInt32(), Animation=r.ReadInt32(), FlipX=r.ReadBoolean() };
                    d.Pose=new ColonyDupePose { X=r.ReadSingle(),Y=r.ReadSingle(),M00=r.ReadSingle(),M01=r.ReadSingle(),M02=r.ReadSingle(),
                        M10=r.ReadSingle(),M11=r.ReadSingle(),M12=r.ReadSingle(),File=r.ReadInt32(),Frame=r.ReadInt32() };
                    int frames=r.ReadInt32(); float rate=r.ReadSingle(); bool loop=r.ReadBoolean();
                    if(!(d.Pose.X>=0 && d.Pose.X<Grid.WidthInCells && d.Pose.Y>=-.05f && d.Pose.Y<Grid.HeightInCells) ||
                        !(rate>=-600 && rate<=600) || frames<0 || frames>65536 || d.Pose.Frame>=frames || d.SaveId<0) throw new InvalidDataException("Invalid character motion values");
                    Grid.CellToXY(Grid.PosToCell(new Vector2(d.Pose.X,d.Pose.Y)),out d.X,out d.Y);
                    ColonyView.ValidatePose(d);
                    var a=NativeDupePose.Knows(d.SaveId) && NativeDupePose.Current(d.SaveId,tick) ?
                        NativeWorldMirror.GetDupeSelectable(d.SaveId)?.GetComponent<KBatchedAnimController>() : null;
                    if(a!=null) NativeDupePose.Apply(a,d,tick,paused,true,rate,loop,frames,sourceTime);
                    NativeDigVisuals.Read(r,d.SaveId,a);
                    if(tools) NativeToolSymbols.Read(r,a);
                }
            }
        }
    }
}
