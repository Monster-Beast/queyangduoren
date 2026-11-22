using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeEntitySampler
    {
        internal struct Candidate
        {
            internal GameObject Object;
            internal int Index, Owner;
            internal ColonyEntityKind Kind;
            internal ColonyItemPlace Place;
        }
        const byte Flip = 1, Marked = 2, Wilting = 4, HarvestReady = 8, HarvestEnabled = 16;
        static readonly AccessTools.FieldRef<Clearable, bool> MarkedForClear =
            AccessTools.FieldRefAccess<Clearable, bool>("isMarkedForClear");
        internal static bool IsMarked(Clearable clearable) => MarkedForClear(clearable);

        internal static byte[] Capture(ColonyView view, WorldContainer world,int sample=-1)
        {
            var found = new Dictionary<int, Candidate>();
            Collect(view, world, found);
            var ids = new List<int>(found.Keys);
            ids.Sort();
            int pages = Math.Max(1, (ids.Count + EntityStateCodec.MaxCount - 1) / EntityStateCodec.MaxCount);
            int pageIndex = (sample<0 ? Mathf.FloorToInt(Time.unscaledTime) : sample) % pages;
            int first = pageIndex * EntityStateCodec.MaxCount, count = Math.Min(EntityStateCodec.MaxCount, ids.Count - first);
            var page = new ColonyEntityPage { From = first == 0 ? 0 : ids[first - 1] + 1,
                Through = pageIndex == pages - 1 ? int.MaxValue : ids[first + count - 1], Entities = new ColonyEntity[count] };
            for (int i = 0; i < count; i++) page.Entities[i] = Read(found[ids[first + i]], ids[first + i], view);
            return EntityStateCodec.Encode(page);
        }

        internal static void Collect(ColonyView view, WorldContainer world, Dictionary<int, Candidate> found)
        {
            found.Clear();
            for (int i = 0; i < view.Cells.Length; i++)
            {
                if ((view.Cells[i].Flags & 1) == 0) continue;
                int x = view.X + i % ColonyView.Width, y = view.Y + i / ColonyView.Width;
                if (x >= Grid.WidthInCells || y >= Grid.HeightInCells) continue;
                int cell = Grid.XYToCell(x, y);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id) continue;
                for (var entry = NativeResources.First(cell); entry != null; entry = entry.nextItem)
                {
                    var pickup = entry.pickupable;
                    if (pickup == null || pickup.IsEntombed || pickup.storage != null || pickup.KPrefabID == null ||
                        pickup.KPrefabID.HasTag(GameTags.BaseMinion) || pickup.KPrefabID.HasTag(GameTags.Creature) ||
                        NativeResources.IsOre(pickup) || Grid.PosToCell(pickup) != cell) continue;
                    Add(found, pickup.gameObject, ItemKind(pickup), i);
                }
                var plant = Grid.Objects[cell, (int)ObjectLayer.Plants];
                if (plant == null)
                {
                    var occupant = Grid.Objects[cell,(int)ObjectLayer.Building];
                    if (occupant != null && occupant.HasTag(GameTags.Plant)) plant = occupant;
                }
                if (plant != null && Grid.PosToCell(plant) == cell) Add(found, plant, ColonyEntityKind.Plant, i);
                var c = view.Cells[i];
                if (c.BuildingHash == 0 || c.AnchorDX != 0 || c.AnchorDY != 0) continue;
                var obj = HostWorld.FindBuilding(cell);
                if (obj == null) continue;
                if (obj.GetComponent<Constructable>() != null) Add(found, obj, ColonyEntityKind.Construction, i);
                else if (obj.GetComponent<Storage>() != null) Add(found, obj, ColonyEntityKind.Storage, i);
                AddContents(found, obj, i, ColonyItemPlace.Stored);
            }
            // Several critters can occupy one cell and flying species do not need a floor.
            foreach (var brain in Components.Brains.Items)
            {
                if (!(brain is CreatureBrain) || brain == null || brain.GetComponent<Pickupable>()?.IsEntombed==true) continue;
                int cell = Grid.PosToCell(brain);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id) continue;
                Grid.CellToXY(cell, out int x, out int y);
                x -= view.X; y -= view.Y;
                if (x < 0 || y < 0 || x >= ColonyView.Width || y >= ColonyView.Height) continue;
                int i = y * ColonyView.Width + x;
                if ((view.Cells[i].Flags & 1) != 0) Add(found, brain.gameObject, ColonyEntityKind.Critter, i);
            }
            foreach (var dupe in Components.LiveMinionIdentities.Items)
            {
                if (dupe == null) continue;
                int cell = Grid.PosToCell(dupe);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id) continue;
                Grid.CellToXY(cell, out int x, out int y); x -= view.X; y -= view.Y;
                if (x < 0 || y < 0 || x >= ColonyView.Width || y >= ColonyView.Height) continue;
                int i = y * ColonyView.Width + x;
                if ((view.Cells[i].Flags & 1) == 0) continue;
                AddContents(found, dupe.gameObject, i, ColonyItemPlace.Carried);
                var equipment=dupe.GetEquipment();
                if (equipment==null) continue;
                foreach (var slot in equipment.Slots)
                    if (slot.assignable is Equippable gear && gear!=null && gear.isEquipped)
                        Add(found,gear.gameObject,ColonyEntityKind.Item,i,ColonyItemPlace.Carried,dupe.GetComponent<KPrefabID>().InstanceID);
            }
        }

        static ColonyEntityKind ItemKind(Pickupable p) => p.KPrefabID.HasTag(GameTags.Egg) ? ColonyEntityKind.Egg :
            p.GetComponent<Edible>() != null ? ColonyEntityKind.Food : ColonyEntityKind.Item;

        static void AddContents(Dictionary<int, Candidate> found, GameObject owner, int index, ColonyItemPlace place)
        {
            int ownerId = owner.GetComponent<KPrefabID>().InstanceID;
            foreach (var store in owner.GetComponents<Storage>())
                foreach (var go in store.items)
                {
                    var pickup = go == null ? null : go.GetComponent<Pickupable>();
                    if (pickup != null && pickup.KPrefabID != null && !pickup.KPrefabID.HasTag(GameTags.BaseMinion))
                        Add(found, go, ItemKind(pickup), index, place, ownerId);
                }
        }

        static void Add(Dictionary<int, Candidate> found, GameObject obj, ColonyEntityKind kind, int index,
            ColonyItemPlace place = ColonyItemPlace.Ground, int owner = -1)
        {
            var id = obj.GetComponent<KPrefabID>();
            if (id != null && id.InstanceID >= 0)
                found[id.InstanceID] = new Candidate { Kind = kind, Object = obj, Index = index, Place = place, Owner = owner };
        }

        static float Amount(float n, float fallback = 0) => float.IsNaN(n) || float.IsInfinity(n) || n < 0 ? fallback : n;
        static byte Unit(float n) => (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(Amount(n)));
        static float Freshness(GameObject obj)
        {
            var rot = obj.GetSMI<Rottable.Instance>();
            return rot == null ? -1 : Mathf.Clamp01(Amount(rot.RotConstitutionPercentage));
        }

        static ColonyEntity Read(Candidate c, int id, ColonyView view)
        {
            var obj = c.Object;
            var anim = obj.GetComponent<KBatchedAnimController>();
            var p = obj.GetComponent<PrimaryElement>();
            var pos = obj.transform.position;
            var e = new ColonyEntity { Id = id, Kind = c.Kind, Cell = (ushort)c.Index, Place = c.Place, OwnerId = c.Owner,
                Prefab = obj.GetComponent<KPrefabID>().PrefabTag.GetHash(),
                SubX = Unit(pos.x - (view.X + c.Index % ColonyView.Width)), SubY = Unit(pos.y - (view.Y + c.Index / ColonyView.Width)),
                Element = p == null ? 0 : (int)p.ElementID, Mass = p == null ? 0 : Amount(p.Mass),
                Temperature = p == null ? 0 : Amount(p.Temperature), Limit = -1, Extra = -1 };
            if (anim != null) { e.Animation = anim.currentAnim.HashValue; e.Frame = Unit(anim.GetPositionPercent()); if (anim.FlipX) e.Flags |= Flip; }
            switch (c.Kind)
            {
                case ColonyEntityKind.Item:
                case ColonyEntityKind.Food:
                case ColonyEntityKind.Egg:
                    var pickup = obj.GetComponent<Pickupable>();
                    e.Value = Amount(pickup.TotalAmount);
                    e.Extra = Amount(pickup.ReservedAmount);
                    e.Reserved = e.Extra;
                    e.Available = Amount(pickup.UnreservedAmount);
                    if (pickup.Clearable != null && MarkedForClear(pickup.Clearable)) e.Flags |= Marked;
                    if (c.Place == ColonyItemPlace.Carried && obj.GetComponent<Equippable>()?.isEquipped == true)
                    {
                        e.Flags |= ColonyEntity.Worn;
                        var owner=KPrefabIDTracker.Get().GetInstance(c.Owner);
                        if (owner!=null) NativeWearDisplay.Capture(e,owner.gameObject,obj.GetComponent<Equippable>());
                    }
                    if (c.Kind == ColonyEntityKind.Food) { e.Value = Amount(obj.GetComponent<Edible>().Calories / 1000f); e.Limit = Freshness(obj); }
                    if (c.Kind == ColonyEntityKind.Egg)
                    {
                        var egg = obj.GetSMI<IncubationMonitor.Instance>();
                        e.Value = Fraction(egg?.incubation); e.Limit = Fraction(egg?.viability); e.Extra = Fraction(egg?.wildness);
                    }
                    break;
                case ColonyEntityKind.Plant:
                    var growing = obj.GetComponent<Growing>();
                    e.Value = growing == null ? -1 : Mathf.Clamp01(Amount(growing.PercentOfCurrentHarvest()));
                    e.Limit = growing == null ? -1 : Amount(growing.TimeUntilNextHarvest(), -1);
                    var wilt = obj.GetComponent<WiltCondition>();
                    if (wilt != null && wilt.IsWilting()) e.Flags |= Wilting;
                    var harvest = obj.GetComponent<HarvestDesignatable>();
                    if (harvest != null)
                    {
                        if (harvest.CanBeHarvested()) e.Flags |= HarvestReady;
                        if (harvest.HarvestWhenReady) e.Flags |= HarvestEnabled;
                        if (harvest.MarkedForHarvest) e.Flags |= Marked;
                    }
                    break;
                case ColonyEntityKind.Critter:
                    var age = obj.GetSMI<AgeMonitor.Instance>()?.age;
                    e.Value = age == null ? -1 : Amount(age.value, -1); e.Limit = age == null ? -1 : Amount(age.GetMax(), -1);
                    var hp = obj.GetComponent<Health>();
                    if (hp != null && hp.maxHitPoints > 0) e.Extra = Mathf.Clamp01(Amount(hp.hitPoints / hp.maxHitPoints));
                    if (obj.GetComponent<KPrefabID>().HasTag(GameTags.Creatures.Wild)) e.Flags |= 32;
                    break;
                default: ReadStorage(obj, e); break;
            }
            return e;
        }

        static float Fraction(Klei.AI.AmountInstance amount) => amount == null || amount.GetMax() <= 0 ? -1 :
            Mathf.Clamp01(Amount(amount.value / amount.GetMax()));

        internal static void ReadStorage(GameObject obj, ColonyEntity e, NativeFacilityStorage.Snapshot details=null)
        {
            IStorage[] stores = details==null ? obj.GetComponents<Storage>() : obj.GetComponentsInChildren<IStorage>();
            if(details==null && e.Kind==ColonyEntityKind.Storage) e.StorageMeter=NativeFacilityStorage.SampleFoodMeter(obj);
            var seen = details==null ? null : new HashSet<GameObject>();
            var lists = details==null ? null : new HashSet<List<GameObject>>();
            IStorage washStore = details!=null && obj.GetComponent<HandSanitizer>()!=null ? obj.GetComponent<Storage>() : null;
            var totals = new Dictionary<int, ColonyIngredient>();
            float stored = 0, capacity = 0, needed = 0;
            int stacks = 0;
            foreach (var storage in stores)
            {
                if(details!=null && storage!=washStore && !storage.ShouldShowInUI()) continue;
                var items = storage.GetItems();
                if(items==null || (lists!=null && !lists.Add(items))) continue;
                float limit=details==null ? ((Storage)storage).capacityKg : storage.Capacity();
                capacity=(float)Math.Min(float.MaxValue,(double)capacity+Amount(limit,float.IsPositiveInfinity(limit)?float.MaxValue:0));
                foreach (var item in items)
                {
                    if (item == null || (seen!=null && !seen.Add(item))) continue;
                    var primary = item.GetComponent<PrimaryElement>(); var prefab = item.GetComponent<KPrefabID>();
                    if (primary == null || prefab == null) continue;
                    int hash = prefab.PrefabTag.GetHash();
                    if (!totals.TryGetValue(hash, out var row)) row = new ColonyIngredient { Tag = hash, Freshness = -1 };
                    float mass = Amount(primary.Mass); stored += mass; stacks++;
                    var pickup=item.GetComponent<Pickupable>();
                    float available=pickup!=null && pickup.TotalAmount>0 ? mass*Mathf.Clamp01(pickup.UnreservedAmount/pickup.TotalAmount) : mass;
                    e.Available+=available; e.Reserved+=mass-available;
                    details?.Add(item,primary,hash,available);
                    row.Stored += mass;
                    if (item.GetComponent<Edible>() != null)
                    {
                        float f = Freshness(item);
                        if (f >= 0 && (row.Freshness < 0 || f < row.Freshness)) row.Freshness = f;
                    }
                    totals[hash] = row;
                }
            }
            if (e.Kind == ColonyEntityKind.Construction)
            {
                var construction = obj.GetComponent<Constructable>();
                var recipe = construction.Recipe?.Ingredients;
                var chosen = construction.SelectedElementsTags;
                if (recipe != null)
                    for (int i = 0; i < recipe.Count; i++)
                    {
                        var ingredient = recipe[i];
                        int tag = chosen != null && i < chosen.Count ? chosen[i].GetHash() : ingredient.tag.GetHash();
                        if (!totals.TryGetValue(tag, out var row)) row = new ColonyIngredient { Tag = tag, Freshness = -1 };
                        float required = Amount(ingredient.amount); needed += required; row.Required += required; totals[tag] = row;
                    }
                e.Value = construction.GetWorkTime() > 0 ? Mathf.Clamp01(Amount(construction.GetPercentComplete())) : 0;
                e.Limit = stored; e.Extra = needed;
            }
            else { e.Value = stored; e.Limit = Amount(capacity); e.Extra = stacks; }
            var rows = new List<ColonyIngredient>(totals.Values);
            rows.Sort((a,b) => { int priority = b.Required.CompareTo(a.Required);
                if (priority != 0) return priority;
                int mass = b.Stored.CompareTo(a.Stored); return mass == 0 ? a.Tag.CompareTo(b.Tag) : mass; });
            e.IngredientTypes = (ushort)Math.Min(ushort.MaxValue, rows.Count);
            e.Contents = rows.GetRange(0, Math.Min(rows.Count, EntityStateCodec.MaxIngredients)).ToArray();
        }
    }
}
