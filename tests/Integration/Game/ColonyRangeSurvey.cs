using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class ColonyRangeSurvey
    {
        readonly Action<string> log;
        readonly string folder;
        readonly BuildingComplete[] buildings;
        readonly List<(string Category, string Prefab)> catalog;
        readonly HashSet<int> active = new HashSet<int>(), operational = new HashSet<int>();
        readonly Queue<(string Name, int Cell, float Zoom)> scenes = new Queue<(string, int, float)>();
        readonly float started, gameTime;
        readonly int minions;
        float nextSample, settleUntil;
        FixtureFrame photo;
        bool measured;

        internal ColonyRangeSurvey(string report, Action<string> log)
        {
            if (Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT") ||
                SaveLoader.Instance.GameInfo.dlcIds.Any(id => !string.IsNullOrEmpty(id)))
                throw new InvalidOperationException("Survey requires a Vanilla colony in an isolated profile");
            this.log = log;
            folder = Path.GetDirectoryName(report);
            var world = ClusterManager.Instance.activeWorld;
            buildings = Components.BuildingCompletes.Items.Where(item => item != null &&
                ClusterUtil.GetMyWorldId(item) == world.id && item.GetComponent<Building>() != null).ToArray();
            catalog = TUNING.BUILDINGS.PLANORDER.SelectMany(plan => plan.buildingAndSubcategoryData.Select(entry =>
                (Category: HashCache.Get().Get(plan.category) ?? plan.category.ToString(), Prefab: entry.Key)))
                .Where(entry =>
                {
                    var def = Assets.GetBuildingDef(entry.Prefab);
                    return def != null && def.IsAvailable() && def.ShouldShowInBuildMenu() && global::Game.IsCorrectDlcActiveForCurrentSave(def);
                }).GroupBy(entry => entry.Prefab, StringComparer.Ordinal).Select(group => group.First()).ToList();
            minions = Components.LiveMinionIdentities.Items.Count;
            try
            {
                var view = HostWorld.Capture(-1, -1);
                ColonyView.Decode(view);
                int maxMotion = NativeMotionStream.Capture(-1, -1).Select(packet => packet.Length).DefaultIfEmpty().Max();
                log($"COLONY_SURVEY_FIRST_VIEW bytes={view.Length} maxMotionBytes={maxMotion}");
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                log("COLONY_SURVEY_FIRST_VIEW_ERROR " + error);
            }
            string save = Path.Combine(folder, "SurveyedColony.sav");
            if (File.Exists(save) || SaveLoader.Instance.Save(save, false, false) != save || !File.Exists(save))
                throw new IOException("Surveyed colony native save failed");
            var speed = SpeedControlScreen.Instance;
            speed.SetSpeed(0);
            while (speed.IsPaused) speed.Unpause(false);
            gameTime = GameClock.Instance.GetTime();
            started = Time.unscaledTime;
            log($"COLONY_SURVEY_LOADED cycle={GameClock.Instance.GetCycle()} minions={minions} completed={buildings.Length} vanillaCatalog={catalog.Count}");
        }

        internal bool Tick()
        {
            float now = Time.unscaledTime;
            if (now - started > 105) throw new TimeoutException("Colony survey did not complete");
            if (!measured)
            {
                if (now >= nextSample)
                {
                    nextSample = now + .7f;
                    foreach (var building in buildings.Where(item => item != null))
                    {
                        var op = building.GetComponent<Operational>();
                        if (op == null) continue;
                        int id = building.GetComponent<KPrefabID>().InstanceID;
                        if (op.IsActive) active.Add(id);
                        if (op.IsOperational) operational.Add(id);
                    }
                }
                if (now - started < 25) return false;
                SpeedControlScreen.Instance.Pause(false);
                Record();
                measured = true;
            }
            if (photo != null)
            {
                if (!photo.Complete) return false;
                photo = null;
                scenes.Dequeue();
            }
            if (scenes.Count == 0)
            {
                log("PASS COLONY_SURVEY");
                return true;
            }
            var scene = scenes.Peek();
            if (settleUntil == 0)
            {
                var camera = CameraController.Instance;
                var point = Grid.CellToPosCCC(scene.Cell, Grid.SceneLayer.Move);
                point.z = camera.transform.position.z;
                camera.SetTargetPos(point, scene.Zoom, false);
                camera.SetPosition(point);
                camera.OrthographicSize = scene.Zoom;
                settleUntil = now + .5f;
                return false;
            }
            if (now < settleUntil) return false;
            photo = new FixtureFrame(Path.Combine(folder, scene.Name + ".png"));
            photo.Capture();
            settleUntil = 0;
            return false;
        }

        void Record()
        {
            var present = buildings.Where(item => item != null).GroupBy(item => item.Def.PrefabID)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var catalogIds = new HashSet<string>(catalog.Select(entry => entry.Prefab));
            int completedTypes = present.Keys.Count(catalogIds.Contains);
            var pod = Components.Telepads.Items.FirstOrDefault(item => item != null);
            int overview = pod == null ? Grid.PosToCell(CameraController.Instance.transform.position) : Grid.PosToCell(pod);
            scenes.Enqueue(("colony-overview", overview, 22));
            var table = new List<string> { "Category\tPrefab\tBuilt\tOperationalSeen\tActiveSeen\tExampleCell\tX\tY" };
            WriteGroups(catalog);
            WriteGroups(present.Keys.Where(id => !catalogIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => (Category: "Other", Prefab: id)));
            File.WriteAllLines(Path.Combine(folder, "colony-buildings.tsv"), table);
            log(FormattableString.Invariant($"COLONY_SURVEY_RESULT builtTypes={completedTypes} catalogTypes={catalog.Count} operationalInstances={operational.Count} activeInstances={active.Count} simSeconds={GameClock.Instance.GetTime()-gameTime:F1} livingStart={minions} livingEnd={Components.LiveMinionIdentities.Items.Count}"));

            void WriteGroups(IEnumerable<(string Category, string Prefab)> entries)
            {
                foreach (var category in entries.GroupBy(entry => entry.Category))
                {
                    BuildingComplete witness = null;
                    foreach (var entry in category)
                    {
                        var instances = present.TryGetValue(entry.Prefab, out var group) ? group : Array.Empty<BuildingComplete>();
                        var example = instances.Where(item => Visible(Cell(item))).OrderByDescending(item => active.Contains(Id(item))).FirstOrDefault();
                        int cell = example == null ? Grid.InvalidCell : Cell(example);
                        int x = -1, y = -1;
                        if (Visible(cell)) Grid.CellToXY(cell, out x, out y);
                        table.Add(FormattableString.Invariant($"{entry.Category}\t{entry.Prefab}\t{instances.Length}\t{instances.Count(item => operational.Contains(Id(item)))}\t{instances.Count(item => active.Contains(Id(item)))}\t{cell}\t{x}\t{y}"));
                        if (example != null && (witness == null || active.Contains(Id(example)) && !active.Contains(Id(witness)))) witness = example;
                    }
                    if (witness != null && category.Key != "Other")
                        scenes.Enqueue(($"colony-{scenes.Count:D2}", Cell(witness), 14));
                }
            }
        }

        static int Id(BuildingComplete item) => item.GetComponent<KPrefabID>().InstanceID;
        static int Cell(BuildingComplete item) => item.GetComponent<Building>().GetCell();
        static bool Visible(int cell) => Grid.IsValidCell(cell) && Grid.Visible[cell] != 0;

        internal static string BuildingSignature()
        {
            int world = ClusterManager.Instance.activeWorldId;
            var buildings = Components.BuildingCompletes.Items.Where(item => item != null && ClusterUtil.GetMyWorldId(item) == world)
                .Select(item => FormattableString.Invariant($"{Id(item)}:{Cell(item)}:{item.Def.PrefabID}"))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return FormattableString.Invariant($"{buildings.Length}/{NativeManagement.Digest(string.Join("\n", buildings))}");
        }
    }
}
