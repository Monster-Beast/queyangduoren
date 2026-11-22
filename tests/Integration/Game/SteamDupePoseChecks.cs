using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using Unity.Collections;
using UnityEngine;

namespace Queyang.Integration
{
    internal static class SteamDupePoseChecks
    {
        const string Witness="qy_dual_pose_witness", Ack="qy_dual_pose_ack";
        static bool observe, observeGuest, reported;
        static readonly Dictionary<int,Dictionary<int,float[]>> hostFrames=new Dictionary<int,Dictionary<int,float[]>>();
        static readonly Dictionary<int,int> guestTicks=new Dictionary<int,int>();
        static readonly string[] fields={"m00","m01","m02","m10","m11","m12"};

        internal static void StartHost() { observe=true; hostFrames.Clear(); }

        internal static void StartGuest() { observeGuest=true; guestTicks.Clear(); }

        // Exercise the engine's own serialization path for the instance texture, including
        // navigation/working overrides. Offsets are taken from the actual installed game.
        internal static float[] Read(KBatchedAnimController anim)
        {
            var entry=typeof(KBatchedAnimInstanceData).GetNestedType("AnimInstanceData",
                System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
            var data=new NativeArray<byte>(Marshal.SizeOf(entry),Allocator.Temp);
            try
            {
                anim.GetBatchInstanceData().WriteToTexture(data,0,0);
                byte[] bytes=data.ToArray();
                int matrix=(int)Marshal.OffsetOf(entry,"transformMatrix");
                var p=anim.transform.position;
                var result=new float[9]; result[0]=p.x; result[1]=p.y;
                for(int i=0;i<6;i++)
                    result[2+i]=BitConverter.ToSingle(bytes,matrix+(int)Marshal.OffsetOf(typeof(Matrix2x3),fields[i]));
                result[8]=BitConverter.ToSingle(bytes,(int)Marshal.OffsetOf(entry,"curAnimFrameIndex"));
                return result;
            }
            finally { data.Dispose(); }
        }

        [HarmonyPatch(typeof(HostWorld),"CapturePage")]
        static class HostSample
        {
            static void Postfix()
            {
                if(!observe) return;
                int tick=Time.frameCount;
                if(hostFrames.ContainsKey(tick)) return;
                if(hostFrames.Count>=256) hostFrames.Remove(hostFrames.Keys.Min());
                var rows=hostFrames[tick]=new Dictionary<int,float[]>();
                foreach(var d in Components.LiveMinionIdentities.Items)
                {
                    var a=d?.GetComponent<KBatchedAnimController>();
                    if(a!=null && a.IsVisible() && a.GetBatchInstanceData()!=null)
                        rows[d.GetComponent<KPrefabID>().InstanceID]=Read(a);
                }
            }
        }
        [HarmonyPatch(typeof(NativeWorldMirror),"Apply")]
        static class GuestSample
        {
            static void Postfix(ColonyView view)
            {
                if(!observeGuest || view==null || !view.Paused) return;
                foreach(var d in view.Dupes)
                    if(!guestTicks.TryGetValue(d.SaveId,out int last) || view.DupePoseTick>=last)
                        guestTicks[d.SaveId]=view.DupePoseTick;
            }
        }
        internal static bool Guest(CSteamID room,Action<string> log)
        {
            if(reported) return SteamMatchmaking.GetLobbyData(room,Ack)=="2";
            var camera=CameraController.Instance.baseCamera;
            var proxies=UnityEngine.Object.FindObjectsByType<NativeDupeIdentityMarker>(FindObjectsSortMode.None);
            var rows=new List<System.Tuple<int,int,float[]>>();
            foreach(var kv in guestTicks.OrderByDescending(kv=>kv.Value))
            {
                var a=NativeWorldMirror.GetDupeSelectable(kv.Key)?.GetComponent<KBatchedAnimController>();
                if(a==null || !a.IsVisible() || a.GetBatchInstanceData()==null) continue;
                var at=camera.WorldToViewportPoint(a.transform.position);
                if(at.z<=0 || at.x<=0 || at.x>=1 || at.y<=0 || at.y>=1) continue;
                int copies=Components.LiveMinionIdentities.Items.Count(d=>d!=null && d.GetComponent<KPrefabID>().InstanceID==kv.Key &&
                    d.GetComponent<KBatchedAnimController>().IsVisible())+
                    proxies.Count(p=>p.HostId==kv.Key && p.GetComponent<KBatchedAnimController>().IsVisible());
                if(copies!=1) throw new InvalidDataException("Visible duplicant copies for "+kv.Key+": "+copies);
                rows.Add(Tuple.Create(kv.Key,kv.Value,Read(a)));
                if(rows.Count==8) break;
            }
            if(rows.Count==0) return false;
            using(var stream=new MemoryStream())
            using(var w=new BinaryWriter(stream))
            {
                w.Write(rows.Count);
                foreach(var row in rows)
                {
                    w.Write(row.Item1); w.Write(row.Item2);
                    foreach(float n in row.Item3) w.Write(n);
                    log("DUAL_GUEST_DUPE_POSE id="+row.Item1+" tick="+row.Item2+" gpu="+string.Join(",",row.Item3.Select(n=>n.ToString("R",CultureInfo.InvariantCulture)))+" visibleCopies=1");
                }
                SteamMatchmaking.SetLobbyMemberData(room,Witness,Convert.ToBase64String(stream.ToArray()));
            }
            reported=true;
            return false;
        }
        internal static bool Host(CSteamID room,CSteamID friend,Action<string> log)
        {
            var witness=SteamMatchmaking.GetLobbyMemberData(room,friend,Witness);
            if(string.IsNullOrEmpty(witness)) return false;
            using(var r=new BinaryReader(new MemoryStream(Convert.FromBase64String(witness))))
            {
                int count=r.ReadInt32();
                if(count<1 || count>8) throw new InvalidDataException("Invalid pose witness count");
                int matched=0;
                for(int i=0;i<count;i++)
                {
                    int id=r.ReadInt32(),tick=r.ReadInt32();
                    var actual=new float[9]; for(int j=0;j<9;j++) actual[j]=r.ReadSingle();
                    if(!hostFrames.TryGetValue(tick,out var samples) || !samples.TryGetValue(id,out var expected)) continue;
                    for(int j=0;j<9;j++)
                        if(!(Math.Abs(expected[j]-actual[j])<=.005f))
                            throw new InvalidDataException($"Dupe pose id={id} tick={tick} component={j} host={expected[j]:R} guest={actual[j]:R}");
                    matched++;
                    log("DUAL_HOST_DUPE_POSE id="+id+" tick="+tick+" gpu="+string.Join(",",expected.Select(n=>n.ToString("R",CultureInfo.InvariantCulture)))+" matched=True tolerance=0.005");
                }
                if(matched!=count) throw new InvalidDataException($"Missing host renderer samples: matched={matched} requested={count}");
            }
            if(!SteamMatchmaking.SetLobbyData(room,Ack,"2")) throw new IOException("Steam pose confirmation failed");
            observe=false;
            return true;
        }
    }
}
