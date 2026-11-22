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

        public static byte[] Capture(int x, int y) => CapturePage(x,y,-1);

        internal static Vector2I ViewOrigin(WorldContainer world, int x, int y)
        {
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
            return new Vector2I(Math.Max(origin.x, Math.Min(origin.x + size.x - ColonyView.Width, x)),
                Math.Max(origin.y, Math.Min(origin.y + size.y - ColonyView.Height, y)));
        }

        internal static byte[] CapturePage(int x, int y, int sample)
        {
            var world = World;
            if (world == null || Grid.WidthInCells == 0)
                throw new InvalidOperationException("The host colony has not loaded.");
            var origin = ViewOrigin(world, x, y);
            var view = new ColonyView {
                Native = true, Detailed = true, HasResources = true, HasEnvironment = true,
                X = origin.x, Y = origin.y
            };
            view.EnvironmentPage = sample>=0 ? sample%4 : NativeEnvironment.NextPage(world, view.X, view.Y);
            var clock = GameClock.Instance;
            var speed = SpeedControlScreen.Instance;
            view.Cycle = clock.GetCycle() + 1;
            view.CycleFraction = Mathf.Clamp01(clock.GetCurrentCycleAsPercentage());
            view.Paused = speed.IsPaused;
            float selectedSpeed = speed.GetSpeed() == 0 ? speed.normalSpeed :
                speed.GetSpeed() == 1 ? speed.fastSpeed : speed.ultraSpeed;
            view.Speed = (byte)Mathf.Clamp(Mathf.RoundToInt(selectedSpeed), 0, 10);
            var circuits = new Dictionary<ushort, ColonyCell>();
            var buried=global::Game.Instance.GetComponent<EntombedItemVisualizer>();
            view.HasEntombed=buried!=null;
            for (int dy = 0; dy < ColonyView.Height; dy++)
            {
                for (int dx = 0; dx < ColonyView.Width; dx++)
                {
                    int cell;
                    if (VisibleCell(world, view.X + dx, view.Y + dy, out cell))
                    {
                        var sampleCell=ReadCell(cell,circuits);
                        if(buried!=null && (sampleCell.Flags&2)!=0) NativeBuriedVisuals.Capture(buried,cell,ref sampleCell);
                        view.Cells[dy * ColonyView.Width + dx]=sampleCell;
                    }
                }
            }
            var dupes = new List<ColonyDupe>();
            view.DupePoseTick=Math.Max(1,Time.frameCount);
            foreach (var dupe in Components.LiveMinionIdentities.Items)
            {
                if (dupe == null) continue;
                // The KMonoBehaviour overload may return a pending navigation cell. Use
                // the same rendered root for both tile coverage and the sub-cell pose.
                var pos = dupe.transform.position;
                int cell = Grid.PosToCell(pos);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id || Grid.Visible[cell] == 0)
                    continue;
                int dupeX, dupeY;
                Grid.CellToXY(cell, out dupeX, out dupeY);
                if (dupeX < view.X || dupeY < view.Y || dupeX >= view.X + ColonyView.Width ||
                    dupeY >= view.Y + ColonyView.Height) continue;
                var driver = dupe.GetComponent<ChoreDriver>();
                var chore = driver == null ? null : driver.GetCurrentChore();
                var anim = dupe.GetComponent<KBatchedAnimController>();
                var d=new ColonyDupe {
                    SaveId = dupe.GetComponent<KPrefabID>().InstanceID,
                    Personality = dupe.personalityResourceId.HashValue,
                    Animation = anim == null ? 0 : anim.currentAnim.HashValue,
                    AnimationPosition = anim == null ? (byte)0 :
                        (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(anim.GetPositionPercent())),
                    FlipX = anim != null && anim.FlipX,
                    Pose = NativeDupePose.Capture(anim,pos),
                    X = dupeX, Y = dupeY, Name = dupe.GetProperName(),
                    Id = TargetKeys.GetValue(dupe.gameObject, CreateKey).Value,
                    SubX = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(pos.x - dupeX)),
                    SubY = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(pos.y - dupeY)),
                    Activity = chore == null ? "Idle" : chore.choreType.Id
                };
                NativeDupeAppearance.Capture(anim,ref d);
                d.HealthFill=NativeDupeHealth.Capture(anim);
                dupes.Add(d);
            }
            view.SetDupePage(dupes,sample>=0 ? sample : Mathf.FloorToInt(Time.unscaledTime));
            view.HasDupeAppearance=view.Dupes.Length>0;
            NativeResources.Capture(view, world,sample);
            NativeManagement.CaptureHud(view,world);
            NativeColonyLighting.Capture(view,world,sample);
            NativeBuildingMeters.Capture(view,sample);
            NativeBuildingWork.Capture(view,world,sample);
            NativeBuildingWarnings.Capture(view,sample);
            if(view.LightMask!=null) view.HostAlerts=NativeAlerts.Capture(world);
            view.HasHealthBars=view.LightMask!=null && view.Dupes.Length>0;
            view.HasPileAppearance=view.HasEntombed && view.HasColonyHud;
            OrderOwnership.Capture(view,sample);
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

        internal static GameObject FindBuilding(int cell)
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
            return FindBuilding(cell) ?? dig ?? SecondaryTarget(cell);
        }

        static GameObject SecondaryTarget(int cell)
        {
            var mop = Grid.Objects[cell, (int)ObjectLayer.MopPlacer];
            if (mop != null) return mop;
            var plant = Grid.Objects[cell, (int)ObjectLayer.Plants]?.GetComponent<HarvestDesignatable>() ??
                Grid.Objects[cell,(int)ObjectLayer.Building]?.GetComponent<HarvestDesignatable>();
            if (plant != null && plant.HarvestWhenReady) return plant.gameObject;
            var pile = Grid.Objects[cell, (int)ObjectLayer.Pickupables]?.GetComponent<Pickupable>()?.objectLayerListItem;
            for (; pile != null; pile = pile.nextItem)
                if (pile.pickupable?.Clearable != null && NativeEntitySampler.IsMarked(pile.pickupable.Clearable))
                    return pile.pickupable.gameObject;
            return null;
        }

        private static ColonyCell ReadCell(int cell, Dictionary<ushort, ColonyCell> circuits)
        {
            var element = Grid.Element[cell];
            var building = FindBuilding(cell);
            var dig = Grid.Objects[cell, (int)ObjectLayer.DigPlacer];
            var order = dig ?? SecondaryTarget(cell);
            var target = building ?? order;
            var result = new ColonyCell {
                ElementHash = (int)element.id, Visibility = Grid.Visible[cell], RenderGround = Grid.RenderedByWorld[cell],
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
            if (dig != null)
            {
                result.Flags |= 8;
                NativeOrderColors.CaptureDig(dig.GetComponent<Diggable>(), ref result);
            }
            result.MopOrdered = Grid.Objects[cell, (int)ObjectLayer.MopPlacer] != null;
            result.OrderPriority = ReadPriority(target);
            if (order != null && order != target)
            {
                result.OrderTargetId = TargetKeys.GetValue(order,CreateKey).Value;
                result.SeparateOrderPriority = ReadPriority(order);
            }
            if (building != null)
            {
                var item = building.GetComponent<Building>();
                result.BuildingHash = new HashedString(item.Def.PrefabID).HashValue;
                var offset = Grid.CellToXY(item.GetCell()) - Grid.CellToXY(cell);
                result.AnchorDX = (sbyte)Mathf.Clamp(offset.x, -127, 127);
                result.AnchorDY = (sbyte)Mathf.Clamp(offset.y, -127, 127);
                if (offset.x == 0 && offset.y == 0)
                {
                    result.BuildingSaveId = building.GetComponent<KPrefabID>().InstanceID;
                    var primary = building.GetComponent<PrimaryElement>();
                    if (primary != null)
                        result.Structure = NativeMaterialReadings.Capture(primary);
                    var anim = building.GetComponent<KBatchedAnimController>();
                    if (anim != null)
                    {
                        if (building.GetComponent<Constructable>() != null)
                        {
                            result.HasBlueprintTint=true;
                            result.BlueprintTint=NativeOrderColors.Pack(anim.TintColour);
                        }
                        result.BuildingAnimation = anim.currentAnim.HashValue;
                        result.BuildingFrame = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(anim.GetPositionPercent()));
                    }
                }
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
            ReadUtilityValues(ref result, cell, circuits);
            NativeEnvironment.Capture(cell, ref result);
            return result;
        }

        private static TargetKey CreateKey(GameObject obj) => new TargetKey(obj);

        static byte ReadPriority(GameObject target)
        {
            var priority=target==null ? null : target.GetComponent<Prioritizable>();
            if(priority==null || !priority.IsPrioritizable()) return 0;
            var value=priority.GetMasterPriority();
            if(value.priority_class==PriorityScreen.PriorityClass.topPriority) return 10;
            return value.priority_class==PriorityScreen.PriorityClass.basic ? (byte)Mathf.Clamp(value.priority_value,1,9) : (byte)0;
        }

        public static string Execute(string action, int x, int y)
        {
            int cell;
            var world = World;
            if (world != null && NativeManagement.TryExecute(action,x,y,out string nativeResult)) return nativeResult;
            if (world != null && action != null && action.StartsWith("manage/", StringComparison.Ordinal))
                return ColonyManagement.Execute(action);
            if (world != null && action != null && action.Length <= 96 && action.StartsWith("buildOptions/", StringComparison.Ordinal))
                return Options(action, world);
            if (action == "overview" && world != null) return Overview(world);
            if(world!=null && action!=null && action.StartsWith("digspan/",StringComparison.Ordinal))
                return DigSpan(action.Substring(8),x,y,world,false);
            if(world!=null && action!=null && action.StartsWith("urgent-digspan/",StringComparison.Ordinal))
                return DigSpan(action.Substring(15),x,y,world,true);
            if (world == null || !VisibleCell(world, x, y, out cell) || !Grid.IsValidBuildingCell(cell))
                return "ERROR Cell unavailable";
            if (action != null && action.Length <= 512 && action.StartsWith("plan/",StringComparison.Ordinal))
                return PlaceNativePlan(action,cell,world);
            if (string.IsNullOrEmpty(action) || action.Length > 96) return "ERROR Unsupported tool";
            if (action.StartsWith("object/", StringComparison.Ordinal))
            {
                int end = action.IndexOf('/', 7);
                if (end < 0 || !int.TryParse(action.Substring(7, end - 7), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int id) || id <= 0) return "ERROR Invalid selected object";
                var selected = KPrefabIDTracker.Get().GetInstance(id);
                if (selected == null || selected.GetComponent<Pickupable>()?.storage != null) return "ERROR Selected object changed";
                var b = selected.GetComponent<Building>();
                if ((b != null ? b.GetCell() : Grid.PosToCell(selected)) != cell) return "ERROR Selected object moved";
                string edit = action.Substring(end + 1);
                if (edit == "sweep")
                {
                    var pickup = selected.GetComponent<Pickupable>();
                    if (pickup?.Clearable == null || !pickup.Clearable.isClearable || selected.HasTag(GameTags.BaseMinion))
                        return "ERROR Selected object cannot be swept";
                    pickup.Clearable.MarkForClear(false, false);
                    return "OK Sweep designated";
                }
                int tokenStart = edit.IndexOf(':');
                if (tokenStart < 1 || !int.TryParse(edit.Substring(tokenStart + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int token) || !TargetKeys.TryGetValue(selected.gameObject, out var current)
                    || current.Value != token) return "ERROR Selected order changed";
                edit = edit.Substring(0, tokenStart);
                if (edit != "cancel" && !(edit.StartsWith("priority", StringComparison.Ordinal) && edit.Length == 11))
                    return "ERROR Unsupported object tool";
                return EditChecked(edit, selected.gameObject);
            }
            if (action.StartsWith("build/", StringComparison.Ordinal)) return PlaceSelected(action, cell, world);
            if (action.StartsWith("building/", StringComparison.Ordinal) || action.StartsWith("b/", StringComparison.Ordinal))
                return BuildingOptions(action, cell, x, y);
            switch (action)
            {
                case "inspect": return Inspect(cell);
                case "dig":
                    return DigCell(cell);
                case "urgent-dig":
                    return DigCell(cell,true);
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
            var requested=action.Substring(0,colon);
            if (target != null && TargetKeys.TryGetValue(target, out key) && key.Value == expectedId)
                return EditChecked(requested,target);
            if (requested=="cancel" || requested.StartsWith("priority",StringComparison.Ordinal))
            {
                var order=Grid.Objects[cell,(int)ObjectLayer.DigPlacer] ?? SecondaryTarget(cell);
                if (order!=null && TargetKeys.TryGetValue(order,out key) && key.Value==expectedId)
                    return EditChecked(requested,order);
            }
            return "ERROR The selected order or building has changed";
        }

        static string DigCell(int cell,bool urgent=false)
        {
            if(DigTool.Instance==null || !Grid.Solid[cell] || !Diggable.IsDiggable(cell) ||
                Diggable.Undiggable(Grid.Element[cell]) || Diggable.GetDiggable(cell)!=null)
                return "ERROR Cell cannot be designated for digging";
            var order=DigTool.PlaceDig(cell,0);
            if(order==null) return "ERROR Dig tool declined";
            if(urgent) order.GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority,1));
            return "OK Dig designated";
        }

        static string DigSpan(string size,int x,int y,WorldContainer world,bool urgent)
        {
            if(size.Length>2 || !int.TryParse(size,NumberStyles.None,CultureInfo.InvariantCulture,out int length) ||
                length<2 || length>64 || x<0 || x>Grid.WidthInCells-length)
                return "ERROR Invalid excavation row";
            int accepted=0;
            for(int offset=0;offset<length;offset++)
                if(VisibleCell(world,x+offset,y,out int cell) && Grid.IsValidBuildingCell(cell) &&
                   DigCell(cell,urgent)=="OK Dig designated") accepted++;
            return FormattableString.Invariant($"{(accepted>0?"OK":"ERROR")} Area excavation designated {accepted}/{length}");
        }

        private static string EditChecked(string edit, GameObject target)
        {
            if (edit.StartsWith("priority", StringComparison.Ordinal) && edit.Length > 9)
            {
                if (edit.Length != 11 || edit[9] != '/') return "ERROR Order priority changed";
                int expected=edit[10]=='!'?10:edit[10]-'0';
                if(expected<1 || expected>10 || ReadPriority(target)!=expected) return "ERROR Order priority changed";
                edit = edit.Substring(0, 9);
            }
            return EditTarget(edit, target);
        }

        private static string Place(string prefab, int cell, WorldContainer world)
        {
            GameObject order;
            return Place(prefab, cell, world, out order);
        }

        private static string Place(string prefab, int cell, WorldContainer world, out GameObject order,
            Orientation orientation = Orientation.Neutral, string primaryMaterial = null, string[] specified = null)
        {
            order = null;
            var def = Assets.GetBuildingDef(prefab);
            if (def == null || PlanScreen.Instance == null || !PlanScreen.Instance.IsDefBuildable(def))
                return "ERROR Building locked or unavailable";
            if (specified != null && def.MaterialCategory.Length != specified.Length)
                return "ERROR Construction materials changed";
            if (!VacantFootprint(def,cell,orientation,world)) return "ERROR Footprint hidden or occupied";
            var materials = new List<Tag>();
            for (int i = 0; i < def.MaterialCategory.Length; i++)
            {
                foreach (Tag tag in MaterialSelector.GetValidMaterials(def.MaterialCategory[i], true))
                {
                    if (i == 0 && primaryMaterial != null && tag.ToString() != primaryMaterial) continue;
                    if (specified != null && tag.ToString() != specified[i]) continue;
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
            // The TryPlace postfix also reserves local host orders in the same frame.
            if (!CoopSession.IsHosting) ReservePlacement(def,cell,orientation,order);
            return "OK Construction planned";
        }

        internal static bool VacantFootprint(BuildingDef def,int cell,Orientation orientation,WorldContainer world)
        {
            if (world==null || def==null || !Grid.IsValidCell(cell)) return false;
            var pending=Pending.GetOrCreateValue(world);
            ReleaseRegistered(pending);
            Grid.CellToXY(cell,out int x,out int y);
            foreach (var original in def.PlacementOffsets)
            {
                var offset=Rotatable.GetRotatedCellOffset(original,orientation);
                if (!VisibleCell(world,x+offset.x,y+offset.y,out int occupied) ||
                    Grid.Objects[occupied,(int)def.ObjectLayer]!=null || pending.ContainsKey(Slot(occupied,def.ObjectLayer))) return false;
            }
            return true;
        }

        internal static bool PendingPlacement(BuildingDef def,int cell,Orientation orientation)
        {
            var world=ClusterManager.Instance?.activeWorld;
            if (world==null || !Grid.IsValidCell(cell) || !Pending.TryGetValue(world,out var pending)) return false;
            ReleaseRegistered(pending);
            if (pending.Count==0) return false;
            foreach (var original in def.PlacementOffsets)
            {
                var offset=Rotatable.GetRotatedCellOffset(original,orientation);
                if (Grid.IsCellOffsetValid(cell,offset) && pending.ContainsKey(Slot(Grid.OffsetCell(cell,offset),def.ObjectLayer))) return true;
            }
            return false;
        }

        internal static void ReservePlacement(BuildingDef def,int cell,Orientation orientation,GameObject order)
        {
            var world=ClusterManager.Instance?.activeWorld;
            if (world==null || !Grid.IsValidCell(cell)) return;
            var pending=Pending.GetOrCreateValue(world);
            foreach (var original in def.PlacementOffsets)
            {
                var offset=Rotatable.GetRotatedCellOffset(original,orientation);
                if (!Grid.IsCellOffsetValid(cell,offset)) continue;
                int occupied=Grid.OffsetCell(cell,offset);
                if (Grid.Objects[occupied,(int)def.ObjectLayer]==null) pending[Slot(occupied,def.ObjectLayer)]=order;
            }
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
            if (action.Length == 9 && action.StartsWith("priority", StringComparison.Ordinal) &&
                (action[8]=='!' || action[8] >= '1' && action[8] <= '9'))
            {
                var priority = target.GetComponent<Prioritizable>();
                if (priority == null || !priority.IsPrioritizable()) return "ERROR Target has no active priority";
                bool urgent=action[8]=='!';
                priority.SetMasterPriority(new PrioritySetting(urgent?PriorityScreen.PriorityClass.topPriority:PriorityScreen.PriorityClass.basic,
                    urgent?1:action[8]-'0'));
                return "OK Priority changed";
            }
            var deconstruct = target.GetComponent<Deconstructable>();
            switch (action)
            {
                case "cancel":
                    var sweep = target.GetComponent<Clearable>();
                    if (sweep != null && NativeEntitySampler.IsMarked(sweep))
                    {
                        sweep.CancelClearing();
                        TargetKeys.Remove(target);
                        return "OK Sweep cancelled";
                    }
                    var harvest = target.GetComponent<HarvestDesignatable>();
                    if (harvest != null && harvest.HarvestWhenReady)
                    {
                        harvest.SetHarvestWhenReady(false);
                        TargetKeys.Remove(target);
                        return "OK Harvest cancelled";
                    }
                    if (target.GetComponent<Moppable>() != null) { target.Trigger((int)GameHashes.Cancel, null); return "OK Mop cancelled"; }
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
