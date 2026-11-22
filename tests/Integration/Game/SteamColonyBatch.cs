using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Queyang.Core;
using Queyang.Game;
using Steamworks;
using UnityEngine;
using WireMessage = Queyang.Core.Message;

namespace Queyang.Integration
{
    internal sealed class SteamColonyBatch
    {
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("QY_COLONY_BATCH") == "1";
        static readonly Vector2I[] Stops = { new Vector2I(81,180), new Vector2I(146,230), new Vector2I(127,196) };
        static SteamColonyBatch current;
        readonly HashSet<string> marks = new HashSet<string>();
        readonly HashSet<Vector2I> edges = new HashSet<Vector2I>();
        Action<string> log;
        int round, stage, modQueuePeak;
        bool host;
        Action<string> publishSignature;
        long moved, matched;
        FixtureFrame photo;
        Vector3 parked;
        internal string BuildingSignature { get; private set; }

        internal void Begin(bool isHost, int joinRound, Action<string> write, Action<string> publish = null)
        {
            if (!Enabled || round == joinRound) return;
            host = isHost; round = joinRound; log = write; publishSignature = publish;
            stage = 0; moved = matched = 0; photo = null;
            marks.Clear(); edges.Clear(); modQueuePeak = 0;
            BuildingSignature = null;
            current = this;
            Mark(host ? "invite" : "join");
        }

        void Mark(string name, string fields = "")
        {
            if (!marks.Add(name)) return;
            log($"MAP02_MARK role={(host ? "host" : "guest")} round={round} stage={name} qpc={Stopwatch.GetTimestamp()} hz={Stopwatch.Frequency}{fields}");
        }

        internal void ToolReady()
        {
            if (!Enabled || marks.Contains("tool_ready")) return;
            if (PlayerController.Instance == null || DigTool.Instance == null || SelectTool.Instance == null)
                throw new InvalidOperationException("Native tools missing after first applied view");
            PlayerController.Instance.ActivateTool(DigTool.Instance);
            if (PlayerController.Instance.ActiveTool != DigTool.Instance)
                throw new InvalidOperationException("Native DigTool could not activate after join");
            PlayerController.Instance.ActivateTool(SelectTool.Instance);
            Mark("tool_ready");
        }

        internal bool CameraSweep(CSteamID lobby, CSteamID guest)
        {
            if (!Enabled) return true;
            if (round == 2) return true;
            if (stage == Stops.Length) return true;
            string token = (stage + 1).ToString();
            var camera = CameraController.Instance;
            if (moved == 0)
            {
                if (!host && SteamMatchmaking.GetLobbyData(lobby,"qy_batch_view") != token) return false;
                var target = host ? new Vector2I(115,203) : Stops[stage];
                parked = new Vector3(target.x+.5f,target.y+.5f,camera.transform.position.z);
                float zoom = stage == Stops.Length-1 ? 20 : 9;
                camera.SetTargetPos(parked,zoom,false); camera.SetPosition(parked); camera.OrthographicSize=zoom;
                moved = Stopwatch.GetTimestamp();
                edges.Clear();
                if (host && !SteamMatchmaking.SetLobbyData(lobby,"qy_batch_view",token))
                    throw new IOException("Could not start paired camera step");
                return false;
            }
            double elapsed = Milliseconds(Stopwatch.GetTimestamp() - moved);
            if (elapsed > 22000) throw new TimeoutException($"MAP-03 camera step={stage} host={host} edges={edges.Count}");
            if (Vector2.Distance(parked,camera.baseCamera.transform.position) > 1)
                throw new InvalidOperationException("Camera moved away from the requested independent position");
            if (!host && photo == null)
            {
                var view=CoopSession.NativeView;
                var world=ClusterManager.Instance.activeWorld;
                var origin=world.WorldOffset; var size=world.WorldSize;
                int x=Math.Max(origin.x,Math.Min(origin.x+size.x-ColonyView.Width,Stops[stage].x-ColonyView.Width/2));
                int y=Math.Max(origin.y,Math.Min(origin.y+size.y-ColonyView.Height,Stops[stage].y-ColonyView.Height/2));
                if (view?.X!=x || view.Y!=y || CoopSession.View==null) { matched=0; return false; }
                if (matched==0) matched=Stopwatch.GetTimestamp();
                if (stage==Stops.Length-1 && edges.Count==0) return false;
                double first=Milliseconds(matched-moved);
                log(FormattableString.Invariant($"MAP03_VIEW role=guest step={stage} x={x} y={y} firstMs={first:F1} captureMs={elapsed:F1} peripheral={edges.Count} zoom={camera.OrthographicSize:F1}"));
                Capture();
                return false;
            }
            if (host && photo == null)
            {
                if (SteamMatchmaking.GetLobbyMemberData(lobby,guest,"qy_batch_captured") != token) return false;
                log(FormattableString.Invariant($"MAP03_VIEW role=host step={stage} centerX={parked.x:F1} centerY={parked.y:F1} zoom={camera.OrthographicSize:F1}"));
                Capture();
                return false;
            }
            if (!photo.Complete) return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(lobby,"qy_batch_captured",token);
            stage++; moved = matched = 0; photo = null;
            return stage == Stops.Length;
        }

        void Capture()
        {
            var dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
            photo=new FixtureFrame(Path.Combine(dir,$"map03-{(host ? "host" : "guest")}-{stage}.png"));
            photo.Capture();
        }

        static double Milliseconds(long ticks) => ticks*1000.0/Stopwatch.Frequency;

        [HarmonyPatch(typeof(RoomHost),nameof(RoomHost.Receive))]
        static class Accept
        {
            static bool Prepare() => Enabled;
            static void Postfix(byte[] frame)
            {
                if (current?.host==true && Kind(frame)==WireMessage.Hello) current.Mark("hello");
            }
        }

        [HarmonyPatch(typeof(RoomClient),nameof(RoomClient.Receive))]
        static class Transfer
        {
            static bool Prepare() => Enabled;
            static void Prefix(byte[] frame)
            {
                if (current==null || current.host) return;
                switch (Kind(frame))
                {
                    case WireMessage.Welcome: current.Mark("welcome"); break;
                    case WireMessage.NativeBegin: current.Mark("transfer_begin"); break;
                    case WireMessage.NativeEnd: current.Mark("transfer_end"); break;
                }
            }
        }

        static WireMessage Kind(byte[] frame) => frame.Length>=16 ? (WireMessage)BitConverter.ToUInt16(frame,8) : 0;

        [HarmonyPatch(typeof(CoopSession),"CaptureNativeSave")]
        static class Save
        {
            static bool Prepare() => Enabled;
            static void Prefix()
            {
                if (current?.host!=true) return;
                var speed=SpeedControlScreen.Instance;
                current.Mark("snapshot_begin",$" paused={speed.IsPaused} speed={speed.GetSpeed()+1}");
            }
            static void Postfix(Stream __result)
            {
                if (current?.host!=true) return;
                current.Mark("snapshot_end",$" bytes={__result.Length} paused={SpeedControlScreen.Instance.IsPaused}");
                current.BuildingSignature=ColonyRangeSurvey.BuildingSignature();
                current.publishSignature(current.BuildingSignature);
            }
        }

        [HarmonyPatch(typeof(NativeGuestScene),nameof(NativeGuestScene.Load))]
        static class Verified
        {
            static bool Prepare() => Enabled;
            static void Prefix() { if (current?.host==false) current.Mark("verified"); }
        }

        [HarmonyPatch(typeof(RoomClient),nameof(RoomClient.NativeWorldReady))]
        static class Ready
        {
            static bool Prepare() => Enabled;
            static void Postfix() { if (current?.host==false) current.Mark("native_ready"); }
        }

        [HarmonyPatch(typeof(CoopSession),"ApplyNativeTile")]
        static class View
        {
            static bool Prepare() => Enabled;
            static void Postfix(ColonyView tile, bool center)
            {
                if (current?.host!=false) return;
                if (center) current.Mark("view_applied");
                else if (current.matched!=0) current.edges.Add(new Vector2I(tile.X,tile.Y));
            }
        }

        [HarmonyPatch(typeof(SteamBackend),"QueueReliable")]
        static class Queue
        {
            static readonly FieldInfo Bytes=AccessTools.DeclaredField(AccessTools.Inner(typeof(SteamBackend),"Peer"),"QueuedBytes");
            static bool Prepare() => Enabled;
            static void Postfix(object peer)
            {
                if (current?.host==true) current.modQueuePeak=Math.Max(current.modQueuePeak,(int)Bytes.GetValue(peer));
            }
        }

        internal void CaptureSummary()
        {
            if (Enabled) log($"MAP02_QUEUE round={round} modReliablePeak={modQueuePeak}");
        }
    }
}
