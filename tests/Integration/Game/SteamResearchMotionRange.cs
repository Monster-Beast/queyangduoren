using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamResearchMotionRange
    {
        const string Prefix = "qy_research_motion_";
        static readonly HashedString WorkLoop = new HashedString("working_loop");
        static readonly AccessTools.FieldRef<EnergyConsumer, ushort> Circuit =
            AccessTools.FieldRefAccess<EnergyConsumer, ushort>("<CircuitID>k__BackingField");
        readonly HashSet<int> frames = new HashSet<int>();
        readonly List<string> samples = new List<string> { "time,building,worker,clip,frame,frame_count,research_fraction" };
        ResearchCenter station;
        Tech tech;
        FixtureFrame photo;
        int cell, id, worker, previousFrame = -1, wraps;
        float until, nextLog, zoom, points;
        Vector3 cameraPosition;
        bool started, paused, submitted, complete;

        internal bool Check(CSteamID room, CSteamID peer, bool host, int round, Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_RESEARCH_MOTION_RANGE") != "1" || round != 1 || complete) return true;
            float now = Time.realtimeSinceStartup;
            var camera = CameraController.Instance;
            if (until == 0)
            {
                until = now + 145f;
                cameraPosition = camera.transform.position;
                zoom = camera.OrthographicSize;
            }
            if (now >= until) throw new TimeoutException($"Native research motion host={host} worker={worker} id={id} frames={frames.Count} wraps={wraps} pointsFraction={points}");
            if (host && station == null) Prepare(room);
            if (!host && id == 0)
            {
                var device = Read(room, "device").Split('|');
                if (device.Length != 2) return false;
                id = int.Parse(device[0], CultureInfo.InvariantCulture);
                cell = int.Parse(device[1], CultureInfo.InvariantCulture);
            }
            var center = Grid.CellToPosCCC(cell, Grid.SceneLayer.Building);
            center.y += 1;
            center.z = camera.transform.position.z;
            camera.SetTargetPos(center, 5f, false);
            camera.SetPosition(center);
            camera.OrthographicSize = 5f;
            var target = host ? station.GetComponent<KSelectable>() : NativeWorldMirror.GetBuildingSelectable(cell, id);
            var body = target?.GetComponent<KBatchedAnimController>();
            if (body == null || !body.IsVisible() || body.GetBatchInstanceData() == null) return false;
            if (host && !started)
            {
                if (!station.GetComponent<KPrefabID>().isSpawned || SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "ready") != "1") return false;
                var speed = SpeedControlScreen.Instance;
                paused = speed.IsPaused;
                speed.SetSpeed(0);
                station.GetComponent<Storage>().AddOre(SimHashes.Dirt, 650f, 295f, byte.MaxValue, 0, false, false);
                Research.Instance.SetActiveResearch(tech, true);
                station.GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 9));
                foreach (var dupe in Components.LiveMinionIdentities.Items.Where(d => d != null && ClusterUtil.GetMyWorldId(d) == ClusterManager.Instance.activeWorldId))
                    dupe.GetComponent<ChoreConsumer>().SetPersonalPriority(Db.Get().ChoreGroups.Research, 5);
                if (paused) speed.Unpause(false);
                started = true;
            }
            else if (!host && !started)
            {
                SteamMatchmaking.SetLobbyMemberData(room, Prefix + "ready", "1");
                started = true;
            }
            if (!submitted)
            {
                if (host)
                {
                    // The fixture supplies power. The original scheduler, worker,
                    // workstation animation and material conversion run normally.
                    var energy = station.GetComponent<EnergyConsumer>();
                    Circuit(energy) = 1;
                    energy.SetConnectionStatus(CircuitManager.ConnectionStatus.Powered);
                    station.GetComponent<RequireInputs>().Sim200ms(0);
                    points = station.GetPercentComplete();
                    if (station.worker != null && worker == 0 && body.currentAnim == WorkLoop)
                    {
                        worker = station.worker.GetComponent<KPrefabID>().InstanceID;
                        Publish(room, "worker", worker.ToString(CultureInfo.InvariantCulture));
                    }
                }
                else if (worker == 0) int.TryParse(Read(room, "worker"), out worker);

                int count = body.GetCurrentNumFrames();
                int frame = body.GetCurrentFrameIndex() - body.GetFirstFrameIndex();
                if (worker != 0 && (!host || station.worker != null) && body.currentAnim == WorkLoop && frame >= 0 && frame < count)
                {
                    frames.Add(frame);
                    if (previousFrame - frame > count / 2) wraps++;
                    previousFrame = frame;
                    samples.Add(FormattableString.Invariant($"{now:R},{id},{worker},{body.currentAnim.HashValue},{frame},{count},{(host ? (float?)points : null)}"));
                    if (photo == null && frames.Count >= 4)
                    {
                        photo = new FixtureFrame(Path.Combine(Folder, $"research-motion-{Role(host)}.png"));
                        photo.Capture();
                    }
                }
                if (now >= nextLog)
                {
                    log($"RESEARCH_MOTION_WAIT role={Role(host)} id={id} operational={station?.GetComponent<Operational>().IsOperational} worker={worker} clip={body.currentAnim.HashValue} frame={frame}/{count} sampled={frames.Count} wraps={wraps} points={points}");
                    nextLog = now + 5f;
                }
                int minimum = Math.Max(3, Math.Min(12, count - 1));
                if (frames.Count < minimum || wraps < 2 || photo?.Complete != true) return false;
                if (host)
                {
                    if (points <= 0 || station.worker == null) return false;
                    Publish(room, "frames", string.Join(",", frames.OrderBy(f => f)));
                }
                else
                {
                    var reference = Read(room, "frames");
                    if (string.IsNullOrEmpty(reference)) return false;
                    if (reference.Split(',').Select(f => int.Parse(f, CultureInfo.InvariantCulture)).Count(frames.Contains) < minimum)
                        throw new InvalidDataException("Guest did not render the native research station's working frames");
                }
                File.WriteAllLines(Path.Combine(Folder, $"research-motion-{Role(host)}.csv"), samples);
                log(FormattableString.Invariant($"RESEARCH_MOTION_RANGE role={Role(host)} id={id} worker={worker} clip=working_loop frames={frames.Count}/{count} wraps={wraps} researchFraction={(host ? (float?)points : null)}"));
                if (!host) SteamMatchmaking.SetLobbyMemberData(room, Prefix + "done", "1");
                submitted = true;
            }
            if (host)
            {
                if (SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "done") != "1") return false;
                Research.Instance.SetActiveResearch(null, true);
                if (paused) SpeedControlScreen.Instance.Pause(false);
                Publish(room, "release", "1");
            }
            else if (Read(room, "release") != "1") return false;
            camera.SetTargetPos(cameraPosition, zoom, false);
            camera.SetPosition(cameraPosition);
            camera.OrthographicSize = zoom;
            complete = true;
            return true;
        }

        void Prepare(CSteamID room)
        {
            if (!CoopSession.IsHosting || Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                throw new InvalidOperationException("Research motion requires an isolated host");
            station = NativeFacilityChecks.Build(ResearchCenterConfig.ID, ClusterManager.Instance.activeWorldId).GetComponent<ResearchCenter>();
            cell = station.GetComponent<Building>().GetCell();
            id = station.GetComponent<KPrefabID>().InstanceID;
            string type = station.GetResearchType();
            tech = Db.Get().Techs.resources.First(t => !t.Disabled && t.FoundNode && !t.IsComplete() && t.ArePrerequisitesComplete() &&
                t.costsByResearchTypeID.TryGetValue(type, out float cost) && cost >= 4 && t.costsByResearchTypeID.Values.Count(n => n > 0) == 1);
            Publish(room, "device", FormattableString.Invariant($"{id}|{cell}"));
        }

        static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
        static string Role(bool host) => host ? "host" : "guest";
        static string Read(CSteamID room, string field) => SteamMatchmaking.GetLobbyData(room, Prefix + field);
        static void Publish(CSteamID room, string field, string value)
        {
            if (!SteamMatchmaking.SetLobbyData(room, Prefix + field, value)) throw new IOException("Could not publish research motion " + field);
        }
    }
}
