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
        static readonly AccessTools.FieldRef<ImageToggleState, ImageToggleState.State> ToggleState =
            AccessTools.FieldRefAccess<ImageToggleState, ImageToggleState.State>("currentState");
        readonly HashSet<int> frames = new HashSet<int>();
        readonly List<string> samples = new List<string> { "qpc,time,building,worker,clip,frame,frame_count,research_fraction" };
        ResearchCenter station;
        Battery power;
        List<global::Wire> wires;
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
                power.AddEnergy(power.Capacity - power.JoulesAvailable);
                station.GetComponent<Storage>().AddOre(SimHashes.Dirt, 650f, 295f, byte.MaxValue, 0, false, false);
                Research.Instance.SetActiveResearch(tech, true);
                station.GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 9));
                foreach (var dupe in Components.LiveMinionIdentities.Items.Where(d => d != null && ClusterUtil.GetMyWorldId(d) == ClusterManager.Instance.activeWorldId))
                    dupe.GetComponent<ChoreConsumer>().SetPersonalPriority(Db.Get().ChoreGroups.Research, 5);
                while (speed.IsPaused) speed.Unpause(false);
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
                    points = station.GetPercentComplete();
                    if (station.worker != null && worker == 0 && body.currentAnim == WorkLoop)
                    {
                        worker = station.worker.GetComponent<KPrefabID>().InstanceID;
                        Publish(room, "worker", worker.ToString(CultureInfo.InvariantCulture));
                        log($"RESEARCH_MOTION_RECORD id={id} worker={worker}");
                    }
                }
                else if (worker == 0) int.TryParse(Read(room, "worker"), out worker);

                int count = body.GetCurrentNumFrames();
                int frame = body.GetCurrentFrameIndex() - body.GetFirstFrameIndex();
                var controls = SpeedControlScreen.Instance;
                bool speedMatches = SpeedArt(controls.speedButtonWidget_slow, true) && SpeedArt(controls.speedButtonWidget_medium, false) &&
                    SpeedArt(controls.speedButtonWidget_fast, false) && SpeedArt(controls.pauseButtonWidget, false);
                if (worker != 0 && (!host || station.worker != null) && body.currentAnim == WorkLoop && frame >= 0 && frame < count)
                {
                    frames.Add(frame);
                    if (previousFrame - frame > count / 2) wraps++;
                    previousFrame = frame;
                    long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    samples.Add(FormattableString.Invariant($"{stamp},{now:R},{id},{worker},{body.currentAnim.HashValue},{frame},{count},{(host ? (float?)points : null)}"));
                    if (photo == null && frames.Count >= 4)
                    {
                        photo = new FixtureFrame(Path.Combine(Folder, $"research-motion-{Role(host)}.png"));
                        photo.Capture();
                    }
                }
                if (now >= nextLog)
                {
                    log($"RESEARCH_MOTION_WAIT role={Role(host)} id={id} operational={station?.GetComponent<Operational>().IsOperational} worker={worker} clip={body.currentAnim.HashValue} frame={frame}/{count} sampled={frames.Count} wraps={wraps} points={points} speedMatches={speedMatches}");
                    if (host && worker == 0)
                    {
                        var candidates = Components.LiveMinionIdentities.Items.Where(d => d != null)
                            .Select(d => $"{d.GetComponent<Navigator>().CanReach(station)}:{d.GetComponent<ChoreDriver>().GetCurrentChore()?.choreType.Id}");
                        var energy = station.GetComponent<EnergyConsumer>();
                        log(FormattableString.Invariant($"RESEARCH_MOTION_LABOR time={GameClock.Instance.GetTime():0.0} paused={SpeedControlScreen.Instance.IsPaused} scale={Time.timeScale} material={station.GetComponent<Storage>().MassStored()} nativeWorker={station.worker?.GetComponent<KPrefabID>().InstanceID} circuit={energy.CircuitID} powered={energy.IsPowered} batteryJoules={power.JoulesAvailable:R} reachAndChores={string.Join(",", candidates)}"));
                    }
                    nextLog = now + 5f;
                }
                int minimum = Math.Max(3, Math.Min(12, count - 1));
                if (frames.Count < minimum || wraps < 2 || photo?.Complete != true || !speedMatches) return false;
                if (host)
                {
                    var energy = station.GetComponent<EnergyConsumer>();
                    if (points <= 0 || station.worker == null || !energy.IsPowered) return false;
                    log(FormattableString.Invariant($"RESEARCH_MOTION_CIRCUIT id={energy.CircuitID} wires={wires.Count} batteryJoules={power.JoulesAvailable:R}"));
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
                log($"RESEARCH_MOTION_SPEED role={Role(host)} slow=True medium=False fast=False paused=False qpcHz={System.Diagnostics.Stopwatch.Frequency}");
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
            int world = ClusterManager.Instance.activeWorldId;
            station = NativeFacilityChecks.Build(ResearchCenterConfig.ID, world).GetComponent<ResearchCenter>();
            power = NativeFacilityChecks.Build(BatteryConfig.ID, world).GetComponent<Battery>();
            cell = station.GetComponent<Building>().GetCell();
            id = station.GetComponent<KPrefabID>().InstanceID;
            string type = station.GetResearchType();
            tech = Db.Get().Techs.resources.First(t => !t.Disabled && t.FoundNode && !t.IsComplete() && t.ArePrerequisitesComplete() &&
                t.costsByResearchTypeID.TryGetValue(type, out float cost) && cost >= 4 && t.costsByResearchTypeID.Values.Count(n => n > 0) == 1);
            wires = WireCircuit(station.GetComponent<Building>().GetPowerInputCell(), power.GetComponent<Building>().GetPowerInputCell(), world);
            Publish(room, "device", FormattableString.Invariant($"{id}|{cell}"));
        }

        static List<global::Wire> WireCircuit(int start, int end, int world)
        {
            var steps = new (int Delta, UtilityConnections Direction, UtilityConnections Opposite)[] {
                (-1, UtilityConnections.Left, UtilityConnections.Right),
                (1, UtilityConnections.Right, UtilityConnections.Left),
                (-Grid.WidthInCells, UtilityConnections.Down, UtilityConnections.Up),
                (Grid.WidthInCells, UtilityConnections.Up, UtilityConnections.Down)
            };
            bool Vacant(int c) => Grid.IsValidBuildingCell(c) && Grid.WorldIdx[c] == world && Grid.Visible[c] != 0 &&
                Grid.Objects[c, (int)ObjectLayer.Wire] == null && Grid.Objects[c, (int)ObjectLayer.WireTile] == null;
            if (!Vacant(start) || !Vacant(end)) throw new InvalidDataException("Research fixture power port overlaps a wire");
            var visited = new Dictionary<int, (int Parent, UtilityConnections Direction, UtilityConnections Opposite)> {
                [start] = (Grid.InvalidCell, 0, 0)
            };
            var pending = new Queue<int>();
            pending.Enqueue(start);
            while (pending.Count > 0 && !visited.ContainsKey(end))
            {
                int c = pending.Dequeue();
                foreach (var step in steps)
                {
                    int next = c + step.Delta;
                    if (!Vacant(next) || Grid.GetCellDistance(c, next) != 1 || Grid.GetCellDistance(start, next) > 24 ||
                        visited.ContainsKey(next)) continue;
                    visited.Add(next, (c, step.Direction, step.Opposite));
                    pending.Enqueue(next);
                }
            }
            if (!visited.ContainsKey(end)) throw new InvalidDataException("No short free path for the research fixture circuit");
            var def = Assets.GetBuildingDef(WireConfig.ID);
            var material = new[] { ElementLoader.FindElementByHash(SimHashes.Cuprite).tag };
            var manager = global::Game.Instance.electricalConduitSystem;
            var built = new List<global::Wire>();
            UtilityConnections fromNext = 0;
            for (int c = end; c != Grid.InvalidCell; c = visited[c].Parent)
            {
                var step = visited[c];
                var obj = def.Build(c, Orientation.Neutral, null, material, 295f, false, -1f);
                if (obj == null) throw new InvalidDataException($"Could not build native wire at cell {c}");
                built.Add(obj.GetComponent<global::Wire>());
                TerrainNetworks.Set(manager, c, obj, fromNext | step.Opposite);
                fromNext = step.Direction;
            }
            return built;
        }

        static string Folder => Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
        static bool SpeedArt(GameObject widget, bool active)
        {
            var toggle = widget.GetComponent<KToggle>();
            return toggle != null && toggle.isOn == active && widget.GetComponents<ImageToggleState>()
                .All(image => (ToggleState(image) == ImageToggleState.State.Active ||
                    ToggleState(image) == ImageToggleState.State.DisabledActive) == active);
        }
        static string Role(bool host) => host ? "host" : "guest";
        static string Read(CSteamID room, string field) => SteamMatchmaking.GetLobbyData(room, Prefix + field);
        static void Publish(CSteamID room, string field, string value)
        {
            if (!SteamMatchmaking.SetLobbyData(room, Prefix + field, value)) throw new IOException("Could not publish research motion " + field);
        }
    }
}
