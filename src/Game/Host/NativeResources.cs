using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Queyang.Game
{
    internal static class NativeResources
    {
        static WorldContainer stockWorld;
        static float nextStockSample, nextHudRefresh;
        static readonly List<ColonyStock> hostStocks = new List<ColonyStock>();
        static readonly Dictionary<int, Tag> knownTags = new Dictionary<int, Tag>();
        static WorldInventory guestInventory;
        static long stockThrough;
        static readonly AccessTools.FieldRef<WorldInventory, bool> CountReady =
            AccessTools.FieldRefAccess<WorldInventory, bool>("hasValidCount");
        static readonly List<Tag> cleared = new List<Tag>();
        static readonly HashSet<int> observed = new HashSet<int>();
        static readonly List<int> removed = new List<int>();
        static readonly Dictionary<int, PileVisual> proxies = new Dictionary<int, PileVisual>();
        static readonly HashSet<Pickupable> hidden = new HashSet<Pickupable>();

        sealed class PileVisual
        {
            internal int Cell;
            internal PrimaryElement Primary;
            internal KBatchedAnimController Anim;
        }

        internal static ObjectLayerListItem First(int cell)
        {
            var first = Grid.Objects[cell, (int)ObjectLayer.Pickupables];
            return first == null ? null : first.GetComponent<Pickupable>()?.objectLayerListItem;
        }

        internal static bool IsOre(Pickupable pickup)
        {
            if (pickup == null || pickup.storage != null || pickup.KPrefabID == null) return false;
            var e = pickup.PrimaryElement?.Element;
            // Vanilla's CreateOreEntity sets PrefabTag to the exact element tag. A creature,
            // ore-based tool or packaged meal can have the same PrimaryElement.
            return e != null && e.IsSolid && pickup.KPrefabID.PrefabTag == e.tag;
        }

        internal static void Capture(ColonyView view, WorldContainer world, int sample=-1)
        {
            int sequence=sample<0 ? Mathf.FloorToInt(Time.unscaledTime) : sample;
            view.EntityPayload = NativeEntitySampler.Capture(view, world,sequence);
            var piles = new List<ColonyPile>();
            var ids = new HashSet<int>();
            int cells = view.Cells.Length;
            for (int i = 0; i < cells; i++)
            {
                if ((view.Cells[i].Flags & 1) == 0) continue;
                int x = view.X + i % ColonyView.Width, y = view.Y + i / ColonyView.Width;
                int cell = Grid.XYToCell(x, y);
                for (var item = First(cell); item != null; item = item.nextItem)
                {
                    var pickup = item.pickupable;
                    if (IsOre(pickup)) AddPile(view,pickup,ids,piles);
                }
                view.PileCoverage[i >> 3] |= (byte)(1 << (i & 7));
            }
            foreach (var pickup in NativeFallingOre.Active()) AddPile(view,pickup,ids,piles);
            piles.Sort((a, b) => a.SaveId.CompareTo(b.SaveId));
            // A dense dump can exceed an entire network frame. Each page remains authoritative
            // for its ID interval, including objects just removed, even when all stacks share a cell.
            int pilePageCount = Math.Max(1, (piles.Count + ColonyView.MaxPiles - 1) / ColonyView.MaxPiles);
            int pilePage = sequence % pilePageCount;
            int firstPile = pilePage * ColonyView.MaxPiles, pileCount = Math.Min(ColonyView.MaxPiles, piles.Count - firstPile);
            view.PileFrom = pilePage == 0 ? 0 : piles[firstPile - 1].SaveId + 1;
            view.PileThrough = pilePage == pilePageCount - 1 ? int.MaxValue : piles[firstPile + pileCount - 1].SaveId;
            view.Piles = piles.GetRange(firstPile, pileCount).ToArray();
            if (stockWorld != world || Time.unscaledTime >= nextStockSample)
            {
                stockWorld = world; nextStockSample = Time.unscaledTime + 1f;
                hostStocks.Clear();
                var amounts = world.worldInventory.GetAccessibleAmounts();
                foreach (var pair in amounts)
                {
                    float reserved = Mathf.Max(0, MaterialNeeds.GetAmount(pair.Key, world.id, false));
                    if (pair.Value <= 0 && reserved <= 0) continue;
                    hostStocks.Add(new ColonyStock { Tag = pair.Key.GetHash(), Total = Mathf.Max(0, pair.Value), Reserved = reserved });
                }
                foreach (var pair in world.materialNeeds)
                    if (pair.Value > 0 && !amounts.ContainsKey(pair.Key))
                        hostStocks.Add(new ColonyStock { Tag = pair.Key.GetHash(), Reserved = pair.Value });
                hostStocks.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            }
            int pages = Math.Max(1, (hostStocks.Count + ColonyView.MaxStocks - 1) / ColonyView.MaxStocks);
            int page = sequence % pages, from = page * ColonyView.MaxStocks;
            int count = Math.Min(ColonyView.MaxStocks, hostStocks.Count - from);
            view.CompleteStock = pages == 1;
            view.StockFrom = from == 0 ? int.MinValue : hostStocks[from - 1].Tag + 1;
            view.StockThrough = page == pages - 1 ? int.MaxValue : hostStocks[from + count - 1].Tag;
            view.Stocks = hostStocks.GetRange(from, count).ToArray();
        }

        static void AddPile(ColonyView view,Pickupable ore,HashSet<int> ids,List<ColonyPile> piles)
        {
            if(ore.IsEntombed) return;
            var pos=ore.transform.position;
            int cell=Grid.PosToCell(pos);
            if (!Grid.IsValidCell(cell)) return;
            Grid.CellToXY(cell,out int x,out int y);
            int dx=x-view.X,dy=y-view.Y;
            if ((uint)dx>=ColonyView.Width || (uint)dy>=ColonyView.Height) return;
            int i=dy*ColonyView.Width+dx;
            if ((view.Cells[i].Flags&1)==0 || !ids.Add(ore.KPrefabID.InstanceID)) return;
            piles.Add(new ColonyPile { Cell=(ushort)i, SaveId=ore.KPrefabID.InstanceID,
                SubX=(byte)Mathf.RoundToInt(255*Mathf.Clamp01(pos.x-x)), SubY=(byte)Mathf.RoundToInt(255*Mathf.Clamp01(pos.y-y)),
                Animation=ore.GetComponent<KBatchedAnimController>()?.currentAnim.HashValue??0,
                Material=NativeMaterialReadings.Capture(ore.PrimaryElement) });
        }

        internal static void Apply(ColonyView view, WorldContainer world, Transform parent)
        {
            if (view.EntityPayload != null && view.EntityPayload.Length > 0)
                NativeEntityDisplay.Apply(view, world, parent);
            NativeFallingOre.ObserveCoverage(view);
            observed.Clear();
            foreach (var p in view.Piles)
            {
                int x = view.X + p.Cell % ColonyView.Width, y = view.Y + p.Cell / ColonyView.Width;
                if (x >= Grid.WidthInCells || y >= Grid.HeightInCells) continue;
                int cell = Grid.XYToCell(x, y);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id) continue;
                var element = ElementLoader.FindElementByHash((SimHashes)p.Material.Element);
                if (element == null || !element.IsSolid || !observed.Add(p.SaveId)) continue;
                if (NativeFallingOre.AcceptView(p.SaveId,view.DupePoseTick))
                    ApplyPile(p,cell,new Vector2(x+p.SubX/255f,y+p.SubY/255f),element,parent,view.HasEnvironment);
            }
            for (int i = 0; i < view.Cells.Length; i++)
            {
                if (!view.CoversPiles(i) || (view.Cells[i].Flags & 1) == 0) continue;
                int x = view.X + i % ColonyView.Width, y = view.Y + i / ColonyView.Width;
                if (x >= Grid.WidthInCells || y >= Grid.HeightInCells) continue;
                int cell = Grid.XYToCell(x, y);
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id) continue;
                for (var item = First(cell); item != null; item = item.nextItem)
                {
                    var pickup = item.pickupable;
                    if (IsOre(pickup) && pickup.KPrefabID.InstanceID >= view.PileFrom &&
                        pickup.KPrefabID.InstanceID <= view.PileThrough && !observed.Contains(pickup.KPrefabID.InstanceID) && NativeFallingOre.AcceptView(pickup.KPrefabID.InstanceID,view.DupePoseTick))
                        SetVisible(pickup, false);
                }
            }
            removed.Clear();
            foreach (var pair in proxies)
            {
                if (!NativeFallingOre.AcceptView(pair.Key,view.DupePoseTick) || observed.Contains(pair.Key) || pair.Key < view.PileFrom || pair.Key > view.PileThrough) continue;
                var xy = Grid.CellToXY(pair.Value.Cell);
                int x = xy.x - view.X, y = xy.y - view.Y;
                if (x >= 0 && x < ColonyView.Width && y >= 0 && y < ColonyView.Height &&
                    view.CoversPiles(y * ColonyView.Width + x)) removed.Add(pair.Key);
            }
            foreach (int id in removed) Remove(id);
            ApplyStocks(view, world.worldInventory);
        }

        internal static void ApplyPile(ColonyPile p,int cell,Vector2 position,Element element,Transform parent,bool environment)
        {
            var original = KPrefabIDTracker.Get().GetInstance(p.SaveId);
            var pickup = original == null ? null : original.GetComponent<Pickupable>();
            if (IsOre(pickup) && !pickup.IsEntombed && pickup.PrimaryElement.ElementID == element.id)
            {
                if (Vector2.Distance(pickup.transform.position,position)<=.006f)
                {
                    SetVisible(pickup, true);
                    NativeWorldMirror.ApplyPrimary(pickup.PrimaryElement, p.Material);
                    if (environment) NativeMaterialReadings.Apply(pickup.PrimaryElement, p.Material);
                    Play(pickup.GetComponent<KBatchedAnimController>(), p.Animation);
                    Remove(p.SaveId);
                    return;
                }
                SetVisible(pickup, false);
            }
            if (!proxies.TryGetValue(p.SaveId, out var visual) || visual.Primary.ElementID != element.id)
            {
                Remove(p.SaveId);
                visual = Create(element, p, cell, parent);
                if (visual == null) return;
                proxies.Add(p.SaveId, visual);
            }
            var pos = visual.Anim.transform.position;
            visual.Anim.transform.position = new Vector3(position.x,position.y,pos.z);
            visual.Anim.SetDirty();
            visual.Cell = cell;
            NativeWorldMirror.ApplyPrimary(visual.Primary, p.Material);
            if (environment) NativeMaterialReadings.Apply(visual.Primary, p.Material);
            Play(visual.Anim, p.Animation);
        }

        static PileVisual Create(Element e, ColonyPile pile, int cell, Transform parent)
        {
            var prefab = Assets.TryGetPrefab(e.tag);
            var source = prefab == null ? null : prefab.GetComponent<KBatchedAnimController>();
            if (source == null) return null;
            var go = new GameObject(e.name);
            go.SetActive(false);
            go.layer = prefab.layer;
            go.transform.SetParent(parent, false);
            go.transform.position = Grid.CellToPosCBC(cell, source.sceneLayer);
            var anim = go.AddComponent<KBatchedAnimController>();
            anim.AnimFiles = source.AnimFiles; anim.initialAnim = source.initialAnim; anim.defaultAnim = source.defaultAnim;
            anim.animScale = source.animScale; anim.sceneLayer = source.sceneLayer; anim.fgLayer = source.fgLayer;
            anim.Offset = source.Offset; anim.materialType = source.materialType; anim.isMovable = true;
            var collision = go.AddComponent<KBoxCollider2D>();
            collision.size = new Vector2(.75f, .7f); collision.offset = new Vector2(0, .3f);
            go.AddComponent<KPrefabID>().PrefabTag = e.tag;
            go.AddComponent<NativeEntityIdentity>().SaveId=pile.SaveId;
            go.AddComponent<KSelectable>().SetName(e.name);
            var primary = go.AddComponent<PrimaryElement>();
            primary.SetElement(e.id, false); primary.KeepZeroMassObject = true;
            NativeWorldMirror.ApplyPrimary(primary, pile.Material);
            go.SetActive(true);
            return new PileVisual { Primary = primary, Anim = anim, Cell = cell };
        }

        static void Play(KBatchedAnimController anim, int hash)
        {
            if (anim != null && hash != 0 && anim.currentAnim.HashValue != hash && anim.HasAnimation(new HashedString(hash)))
                anim.Play(new HashedString(hash));
        }

        static void SetVisible(Pickupable pickup, bool visible)
        {
            if (!(visible ? hidden.Remove(pickup) : hidden.Add(pickup))) return;
            pickup.GetComponent<KBatchedAnimController>()?.SetVisiblity(visible);
            var selectable = pickup.GetComponent<KSelectable>();
            if (selectable == null) return;
            if (!visible && selectable.IsSelected) NativeWorldMirror.ClearSelection(selectable.gameObject);
            selectable.IsSelectable = visible;
        }

        static void Remove(int id)
        {
            if (!proxies.TryGetValue(id, out var p)) return;
            proxies.Remove(id); NativeWorldMirror.DestroySelectableProxy(p.Primary.gameObject);
        }

        internal static KSelectable GetSelectable(int hostSaveId)
        {
            if (proxies.TryGetValue(hostSaveId, out var p)) return p.Primary.GetComponent<KSelectable>();
            var original = KPrefabIDTracker.Get().GetInstance(hostSaveId);
            var pickup = original == null ? null : original.GetComponent<Pickupable>();
            return IsOre(pickup) && !pickup.IsEntombed && !hidden.Contains(pickup) ? pickup.GetComponent<KSelectable>() : null;
        }

        static void ApplyStocks(ColonyView view, WorldInventory inventory)
        {
            if (inventory == null) return;
            if (guestInventory != inventory)
            {
                guestInventory = inventory;
                stockThrough = (long)int.MinValue - 1;
                knownTags.Clear();
                foreach (var prefab in Assets.Prefabs)
                    if (prefab != null) knownTags[prefab.PrefabTag.GetHash()] = prefab.PrefabTag;
                foreach (var tag in inventory.GetAccessibleAmounts().Keys) knownTags[tag.GetHash()] = tag;
            }
            var amounts = inventory.GetAccessibleAmounts();
            var needs = inventory.WorldContainer.materialNeeds;
            observed.Clear();
            foreach (var stock in view.Stocks)
            {
                observed.Add(stock.Tag);
                if (!knownTags.TryGetValue(stock.Tag, out var tag)) tag = new Tag(stock.Tag);
                if (stock.Total > 0 && DiscoveredResources.Instance != null &&
                    !DiscoveredResources.Instance.IsDiscovered(tag) && Assets.TryGetPrefab(tag) != null)
                    DiscoveredResources.Instance.Discover(tag);
                amounts[tag] = stock.Total;
                if (stock.Reserved > 0) needs[tag] = stock.Reserved;
                else needs.Remove(tag);
            }
            cleared.Clear();
            foreach (var tag in amounts.Keys)
                if (tag.GetHash() >= view.StockFrom && tag.GetHash() <= view.StockThrough && !observed.Contains(tag.GetHash()))
                    cleared.Add(tag);
            foreach (var tag in cleared) amounts.Remove(tag);
            cleared.Clear();
            foreach (var tag in needs.Keys)
                if (tag.GetHash() >= view.StockFrom && tag.GetHash() <= view.StockThrough && !observed.Contains(tag.GetHash()))
                    cleared.Add(tag);
            foreach (var tag in cleared) needs.Remove(tag);
            // Vanilla marks the count valid after scanning every inventory tag. That scan is
            // disabled on the replica. Publish readiness after the host covers the full range.
            if ((long)view.StockFrom <= stockThrough + 1)
                stockThrough = Math.Max(stockThrough, view.StockThrough);
            bool firstCount = stockThrough == int.MaxValue && !inventory.HasValidCount;
            if (firstCount) CountReady(inventory) = true;
            if (!firstCount && Time.unscaledTime < nextHudRefresh) return;
            nextHudRefresh = Time.unscaledTime + 1f;
            var pinned = PinnedResourcesPanel.Instance;
            if (pinned != null && pinned.gameObject.activeInHierarchy && AllResourcesScreen.Instance != null)
                pinned.Refresh();
            if (ResourceCategoryScreen.Instance != null && ResourceCategoryScreen.Instance.gameObject.activeInHierarchy)
                foreach (var entry in ResourceCategoryScreen.Instance.GetComponentsInChildren<ResourceEntry>(false)) entry.UpdateValue();
            if (AllResourcesScreen.Instance != null && AllResourcesScreen.Instance.gameObject.activeInHierarchy)
                AllResourcesScreen.Instance.RefreshRows();
        }

        // Vanilla's food rows sum each edible's Units * CaloriesPerUnit. The host inventory
        // already excludes private storage and contains the authoritative units by food tag.
        [HarmonyPatch(typeof(RationTracker), nameof(RationTracker.CountAmountForItemWithID))]
        static class GuestFoodCalories
        {
            static bool Prefix(string ID, WorldInventory inventory, ref float __result)
            {
                if (!CoopSession.IsNativeClient || inventory == null || inventory != guestInventory ||
                    !inventory.HasValidCount) return true;
                var food = EdiblesManager.GetFoodInfo(ID);
                if (food == null) return true;
                __result = inventory.GetTotalAmount(new Tag(ID), false) * food.CaloriesPerUnit;
                return false;
            }
        }

        [HarmonyPatch(typeof(WorldInventory), "Update")]
        static class KeepAuthoritativeStock
        {
            static bool Prefix(WorldInventory __instance) => !CoopSession.IsNativeClient || __instance != guestInventory;
        }

        internal static void Reset()
        {
            NativeFallingOre.Reset();
            NativeEntityDisplay.Reset();
            foreach (var p in proxies.Values) if (p.Primary != null) NativeWorldMirror.DestroySelectableProxy(p.Primary.gameObject);
            proxies.Clear(); hidden.Clear(); knownTags.Clear(); guestInventory = null; stockWorld = null;
            hostStocks.Clear(); nextStockSample = nextHudRefresh = 0;
        }
    }
}
