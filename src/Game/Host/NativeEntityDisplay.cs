using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Queyang.Game
{
    internal sealed class NativeEntityIdentity : MonoBehaviour
    {
        internal int SaveId;
    }

    internal static class NativeEntityDisplay
    {
        sealed class Observation
        {
            internal ColonyEntity State;
            internal KSelectable Target;
            internal Guid Status;
            internal bool Proxy;
            internal int Cell;
            internal int ViewTick;
            internal float LastCritterMotion=float.NegativeInfinity;
        }

        static readonly Dictionary<int, NativeEntitySampler.Candidate> locals = new Dictionary<int, NativeEntitySampler.Candidate>();
        static readonly Dictionary<int, Observation> active = new Dictionary<int, Observation>();
        static readonly HashSet<int> present = new HashSet<int>();
        static readonly HashSet<KSelectable> hidden = new HashSet<KSelectable>();
        static readonly List<int> expired = new List<int>();
        static readonly Dictionary<int, Cargo> cargo = new Dictionary<int, Cargo>();
        static StatusItem status, cargoStatus;

        sealed class Cargo
        {
            internal readonly List<ColonyEntity> Items = new List<ColonyEntity>();
            internal Guid Status;
            internal KSelectable Carrier;
            internal NativeWearDisplay Clothing;
        }

        internal static void Apply(ColonyView view, WorldContainer world, Transform parent)
        {
            var page = EntityStateCodec.Decode(view.EntityPayload);
            NativeEntitySampler.Collect(view, world, locals);
            present.Clear();
            foreach (var e in page.Entities)
            {
                if ((view.Cells[e.Cell].Flags & 1) == 0) throw new InvalidDataException("Entity in an unseen cell.");
                int x = view.X + e.Cell % ColonyView.Width, y = view.Y + e.Cell / ColonyView.Width;
                if (x >= Grid.WidthInCells || y >= Grid.HeightInCells) continue;
                int cell = Grid.XYToCell(x, y);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id) continue;
                present.Add(e.Id);
                if (e.Kind==ColonyEntityKind.Item && !e.IsWorn) NativeWearDisplay.HideBaseline(e.Id);
                if (e.Place != ColonyItemPlace.Ground)
                {
                    if (active.TryGetValue(e.Id, out var previous) && previous.State.Place == ColonyItemPlace.Ground) Forget(e.Id);
                    var floor = Original(e);
                    if (floor != null) SetVisible(floor, false);
                    if (!active.TryGetValue(e.Id, out var stored)) active.Add(e.Id, stored = new Observation());
                    stored.State = e; stored.Cell = cell;
                    continue;
                }
                bool building = e.Kind == ColonyEntityKind.Construction || e.Kind == ColonyEntityKind.Storage;
                KSelectable target = building ? NativeWorldMirror.GetBuildingSelectable(cell, e.Id) : Original(e);
                bool proxy = false;
                if (active.TryGetValue(e.Id, out var item) && item.State.Prefab == e.Prefab && item.State.Kind == e.Kind &&
                    item.Proxy && item.Target != null && target == null) { target = item.Target; proxy = true; }
                if (target == null && !building)
                {
                    target = Spawn(e, cell, parent);
                    proxy = target != null;
                }
                if (target == null) { Forget(e.Id); continue; }
                if (item == null || item.Target != target)
                {
                    Forget(e.Id);
                    item = new Observation { Target = target, Proxy = proxy, State = e };
                    if (status == null)
                    {
                        status = new StatusItem("QueyangHostEntityState", "房主状态", "房主状态", "", StatusItem.IconType.Info,
                            NotificationType.Neutral, false, default(HashedString), showWorldIcon: false);
                        status.resolveStringCallback = (_, data) => Describe(((Observation)data).State, false);
                        status.resolveTooltipCallback = (_, data) => Describe(((Observation)data).State, true);
                    }
                    item.Status = target.AddStatusItem(status, item);
                    active[e.Id] = item;
                }
                item.State = e;
                bool tracked=e.Kind==ColonyEntityKind.Critter && NativeDupePose.Knows(e.Id);
                if(!tracked || NativeDupePose.Current(e.Id,view.DupePoseTick)) item.Cell = cell;
                item.ViewTick=Math.Max(item.ViewTick,view.DupePoseTick);
                if (building) { NativeFacilityStorage.ApplyMeter(target,e); continue; }
                SetVisible(target, true);
                NativeWorldMirror.ApplyPrimary(target.GetComponent<PrimaryElement>(),
                    new MaterialSample { Element = e.Element, Mass = e.Mass, Temperature = e.Temperature });
                if(tracked)
                {
                    if(Time.unscaledTime-item.LastCritterMotion<.55f || !NativeDupePose.Current(e.Id,view.DupePoseTick)) continue;
                    NativeDupePose.Forget(e.Id,view.DupePoseTick);
                }
                float px = x + e.SubX / 255f, py = y + e.SubY / 255f;
                var t = target.transform;
                var pos = t.position;
                if (pos.x != px || pos.y != py) t.position = new Vector3(px, py, pos.z);
                var anim = target.GetComponent<KBatchedAnimController>();
                var name = new HashedString(e.Animation);
                if (anim != null)
                {
                    anim.FlipX = (e.Flags & 1) != 0;
                    if (e.Animation != 0 && anim.HasAnimation(name))
                    {
                        if (anim.currentAnim != name) anim.Play(name);
                        anim.SetPositionPercent(e.Frame / 255f);
                    }
                    anim.SetDirty();
                }
            }
            foreach (var pair in locals)
                if (pair.Key >= page.From && pair.Key <= page.Through && !present.Contains(pair.Key) &&
                    pair.Value.Kind != ColonyEntityKind.Storage && pair.Value.Kind != ColonyEntityKind.Construction &&
                    (pair.Value.Kind!=ColonyEntityKind.Critter || NativeDupePose.Current(pair.Key,view.DupePoseTick)))
                {
                    if (pair.Value.Place==ColonyItemPlace.Carried && pair.Value.Kind==ColonyEntityKind.Item)
                        NativeWearDisplay.HideBaseline(pair.Key);
                    SetVisible(pair.Value.Object.GetComponent<KSelectable>(), false);
                }
            expired.Clear();
            foreach (var pair in active)
            {
                if (present.Contains(pair.Key) || pair.Key < page.From || pair.Key > page.Through) continue;
                if(pair.Value.State.Kind==ColonyEntityKind.Critter && !NativeDupePose.Current(pair.Key,view.DupePoseTick)) continue;
                var xy = Grid.CellToXY(pair.Value.Cell);
                int x = xy.x - view.X, y = xy.y - view.Y;
                if (x >= 0 && x < ColonyView.Width && y >= 0 && y < ColonyView.Height &&
                    (view.Cells[y * ColonyView.Width + x].Flags & 1) != 0) expired.Add(pair.Key);
            }
            foreach (int id in expired)
            {
                var old = active[id];
                if (!old.Proxy && old.Target != null && old.State.Kind != ColonyEntityKind.Storage &&
                    old.State.Kind != ColonyEntityKind.Construction) SetVisible(old.Target, false);
                Forget(id);
            }
            UpdateCargo();
        }

        static KSelectable Original(ColonyEntity e)
        {
            var original = KPrefabIDTracker.Get().GetInstance(e.Id);
            if (original == null || original.PrefabTag.GetHash() != e.Prefab) return null;
            var pickup = original.GetComponent<Pickupable>();
            if (pickup != null && (pickup.storage != null || pickup.IsEntombed)) return null;
            if (e.Kind==ColonyEntityKind.Item && original.GetComponent<Equippable>()?.isEquipped==true) return null;
            return original.GetComponent<KSelectable>();
        }

        static KSelectable Spawn(ColonyEntity e, int cell, Transform parent)
        {
            var prefab = Assets.TryGetPrefab(new Tag(e.Prefab));
            var source = prefab == null ? null : prefab.GetComponent<KBatchedAnimController>();
            if (source == null) return null;
            var tag = prefab.GetComponent<KPrefabID>().PrefabTag;
            string label = prefab.GetComponent<KSelectable>()?.GetName() ?? tag.Name;
            var obj = new GameObject(label);
            obj.SetActive(false); obj.layer = prefab.layer;
            obj.transform.SetParent(parent, false);
            obj.transform.position = Grid.CellToPosCBC(cell, source.sceneLayer);
            obj.AddComponent<KPrefabID>().PrefabTag = tag;
            obj.AddComponent<NativeEntityIdentity>().SaveId=e.Id;
            var anim = obj.AddComponent<KBatchedAnimController>();
            anim.AnimFiles = source.AnimFiles; anim.sceneLayer = source.sceneLayer; anim.fgLayer = source.fgLayer;
            anim.initialAnim = source.initialAnim; anim.defaultAnim = source.defaultAnim;
            anim.animScale = source.animScale; anim.materialType = source.materialType; anim.Offset = source.Offset;
            anim.isMovable = e.Kind != ColonyEntityKind.Plant;
            if(e.Kind==ColonyEntityKind.Critter) SymbolOverrideControllerUtil.AddToPrefab(obj);
            var shape = obj.AddComponent<KBoxCollider2D>();
            var baseShape = prefab.GetComponent<KBoxCollider2D>();
            shape.size = baseShape == null ? Vector2.one : baseShape.size;
            shape.offset = baseShape == null ? new Vector2(0,.4f) : baseShape.offset;
            var select = obj.AddComponent<KSelectable>(); select.SetName(label);
            var element = e.Element == 0 ? null : ElementLoader.FindElementByHash((SimHashes)e.Element);
            if (element != null)
            {
                var material = obj.AddComponent<PrimaryElement>();
                material.SetElement(element.id, false); material.KeepZeroMassObject = true;
            }
            obj.SetActive(true);
            return select;
        }

        static string Describe(ColonyEntity e, bool details)
        {
            var culture = CultureInfo.InvariantCulture;
            string output;
            switch (e.Kind)
            {
                case ColonyEntityKind.Food:
                    output = string.Format(culture, "食物 {0:0.##} kcal", e.Value);
                    if (e.Limit >= 0) output += string.Format(culture, "，新鲜度 {0:P0}", e.Limit);
                    break;
                case ColonyEntityKind.Plant:
                    output = e.Value < 0 ? "植物" : string.Format(culture, "生长 {0:P0}", e.Value);
                    if ((e.Flags & 4) != 0) output += "，萎蔫";
                    else if ((e.Flags & 8) != 0) output += "，可收获";
                    else if (e.Limit >= 0) output += string.Format(culture, "，距收获 {0:0} 秒", e.Limit);
                    if ((e.Flags & 16) != 0) output += "，允许收获";
                    if ((e.Flags & 2) != 0) output += "，收获已排队";
                    break;
                case ColonyEntityKind.Critter:
                    output = e.Value < 0 ? "动物" : string.Format(culture, "年龄 {0:0.0} / {1:0.#} 周期", e.Value, e.Limit);
                    if (e.Extra >= 0) output += string.Format(culture, "，生命 {0:P0}", e.Extra);
                    if ((e.Flags & 32) != 0) output += "，野生";
                    break;
                case ColonyEntityKind.Egg:
                    output = e.Value < 0 ? "蛋" : string.Format(culture, "孵化 {0:P0}", e.Value);
                    if (e.Limit >= 0) output += string.Format(culture, "，活性 {0:P0}", e.Limit);
                    break;
                case ColonyEntityKind.Construction:
                    output = string.Format(culture, "施工 {0:P0}，已运抵 {1:0.##} / {2:0.##} kg", e.Value, e.Limit, e.Extra);
                    break;
                case ColonyEntityKind.Storage:
                    output = string.Format(culture, "储存 {0:0.##} kg，{1:0} 堆", e.Value, e.Extra);
                    if (e.Limit < 1e10f) output += string.Format(culture, "，容量 {0:0.##} kg", e.Limit);
                    break;
                default:
                    output = string.Format(culture, "物品 {0:0.###} kg", e.Mass); break;
            }
            if (e.Kind == ColonyEntityKind.Item || e.Kind == ColonyEntityKind.Food || e.Kind == ColonyEntityKind.Egg)
            {
                if ((e.Flags & 2) != 0) output += "，已标记清扫";
                if (e.Reserved > 0) output += string.Format(culture, "，已预留 {0:0.###} 单位", e.Reserved);
            }
            if (!details || e.Contents.Length == 0) return output;
            var text = new StringBuilder(output);
            foreach (var row in e.Contents)
            {
                string name = TagManager.GetProperName(new Tag(row.Tag));
                text.AppendLine().Append(name).Append(' ').Append(row.Stored.ToString("0.###", culture));
                if (row.Required > 0) text.Append(" / ").Append(row.Required.ToString("0.###", culture));
                text.Append(" kg");
                if (row.Freshness >= 0) text.Append("，最低新鲜度 ").Append(row.Freshness.ToString("P0", culture));
            }
            if (e.IngredientTypes > e.Contents.Length) text.AppendLine().Append("共 ").Append(e.IngredientTypes).Append(" 种材料");
            return text.ToString();
        }

        static void UpdateCargo()
        {
            foreach (var c in cargo.Values) c.Items.Clear();
            foreach (var observation in active.Values)
            {
                var e = observation.State;
                if (e.Place != ColonyItemPlace.Carried) continue;
                if (!cargo.TryGetValue(e.OwnerId, out var c))
                {
                    var carrier = NativeWorldMirror.GetDupeSelectable(e.OwnerId);
                    if (carrier == null) continue;
                    c = new Cargo { Carrier = carrier, Clothing = new NativeWearDisplay(carrier) };
                    if (cargoStatus == null)
                    {
                        cargoStatus = new StatusItem("QueyangHostCargo", "携带物和装备", "携带物和装备", "", StatusItem.IconType.Info,
                            NotificationType.Neutral, false, default(HashedString), showWorldIcon: false);
                        cargoStatus.resolveStringCallback = (_, data) => "携带物和装备 " + ((Cargo)data).Items.Count + " 项";
                        cargoStatus.resolveTooltipCallback = (_, data) => CargoText((Cargo)data);
                    }
                    cargo.Add(e.OwnerId, c);
                    c.Status = carrier.AddStatusItem(cargoStatus, c);
                }
                c.Items.Add(e);
            }
            expired.Clear();
            foreach (var pair in cargo)
            {
                pair.Value.Clothing.Apply(pair.Value.Items);
                if (pair.Value.Carrier == null || pair.Value.Items.Count == 0) expired.Add(pair.Key);
            }
            foreach (int id in expired)
            {
                var c = cargo[id];
                if (c.Carrier != null) c.Carrier.RemoveStatusItem(c.Status);
                cargo.Remove(id);
            }
        }

        static string CargoText(Cargo c)
        {
            var text = new StringBuilder();
            foreach (var e in c.Items)
            {
                if (text.Length > 0) text.AppendLine();
                if (e.Kind == ColonyEntityKind.Item && (e.Flags & ColonyEntity.Worn) != 0) text.Append("穿戴：");
                text.Append(TagManager.GetProperName(new Tag(e.Prefab))).Append(' ')
                    .Append(e.Mass.ToString("0.###", CultureInfo.InvariantCulture)).Append(" kg");
            }
            return text.ToString();
        }

        static void SetVisible(KSelectable select, bool visible)
        {
            if (select == null || !(visible ? hidden.Remove(select) : hidden.Add(select))) return;
            if (!visible && select.IsSelected) NativeWorldMirror.ClearSelection(select.gameObject);
            select.IsSelectable = visible; select.GetComponent<KBatchedAnimController>()?.SetVisiblity(visible);
        }
        static void Forget(int id)
        {
            if (!active.TryGetValue(id, out var old)) return;
            active.Remove(id);
            if(old.State.Kind==ColonyEntityKind.Critter) NativeDupePose.Forget(id,0);
            if (old.Target == null) return;
            old.Target.RemoveStatusItem(old.Status);
            if (old.Proxy) NativeWorldMirror.DestroySelectableProxy(old.Target.gameObject);
        }

        internal static KSelectable GetSelectable(int hostId) => active.TryGetValue(hostId, out var s) ? s.Target : null;
        internal static KBatchedAnimController MovingCritter(int id,int tick,int cell)
        {
            if(!active.TryGetValue(id,out var s) || s.State.Kind!=ColonyEntityKind.Critter ||
                !NativeDupePose.Current(id,tick) || tick<s.ViewTick || s.Target==null || !s.Target.IsSelectable) return null;
            s.Cell=cell;
            s.LastCritterMotion=Time.unscaledTime;
            return s.Target.GetComponent<KBatchedAnimController>();
        }
        internal static ColonyEntity GetObservedState(int hostId) => active.TryGetValue(hostId, out var s) ? s.State : null;
        internal static void Reset()
        {
            expired.Clear(); expired.AddRange(active.Keys);
            foreach (int id in expired) Forget(id);
            foreach (var c in cargo.Values)
                if (c.Carrier != null) { c.Clothing.Apply(null); c.Carrier.RemoveStatusItem(c.Status); }
            cargo.Clear();
            NativeWearDisplay.RestoreBaseline();
            expired.Clear(); locals.Clear(); hidden.Clear(); present.Clear();
        }
    }
}
