using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    /// <summary>Called by the hosting game on Unity's main thread only, after the colony has loaded.</summary>
    public static partial class HostWorld
    {
        private static readonly ObjectLayer[] DisplayLayers = {
            ObjectLayer.Building, ObjectLayer.LadderTile, ObjectLayer.FoundationTile,
            ObjectLayer.GasConduit, ObjectLayer.LiquidConduit, ObjectLayer.Wire, ObjectLayer.LogicWire
        };

        private static readonly ConditionalWeakTable<GameObject, TargetKey> TargetKeys =
            new ConditionalWeakTable<GameObject, TargetKey>();
        // Constructable marks its footprint in OnSpawn. Several peer requests can run in the
        // same Unity frame before that callback; hold their occupied slots until ONI registers them.
        private static readonly ConditionalWeakTable<WorldContainer, Dictionary<long, GameObject>> Pending =
            new ConditionalWeakTable<WorldContainer, Dictionary<long, GameObject>>();
        private static int nextTargetKey;
        private sealed class TargetKey
        {
            internal readonly int Value;
            internal TargetKey(GameObject _) { Value = checked(++nextTargetKey); }
        }

        private static WorldContainer World =>
            ClusterManager.Instance == null ? null : ClusterManager.Instance.activeWorld;

        public static byte[] Capture(int x, int y)
        {
            var world = World;
            if (world == null || Grid.WidthInCells == 0)
                throw new InvalidOperationException("The host colony has not loaded.");
            var origin = world.WorldOffset;
            var size = world.WorldSize;
            if (x == -1 && y == -1)
            {
                x = origin.x + size.x / 2;
                y = origin.y + size.y / 2;
                foreach (var pod in Components.Telepads.Items)
                {
                    if (pod == null) continue;
                    int podCell = Grid.PosToCell(pod);
                    if (!Grid.IsValidCell(podCell) || Grid.WorldIdx[podCell] != world.id) continue;
                    Grid.CellToXY(podCell, out x, out y);
                    break;
                }
                x -= ColonyView.Width / 2;
                y -= ColonyView.Height / 2;
            }
            var view = new ColonyView {
                X = Math.Max(origin.x, Math.Min(origin.x + size.x - ColonyView.Width, x)),
                Y = Math.Max(origin.y, Math.Min(origin.y + size.y - ColonyView.Height, y))
            };
            var clock = GameClock.Instance;
            var speed = SpeedControlScreen.Instance;
            view.Cycle = clock.GetCycle() + 1;
            view.CycleFraction = Mathf.Clamp01(clock.GetCurrentCycleAsPercentage());
            view.Paused = speed.IsPaused;
            float selectedSpeed = speed.GetSpeed() == 0 ? speed.normalSpeed :
                speed.GetSpeed() == 1 ? speed.fastSpeed : speed.ultraSpeed;
            view.Speed = (byte)Mathf.Clamp(Mathf.RoundToInt(selectedSpeed), 0, 10);
            for (int dy = 0; dy < ColonyView.Height; dy++)
            {
                for (int dx = 0; dx < ColonyView.Width; dx++)
                {
                    int cell;
                    if (VisibleCell(world, view.X + dx, view.Y + dy, out cell))
                        view.Cells[dy * ColonyView.Width + dx] = ReadCell(cell);
                }
            }
            var dupes = new List<ColonyDupe>();
            foreach (var dupe in Components.LiveMinionIdentities.Items)
            {
                if (dupe == null) continue;
                int cell = Grid.PosToCell(dupe);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id || Grid.Visible[cell] == 0)
                    continue;
                int dupeX, dupeY;
                Grid.CellToXY(cell, out dupeX, out dupeY);
                if (dupeX < view.X || dupeY < view.Y || dupeX >= view.X + ColonyView.Width ||
                    dupeY >= view.Y + ColonyView.Height) continue;
                var driver = dupe.GetComponent<ChoreDriver>();
                var chore = driver == null ? null : driver.GetCurrentChore();
                var pos = dupe.transform.position;
                dupes.Add(new ColonyDupe {
                    X = dupeX, Y = dupeY, Name = dupe.GetProperName(),
                    Id = TargetKeys.GetValue(dupe.gameObject, CreateKey).Value,
                    SubX = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(pos.x - dupeX)),
                    SubY = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(pos.y - dupeY)),
                    Activity = chore == null ? "Idle" : chore.choreType.Id
                });
                if (dupes.Count == 64) break;
            }
            view.Dupes = dupes.ToArray();
            return view.Encode();
        }

        private static bool VisibleCell(WorldContainer world, int x, int y, out int cell)
        {
            cell = Grid.InvalidCell;
            // Validate both axes before flattening; an out-of-range X could otherwise alias another row.
            if (x < 0 || y < 0 || x >= Grid.WidthInCells || y >= Grid.HeightInCells)
                return false;
            cell = Grid.XYToCell(x, y);
            return Grid.WorldIdx[cell] == world.id && Grid.Visible[cell] > 0;
        }

        private static GameObject FindBuilding(int cell)
        {
            foreach (var layer in DisplayLayers)
            {
                var obj = Grid.Objects[cell, (int)layer];
                if (obj != null && obj.GetComponent<Building>() != null) return obj;
            }
            return null;
        }

        private static GameObject GetTarget(int cell)
        {
            var dig = Grid.Objects[cell, (int)ObjectLayer.DigPlacer];
            return dig != null ? dig : FindBuilding(cell);
        }

        private static ColonyCell ReadCell(int cell)
        {
            var element = Grid.Element[cell];
            var building = FindBuilding(cell);
            var dig = Grid.Objects[cell, (int)ObjectLayer.DigPlacer];
            var target = dig != null ? dig : building;
            var result = new ColonyCell {
                Flags = 1, Element = element.name, Mass = Grid.Mass[cell],
                Temperature = Grid.Temperature[cell],
                TargetId = target == null ? 0 : TargetKeys.GetValue(target, CreateKey).Value
            };
            var colour = element.substance.uiColour;
            result.R = colour.r;
            result.G = colour.g;
            result.B = colour.b;
            if (Grid.Solid[cell]) result.Flags |= 2;
            if (element.IsLiquid) result.Flags |= 4;
            if (element.id == SimHashes.Oxygen || element.id == SimHashes.ContaminatedOxygen) result.Flags |= 64;
            if (dig != null) result.Flags |= 8;
            if (building != null)
            {
                var item = building.GetComponent<Building>();
                result.Building = item.Def.Name;
                result.BuildingStyle = (byte)(((int)item.Orientation & 7) << 4);
                switch (item.Def.PrefabID)
                {
                    case LadderConfig.ID: result.BuildingStyle |= 1; break;
                    case TileConfig.ID: result.BuildingStyle |= 2; break;
                    case DoorConfig.ID: result.BuildingStyle |= 3; break;
                    case StorageLockerConfig.ID: result.BuildingStyle |= 4; break;
                    case MineralDeoxidizerConfig.ID: result.BuildingStyle |= 5; break;
                    case ManualGeneratorConfig.ID: result.BuildingStyle |= 6; break;
                }
                var deconstruct = building.GetComponent<Deconstructable>();
                if (deconstruct != null && deconstruct.IsMarkedForDeconstruction()) result.BuildingStyle |= 128;
                var construct = building.GetComponent<Constructable>();
                result.Flags |= construct != null ? (byte)32 : (byte)16;
                if (construct != null && construct.GetWorkTime() > 0)
                    result.Progress = (byte)Mathf.RoundToInt(100f * Mathf.Clamp01(construct.GetPercentComplete()));
            }
            ReadConnections(ref result, cell);
            return result;
        }

        private static TargetKey CreateKey(GameObject obj) => new TargetKey(obj);

        public static string Execute(string action, int x, int y)
        {
            int cell;
            var world = World;
            if (world != null && action != null && action.StartsWith("manage/", StringComparison.Ordinal))
                return ColonyManagement.Execute(action);
            if (world != null && action != null && action.Length <= 96 && action.StartsWith("buildOptions/", StringComparison.Ordinal))
                return Options(action, world);
            if (action == "overview" && world != null) return Overview(world);
            if (world == null || !VisibleCell(world, x, y, out cell) || !Grid.IsValidBuildingCell(cell))
                return "ERROR Cell unavailable";
            if (string.IsNullOrEmpty(action) || action.Length > 96) return "ERROR Unsupported tool";
            if (action.StartsWith("build/", StringComparison.Ordinal)) return PlaceSelected(action, cell, world);
            if (action.StartsWith("building/", StringComparison.Ordinal) || action.StartsWith("b/", StringComparison.Ordinal))
                return BuildingOptions(action, cell, x, y);
            switch (action)
            {
                case "inspect": return Inspect(cell);
                case "dig":
                    if (DigTool.Instance == null || !Grid.Solid[cell] || !Diggable.IsDiggable(cell) ||
                        Diggable.Undiggable(Grid.Element[cell]) || Diggable.GetDiggable(cell) != null)
                        return "ERROR Cell cannot be designated for digging";
                    return DigTool.PlaceDig(cell, 0) == null ? "ERROR Dig tool declined" : "OK Dig designated";
                case "ladder": return Place(LadderConfig.ID, cell, world);
                case "tile": return Place(TileConfig.ID, cell, world);
                case "door": return Place(DoorConfig.ID, cell, world);
                case "storage": return Place(StorageLockerConfig.ID, cell, world);
                case "oxygen": return Place(MineralDeoxidizerConfig.ID, cell, world);
                case "generator": return Place(ManualGeneratorConfig.ID, cell, world);
                case "sweep": return Sweep(cell);
                case "mop": return Mop(cell);
                case "harvest": return Harvest(cell);
            }
            if (action.IndexOf('@') > 0) return BuildUtilityLine(action, x, y, world);
            int colon = action.IndexOf(':');
            int expectedId;
            if (colon < 1 || !int.TryParse(action.Substring(colon + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out expectedId) || expectedId < 1)
                return "ERROR Unsupported tool or missing target";
            var target = GetTarget(cell);
            TargetKey key;
            if (target == null || !TargetKeys.TryGetValue(target, out key) || key.Value != expectedId)
                return "ERROR The selected order or building has changed";
            return EditTarget(action.Substring(0, colon), target);
        }

        private static string Place(string prefab, int cell, WorldContainer world)
        {
            GameObject order;
            return Place(prefab, cell, world, out order);
        }

        private static string Place(string prefab, int cell, WorldContainer world, out GameObject order,
            Orientation orientation = Orientation.Neutral, string primaryMaterial = null)
        {
            order = null;
            var def = Assets.GetBuildingDef(prefab);
            if (def == null || PlanScreen.Instance == null || !PlanScreen.Instance.IsDefBuildable(def))
                return "ERROR Building locked or unavailable";
            int x, y;
            Grid.CellToXY(cell, out x, out y);
            var pending = Pending.GetOrCreateValue(world);
            ReleaseRegistered(pending);
            foreach (var original in def.PlacementOffsets)
            {
                var offset = Rotatable.GetRotatedCellOffset(original, orientation);
                int occupied;
                if (!VisibleCell(world, x + offset.x, y + offset.y, out occupied) ||
                    Grid.Objects[occupied, (int)def.ObjectLayer] != null || pending.ContainsKey(Slot(occupied, def.ObjectLayer)))
                    return "ERROR Footprint hidden or occupied";
            }
            var materials = new List<Tag>();
            for (int i = 0; i < def.MaterialCategory.Length; i++)
            {
                foreach (Tag tag in MaterialSelector.GetValidMaterials(def.MaterialCategory[i], true))
                {
                    if (i == 0 && primaryMaterial != null && tag.ToString() != primaryMaterial) continue;
                    if (world.worldInventory.GetAmount(tag, true) < def.Mass[i]) continue;
                    materials.Add(tag);
                    break;
                }
                if (materials.Count <= i) return "ERROR Construction material unavailable";
            }
            if (!def.MaterialsAvailable(materials, world)) return "ERROR Construction material unavailable";
            // TryPlace invokes ONI's location checks and creates a construction order; duplicants supply it.
            var position = Grid.CellToPosCBC(cell, Grid.SceneLayer.Building);
            order = def.TryPlace(def.isUtility ? null : def.BuildingPreview, position, orientation, materials, 0);
            if (order == null) return "ERROR Game rejected placement";
            foreach (var original in def.PlacementOffsets)
            {
                var offset = Rotatable.GetRotatedCellOffset(original, orientation);
                int occupied = Grid.XYToCell(x + offset.x, y + offset.y);
                if (Grid.Objects[occupied, (int)def.ObjectLayer] == null)
                    pending[Slot(occupied, def.ObjectLayer)] = order;
            }
            return "OK Construction planned";
        }

        private static long Slot(int cell, ObjectLayer layer) => ((long)cell << 8) | (byte)layer;

        private static void ReleaseRegistered(Dictionary<long, GameObject> pending)
        {
            if (pending.Count == 0) return;
            var done = new List<long>();
            foreach (var entry in pending)
            {
                int cell = (int)(entry.Key >> 8);
                if (entry.Value == null || Grid.Objects[cell, (int)(entry.Key & 255)] != null)
                    done.Add(entry.Key);
            }
            foreach (long slot in done) pending.Remove(slot);
        }

        private static string EditTarget(string action, GameObject target)
        {
            var deconstruct = target.GetComponent<Deconstructable>();
            switch (action)
            {
                case "cancel":
                    if (target.GetComponent<Constructable>() != null || target.GetComponent<Diggable>() != null)
                    {
                        target.Trigger((int)GameHashes.Cancel, null);
                        return "OK Order cancelled";
                    }
                    if (deconstruct == null || !deconstruct.IsMarkedForDeconstruction())
                        return "ERROR No cancellable order";
                    deconstruct.CancelDeconstruction();
                    return "OK Deconstruction cancelled";
                case "deconstruct":
                    if (deconstruct == null || !deconstruct.allowDeconstruction ||
                        deconstruct.IsMarkedForDeconstruction() || target.GetComponent<BuildingComplete>() == null)
                        return "ERROR Cannot deconstruct this target";
                    deconstruct.QueueDeconstruction(false);
                    return deconstruct.IsMarkedForDeconstruction() ? "OK Deconstruction queued" : "ERROR Not queued";
                case "priority5":
                case "priority9":
                    var priority = target.GetComponent<Prioritizable>();
                    if (priority == null || !priority.IsPrioritizable()) return "ERROR Target has no active priority";
                    priority.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.basic,
                        action == "priority5" ? 5 : 9));
                    return "OK Priority changed";
                default: return "ERROR Unsupported tool";
            }
        }

        private static string Sweep(int cell)
        {
            var first = Grid.Objects[cell, (int)ObjectLayer.Pickupables];
            var pickup = first == null ? null : first.GetComponent<Pickupable>();
            int marked = 0;
            var item = pickup == null ? null : pickup.objectLayerListItem;
            while (item != null)
            {
                var obj = item.pickupable;
                item = item.nextItem;
                if (obj == null || obj.Clearable == null || !obj.Clearable.isClearable ||
                    obj.KPrefabID.HasTag(GameTags.BaseMinion)) continue;
                obj.Clearable.MarkForClear(false, false);
                marked++;
            }
            return marked > 0 ? "OK Sweep designated" : "ERROR No sweepable items";
        }

        private static string Harvest(int cell)
        {
            foreach (var plant in Components.HarvestDesignatables.Items)
            {
                if (plant == null || (Grid.PosToCell(plant) != cell &&
                    (plant.area == null || !plant.area.CheckIsOccupying(cell)))) continue;
                plant.SetHarvestWhenReady(true);
                return "OK Harvest when ready enabled";
            }
            return "ERROR No harvestable plant";
        }

        // Resolve this exact protected tool callback lazily so an update cannot break other tools.
        private static readonly Lazy<Action<MopTool, int, int>> MopDrag = new Lazy<Action<MopTool, int, int>>(() => {
            var method = AccessTools.DeclaredMethod(typeof(MopTool), "OnDragTool", new[] { typeof(int), typeof(int) });
            return method == null ? null : AccessTools.MethodDelegate<Action<MopTool, int, int>>(method);
        });

        private static string Mop(int cell)
        {
            if (MopTool.Instance == null || DebugHandler.InstantBuildMode || MopDrag.Value == null)
                return "ERROR Mopping unavailable";
            int floor = Grid.CellBelow(cell);
            if (!Grid.Element[cell].IsLiquid || Grid.Solid[cell] || Grid.Mass[cell] > MopTool.maxMopAmt ||
                !Grid.IsValidCell(floor) || !Grid.Solid[floor] || Grid.Objects[cell, (int)ObjectLayer.MopPlacer] != null)
                return "ERROR Liquid cannot be mopped";
            MopDrag.Value(MopTool.Instance, cell, 0);
            return Grid.Objects[cell, (int)ObjectLayer.MopPlacer] != null ? "OK Mopping designated" : "ERROR Not queued";
        }
    }
}
