using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamBuildingMotionRange
    {
        const string Prefix = "qy_building_motion_";
        const int ChargeSteps = 30;
        const float StepSeconds = .2f;
        static readonly HashedString Loop = new HashedString("working_loop");
        static readonly AccessTools.FieldRef<Battery, MeterController> Meter =
            AccessTools.FieldRefAccess<Battery, MeterController>("meter");
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long value);

        readonly List<string> rows = new List<string> { "qpc,time,generator_id,generator_clip,generator_frame,battery_id,meter_frame,host_joules" };
        readonly HashSet<int> loopFrames = new HashSet<int>();
        readonly HashSet<int> riseFrames = new HashSet<int>();
        readonly HashSet<int> fallFrames = new HashSet<int>();
        GameObject generator;
        Battery battery;
        int generatorId, generatorCell, batteryId, batteryCell;
        int step, previousLoop = -1, previousMeter = -1, wraps;
        int expectedLoopFrames;
        float deadline, nextStep, finishAt, nextLog, oldZoom;
        bool begun, submitted, finished, wasPaused;
        Vector3 oldCamera;
        FixtureFrame screenshot;

        internal bool Check(CSteamID room, CSteamID peer, bool host, int round, Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_BUILDING_MOTION_RANGE") != "1" || round != 1 || finished) return true;
            var camera = CameraController.Instance;
            float now = Time.realtimeSinceStartup;
            if (deadline == 0)
            {
                deadline = now + 85f;
                oldCamera = camera.transform.position;
                oldZoom = camera.OrthographicSize;
            }
            if (now >= deadline) throw new TimeoutException($"Native building motion host={host} step={step} loop={loopFrames.Count} wrap={wraps} rise={riseFrames.Count} fall={fallFrames.Count}");
            if (host && generator == null) Prepare(room);
            if (!host && generatorId == 0 && !ReadDevices(room)) return false;

            var center = (Grid.CellToPosCCC(generatorCell, Grid.SceneLayer.Building) + Grid.CellToPosCCC(batteryCell, Grid.SceneLayer.Building)) * .5f;
            center.y += 1f;
            center.z = camera.transform.position.z;
            float zoom = Mathf.Max(5f, Grid.GetCellDistance(generatorCell, batteryCell) * .7f + 3f);
            camera.SetTargetPos(center, zoom, false);
            camera.SetPosition(center);
            camera.OrthographicSize = zoom;

            var motor = host ? generator.GetComponent<KBatchedAnimController>() :
                NativeWorldMirror.GetBuildingSelectable(generatorCell, generatorId)?.GetComponent<KBatchedAnimController>();
            var meterBody = host ? Meter(battery)?.meterController :
                NativeWorldMirror.GetBuildingSelectable(batteryCell, batteryId)?.GetComponentsInChildren<KBatchedAnimController>(true)
                    .FirstOrDefault(anim => anim.gameObject.name.EndsWith(".meter", StringComparison.Ordinal));
            if (!Visible(motor) || !Visible(meterBody)) return false;
            if (!begun)
            {
                if (host)
                {
                    if (SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "ready") != "1") return false;
                    if (!motor.HasAnimation(Loop)) throw new InvalidDataException("Manual generator has no native working loop");
                    wasPaused = SpeedControlScreen.Instance.IsPaused;
                    while (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
                    motor.Play(Loop, KAnim.PlayMode.Loop);
                    Publish(room, "start", "1");
                    nextStep = now;
                    log($"BUILDING_MOTION_INPUT generator={generatorId} animation=working_loop drive=stock-controller battery={batteryId} intervalMs=200 steps={ChargeSteps * 2 + 1}");
                }
                else
                {
                    if (SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "ready") != "1")
                        SteamMatchmaking.SetLobbyMemberData(room, Prefix + "ready", "1");
                    if (SteamMatchmaking.GetLobbyData(room, Prefix + "start") != "1") return false;
                }
                begun = true;
            }
            if (!submitted)
            {
                Sample(motor, meterBody, host, now);
                if (host && step <= ChargeSteps * 2 && now >= nextStep) ChangeCharge(now);
                if (screenshot == null && loopFrames.Count >= 3 && riseFrames.Count >= 12)
                {
                    screenshot = new FixtureFrame(Path.Combine(Folder, $"building-motion-{Role(host)}.png"));
                    screenshot.Capture();
                }
                if (now >= nextLog)
                {
                    nextLog = now + 4f;
                    log($"BUILDING_MOTION_WAIT role={Role(host)} loop={loopFrames.Count} wrap={wraps} rise={riseFrames.Count} fall={fallFrames.Count} step={step}");
                }
                if (host && finishAt == 0 && step > ChargeSteps * 2) finishAt = now + 1f;
                if (host && (finishAt == 0 || now < finishAt)) return false;
                var reference = host ? null : SteamMatchmaking.GetLobbyData(room, Prefix + "frames");
                if (!host && string.IsNullOrEmpty(reference)) return false;
                if (screenshot == null || !screenshot.Complete || !ValidSamples()) return false;
                if (host) Publish(room, "frames", string.Join("|", Encode(loopFrames), Encode(riseFrames), Encode(fallFrames)));
                else ConfirmReference(reference);
                File.WriteAllLines(Path.Combine(Folder, $"building-motion-{Role(host)}.csv"), rows);
                log($"BUILDING_MOTION_RANGE role={Role(host)} generator={generatorId} loopFrames={loopFrames.Count}/{expectedLoopFrames} wraps={wraps} battery={batteryId} chargeFrames={riseFrames.Count} dischargeFrames={fallFrames.Count} samples={rows.Count-1}");
                if (!host) SteamMatchmaking.SetLobbyMemberData(room, Prefix + "done", "1");
                submitted = true;
            }
            if (host)
            {
                if (SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "done") != "1") return false;
                motor.Play("off");
                if (wasPaused) SpeedControlScreen.Instance.Pause(false);
                Publish(room, "release", "1");
            }
            else if (SteamMatchmaking.GetLobbyData(room, Prefix + "release") != "1") return false;
            camera.SetTargetPos(oldCamera, oldZoom, false);
            camera.SetPosition(oldCamera);
            camera.OrthographicSize = oldZoom;
            finished = true;
            return true;
        }

        void Prepare(CSteamID room)
        {
            if (!CoopSession.IsHosting || Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                throw new InvalidOperationException("Building motion requires an isolated host");
            int world = ClusterManager.Instance.activeWorldId;
            generator = NativeFacilityChecks.Build(ManualGeneratorConfig.ID, world);
            battery = NativeFacilityChecks.Build(BatteryConfig.ID, world).GetComponent<Battery>();
            generatorId = generator.GetComponent<KPrefabID>().InstanceID;
            generatorCell = generator.GetComponent<Building>().GetCell();
            batteryId = battery.GetComponent<KPrefabID>().InstanceID;
            batteryCell = battery.GetComponent<Building>().GetCell();
            Publish(room, "devices", FormattableString.Invariant($"{generatorId}|{generatorCell}|{batteryId}|{batteryCell}"));
        }

        bool ReadDevices(CSteamID room)
        {
            var record = SteamMatchmaking.GetLobbyData(room, Prefix + "devices").Split('|');
            if (record.Length != 4) return false;
            generatorId = int.Parse(record[0], CultureInfo.InvariantCulture);
            generatorCell = int.Parse(record[1], CultureInfo.InvariantCulture);
            batteryId = int.Parse(record[2], CultureInfo.InvariantCulture);
            batteryCell = int.Parse(record[3], CultureInfo.InvariantCulture);
            return true;
        }

        void ChangeCharge(float now)
        {
            int level = step <= ChargeSteps ? step : ChargeSteps * 2 - step;
            float delta = battery.Capacity * level / ChargeSteps - battery.JoulesAvailable;
            if (delta > 0f) battery.AddEnergy(delta);
            else if (delta < 0f) battery.ConsumeEnergy(-delta);
            battery.EnergySim200ms(StepSeconds);
            step++;
            nextStep = now + StepSeconds;
        }

        void Sample(KBatchedAnimController motor, KBatchedAnimController meter, bool host, float now)
        {
            int motionFrame = RenderFrame(motor), meterFrame = RenderFrame(meter);
            if (motor.currentAnim == Loop && motionFrame >= 0)
            {
                expectedLoopFrames = motor.GetCurrentNumFrames();
                loopFrames.Add(motionFrame);
                if (previousLoop - motionFrame > expectedLoopFrames / 2) wraps++;
                previousLoop = motionFrame;
            }
            if (meterFrame >= 0 && meterFrame != previousMeter)
            {
                if (meterFrame > previousMeter) riseFrames.Add(meterFrame);
                else fallFrames.Add(meterFrame);
                previousMeter = meterFrame;
            }
            if (!QueryPerformanceCounter(out long qpc)) throw new InvalidOperationException("Native presentation timestamp unavailable");
            rows.Add(FormattableString.Invariant($"{qpc},{now:R},{generatorId},{motor.currentAnim.HashValue},{motionFrame},{batteryId},{meterFrame},{(host?(float?)battery.JoulesAvailable:null)}"));
        }

        bool ValidSamples() => loopFrames.Count >= Math.Max(4, Math.Min(20, expectedLoopFrames - 1)) && wraps >= 2 &&
            riseFrames.Count >= 20 && fallFrames.Count >= 20;

        void ConfirmReference(string reference)
        {
            var records = reference.Split('|');
            if (records.Length != 3 || !Matches(loopFrames, records[0], Math.Max(4, Math.Min(20, expectedLoopFrames - 1))) ||
                !Matches(riseFrames, records[1], 20) || !Matches(fallFrames, records[2], 20))
                throw new InvalidDataException("Rendered building frames did not match the host's native frame sets");
        }

        static bool Matches(HashSet<int> frames, string encoded, int count) =>
            encoded.Split(',').Select(frame => int.Parse(frame, CultureInfo.InvariantCulture)).Count(frames.Contains) >= count;
        static string Encode(HashSet<int> frames) => string.Join(",", frames.OrderBy(x => x));
        static bool Visible(KBatchedAnimController anim) => anim != null && anim.IsVisible() && anim.GetBatchInstanceData() != null;
        static int RenderFrame(KBatchedAnimController anim)
        {
            int frame = anim.GetCurrentFrameIndex() - anim.GetFirstFrameIndex();
            return frame >= 0 && frame < anim.GetCurrentNumFrames() ? frame : -1;
        }
        static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
        static string Role(bool host) => host ? "host" : "guest";
        static void Publish(CSteamID room, string key, string value)
        {
            if (!SteamMatchmaking.SetLobbyData(room, Prefix + key, value))
                throw new IOException("Could not publish building motion checkpoint " + key);
        }
    }
}
