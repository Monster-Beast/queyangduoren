using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Queyang.Integration
{
    // One native, paused save that both Steam clients can reuse for facility regressions.
    internal sealed class FacilityRangeSeed
    {
        static readonly string[] Prefabs = { RationBoxConfig.ID, WashBasinConfig.ID, MedicalCotConfig.ID };
        readonly Action<string> log;
        readonly string folder;
        readonly BuildingDef[] defs;
        readonly int[] cells;
        readonly GameObject[] stations;
        readonly MinionIdentity patient;
        readonly float deadline;
        FixtureFrame photo;

        internal FacilityRangeSeed(string report, Action<string> log)
        {
            if (Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                throw new InvalidOperationException("Facility range requires an isolated game profile");
            this.log = log;
            folder = Path.GetDirectoryName(report);
            deadline = Time.realtimeSinceStartup + 25;
            SpeedControlScreen.Instance.Pause(false);
            var world = ClusterManager.Instance.activeWorld;
            patient = Components.LiveMinionIdentities.Items.First(dupe => dupe != null &&
                ClusterUtil.GetMyWorldId(dupe) == world.id && dupe.GetComponent<Health>() != null);
            defs = Prefabs.Select(Assets.GetBuildingDef).ToArray();
            cells = FindStations(world, patient.GetComponent<Navigator>(), Grid.PosToCell(patient));
            stations = defs.Select((def, index) =>
            {
                var materials = def.MaterialCategory.Select(kind => MaterialSelector.GetValidMaterials(kind, true).First()).ToArray();
                return def.Build(cells[index], Orientation.Neutral, null, materials, 295f, false, -1f)
                    ?? throw new InvalidOperationException("Could not build range station " + def.PrefabID);
            }).ToArray();
        }

        int[] FindStations(WorldContainer world, Navigator navigator, int origin)
        {
            var candidates = Enumerable.Range(0, Grid.CellCount).Where(cell => Grid.WorldIdx[cell] == world.id &&
                Grid.Visible[cell] > 0 && !Grid.Solid[cell] && Grid.IsValidBuildingCell(cell) &&
                Grid.IsValidCell(Grid.CellBelow(cell)) && Grid.Solid[Grid.CellBelow(cell)] &&
                Grid.GetCellDistance(origin, cell) <= 45 && navigator.CanReach(cell))
                .OrderBy(cell => Grid.GetCellDistance(origin, cell)).ToArray();
            var legal = defs.Select(def => candidates.Where(cell =>
                Queyang.Game.HostWorld.VacantFootprint(def, cell, Orientation.Neutral, world) &&
                def.PlacementOffsets.All(offset => Grid.IsValidCell(Grid.OffsetCell(cell, offset)) && !Grid.Solid[Grid.OffsetCell(cell, offset)]) &&
                def.IsValidPlaceLocation(def.BuildingPreview, cell, Orientation.Neutral, false, out _) &&
                def.IsValidBuildLocation(def.BuildingPreview, cell, Orientation.Neutral, false, out _))
                .Select(cell => new { Cell = cell, Footprint = def.PlacementOffsets.Select(offset => Grid.OffsetCell(cell, offset)).ToArray() })
                .ToArray()).ToArray();
            foreach (var box in legal[0])
            foreach (var wash in legal[1].Where(site => Grid.GetCellDistance(box.Cell, site.Cell) <= 11 &&
                !site.Footprint.Intersect(box.Footprint).Any()))
            {
                var bed = legal[2].FirstOrDefault(site => Grid.GetCellDistance(box.Cell, site.Cell) <= 11 &&
                    Grid.GetCellDistance(wash.Cell, site.Cell) <= 11 &&
                    !site.Footprint.Intersect(box.Footprint).Any() && !site.Footprint.Intersect(wash.Footprint).Any());
                if (bed != null) return new[] { box.Cell, wash.Cell, bed.Cell };
            }
            throw new InvalidOperationException("No compact reachable area for the three facility stations");
        }

        internal bool Tick()
        {
            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Facility range did not finish preparing");
            if (stations.Any(station => station.GetComponent<KPrefabID>()?.isSpawned != true)) return false;
            if (photo == null)
            {
                var box = stations[0].GetComponent<Storage>();
                var wash = stations[1].GetComponent<Storage>();
                var direction = stations[1].GetComponent<DirectionControl>();
                var bed = stations[2].GetComponent<Assignable>();
                var patientHealth = patient.GetComponent<Health>();
                var food = Util.KInstantiate(Assets.GetPrefab(MushBarConfig.ID), box.transform.position);
                DiscoveredResources.Instance.Discover(food.PrefabID(), GameTags.Edible);
                box.GetComponent<TreeFilterable>().AddTagToFilter(food.PrefabID());
                food.GetComponent<PrimaryElement>().Mass = 6;
                food.SetActive(true);
                box.Store(food);
                wash.AddLiquid(SimHashes.Water, 40, 295, byte.MaxValue, 0);
                wash.AddLiquid(SimHashes.DirtyWater, 10, 295, byte.MaxValue, 0);
                if (patientHealth.hitPoints > .55f * patientHealth.maxHitPoints)
                    patientHealth.Damage(patientHealth.hitPoints - .55f * patientHealth.maxHitPoints);

                var camera = CameraController.Instance;
                var center = cells.Select(cell => Grid.CellToPosCCC(cell, Grid.SceneLayer.Move))
                    .Aggregate(Vector3.zero, (sum, pos) => sum + pos) / cells.Length;
                center.z = camera.transform.position.z;
                camera.SetTargetPos(center, 9f, false);
                camera.SetPosition(center);
                camera.OrthographicSize = 9f;
                SelectTool.Instance.Select(null, false);

                var positions = cells.Select((cell, index) =>
                {
                    Grid.CellToXY(cell, out int x, out int y);
                    var id = stations[index].GetComponent<KPrefabID>().InstanceID;
                    return FormattableString.Invariant($"    {{ \"prefab\": \"{Prefabs[index]}\", \"id\": {id}, \"cell\": {cell}, \"x\": {x}, \"y\": {y} }}");
                });
                File.WriteAllText(Path.Combine(folder, "facility-range.json"), "{\n  \"stations\": [\n" +
                    string.Join(",\n", positions) + "\n  ],\n" +
                    FormattableString.Invariant($"  \"foodKg\": {box.MassStored()},\n  \"foodCapacityKg\": {box.capacityKg},\n") +
                    FormattableString.Invariant($"  \"cleanWaterKg\": {wash.FindPrimaryElement(SimHashes.Water).Mass},\n  \"dirtyWaterKg\": {wash.FindPrimaryElement(SimHashes.DirtyWater).Mass},\n") +
                    $"  \"washDirection\": \"{direction.allowedDirection}\",\n  \"bedAssigned\": {(bed.assignee != null ? "true" : "false")},\n" +
                    FormattableString.Invariant($"  \"patientId\": {patient.GetComponent<KPrefabID>().InstanceID},\n  \"patientHealth\": {patientHealth.hitPoints},\n  \"patientMaxHealth\": {patientHealth.maxHitPoints}\n}}\n"));
                string save = Path.Combine(folder, "FacilityRange.sav");
                if (File.Exists(save) || SaveLoader.Instance.Save(save, false, false) != save || !File.Exists(save))
                    throw new IOException("Facility range native save failed");
                photo = new FixtureFrame(Path.Combine(folder, "facility-range-overview.png"));
                photo.Capture();
            }
            if (!photo.Complete) return false;
            log("PASS FACILITY_RANGE_PREPARED stations=3 paused=True");
            return true;
        }
    }
}
