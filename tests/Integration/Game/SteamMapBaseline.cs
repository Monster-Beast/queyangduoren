using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamMapBaseline
    {
        static readonly Vector2I[] Route = Environment.GetEnvironmentVariable("QY_MAP01_DOOMED_LAB") == "1"
            ? new[] { new Vector2I(127, 196), new Vector2I(95, 180), new Vector2I(81, 180) }
            : new[] { new Vector2I(129, 193), new Vector2I(99, 166), new Vector2I(140, 174) };
        const string StageKey = "qy_map01_stage", CapturedKey = "qy_map01_captured";
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("QY_CHECK_COLONY_RUN") == "1";
        static readonly bool VSyncOff = Environment.GetEnvironmentVariable("QY_MAP01_VSYNC_OFF") == "1";
        static readonly bool PumpControl = Environment.GetEnvironmentVariable("QY_MAP01_PUMP_CONTROL") == "1";
        static long suspendHostUntil;
        static float pumpMs;
        static int pumpFrame;
        readonly List<float> frameTimes = new List<float>();
        readonly List<float> pumpTimes = new List<float>();
        int stage;
        double start, lastFrame, deadline;
        float simStart;
        double cpuStart;
        FixtureFrame photo;

        internal bool Check(CSteamID lobby, CSteamID guest, bool host, Action<string> log)
        {
            if (!Enabled || stage == Route.Length) return true;
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (deadline == 0) deadline = now + 100;
            if (now > deadline) throw new TimeoutException($"MAP-01 dual observation stopped at stage={stage} host={host}");
            string state = (stage + 1).ToString();
            var speed = SpeedControlScreen.Instance;
            if (start == 0)
            {
                if (host)
                {
                    if (stage == 0)
                    {
                        speed.SetSpeed(0);
                        if (speed.IsPaused) speed.Unpause(false);
                    }
                    if (!SteamMatchmaking.SetLobbyData(lobby, StageKey, state))
                        throw new IOException("Could not publish MAP-01 camera stage");
                }
                else if (SteamMatchmaking.GetLobbyData(lobby, StageKey) != state) return false;
                if (VSyncOff) QualitySettings.vSyncCount = 0;
                var camera = CameraController.Instance;
                var target = Route[PumpControl ? 0 : stage];
                camera.SetTargetPos(new Vector3(target.x + .5f, target.y + .5f, camera.transform.position.z), 12, false);
                start = now;
                lastFrame = now;
                bool suspend = PumpControl && host && stage == 1;
                if (suspend) suspendHostUntil = Stopwatch.GetTimestamp() + 12 * Stopwatch.Frequency;
                simStart = DisplayTime(host);
                using (var process = Process.GetCurrentProcess()) cpuStart = process.TotalProcessorTime.TotalSeconds;
                log($"MAP01_DUAL_START role={(host ? "host" : "guest")} index={stage} utc={System.DateTime.UtcNow:O} vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate} focused={Application.isFocused} hostPumpControl={PumpControl && stage == 1}");
                return false;
            }
            if (photo == null)
            {
                frameTimes.Add((float)((now - lastFrame) * 1000));
                lastFrame = now;
                if (pumpFrame >= Time.frameCount - 1) pumpTimes.Add(pumpMs);
                if (now - start < 12) return false;
                if (host) suspendHostUntil = 0;
                frameTimes.Sort();
                pumpTimes.Sort();
                string role = host ? "host" : "guest";
                var view = CoopSession.NativeView;
                bool paused = host ? speed.IsPaused : view?.Paused ?? true;
                var rate = host ? (speed.GetSpeed() + 1).ToString() : view?.Speed.ToString();
                log(FormattableString.Invariant($"MAP01_DUAL_STAGE role={role} index={stage} real={now-start:F3} sim={DisplayTime(host)-simStart:F3} speed={rate} paused={paused} frames={frameTimes.Count} p50ms={Percentile(.5f):F2} p95ms={Percentile(.95f):F2} p99ms={Percentile(.99f):F2}"));
                using (var process = Process.GetCurrentProcess())
                    log(FormattableString.Invariant($"MAP01_DUAL_PROCESS role={role} index={stage} pumpSamples={pumpTimes.Count} pumpP95ms={PumpPercentile():F2} cpuSeconds={process.TotalProcessorTime.TotalSeconds-cpuStart:F2}"));
                photo = new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),
                    $"map01-{role}-{stage}.png"));
                photo.Capture();
                return false;
            }
            if (!photo.Complete) return false;
            if (host && SteamMatchmaking.GetLobbyMemberData(lobby, guest, CapturedKey) != state) return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(lobby, CapturedKey, state);
            stage++;
            frameTimes.Clear();
            pumpTimes.Clear();
            start = 0;
            photo = null;
            return stage == Route.Length;
        }

        float Percentile(float p) => frameTimes[Mathf.CeilToInt(frameTimes.Count * p) - 1];
        float PumpPercentile() => pumpTimes.Count == 0 ? -1 : pumpTimes[Mathf.CeilToInt(pumpTimes.Count * .95f) - 1];

        static float DisplayTime(bool host)
        {
            if (host) return GameClock.Instance.GetTime();
            var view = CoopSession.NativeView;
            return (view.Cycle - 1 + view.CycleFraction) * 600f;
        }

        [HarmonyPatch(typeof(CoopSession), nameof(CoopSession.Pump))]
        static class MeasurePump
        {
            static bool Prefix(out long __state)
            {
                __state = Enabled ? Stopwatch.GetTimestamp() : 0;
                if (suspendHostUntil == 0 || __state >= suspendHostUntil) return true;
                __state = 0;
                return false;
            }

            static void Postfix(long __state)
            {
                if (__state == 0) return;
                pumpMs = (float)((Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency);
                pumpFrame = Time.frameCount;
            }
        }
    }
}
