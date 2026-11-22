using System;
using System.Collections.Generic;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // Verify the decoded wire values against original components from the loaded save.
    // A colony without a critter, crop or particular food reports only what it actually has.
    internal static class EntityReadChecks
    {
        public static void Run(Action<string> log)
        {
            var sampled = new HashSet<ColonyEntityKind>();
            var world = ClusterManager.Instance.activeWorld;
            foreach (var crop in Components.Crops.Items)
                if (crop != null && Sample(crop.gameObject, ColonyEntityKind.Plant, sampled, world, log)) break;
            foreach (var brain in Components.Brains.Items)
                if (brain is CreatureBrain && Sample(brain.gameObject, ColonyEntityKind.Critter, sampled, world, log)) break;
            int attempts = 0;
            foreach (var p in Components.Pickupables.Items)
            {
                if (p == null || p.storage != null || p.KPrefabID == null) continue;
                ColonyEntityKind kind;
                if (p.KPrefabID.HasTag(GameTags.Egg)) kind = ColonyEntityKind.Egg;
                else if (p.GetComponent<Edible>() != null) kind = ColonyEntityKind.Food;
                else continue;
                if (sampled.Contains(kind)) continue;
                if (++attempts > 16) break;
                Sample(p.gameObject, kind, sampled, world, log);
            }
            foreach (var b in Components.BuildingCompletes.Items)
                if (b != null && b.GetComponent<Storage>() != null &&
                    Sample(b.gameObject, ColonyEntityKind.Storage, sampled, world, log)) break;
            if (sampled.Count == 0) throw new InvalidOperationException("No live entity matched an independently sampled component.");
        }

        static bool Sample(GameObject obj, ColonyEntityKind kind, HashSet<ColonyEntityKind> kinds, WorldContainer world, Action<string> log)
        {
            int cell = Grid.PosToCell(obj);
            if (!Grid.IsValidCell(cell) || Grid.Visible[cell] == 0 || Grid.WorldIdx[cell] != world.id) return false;
            int id = obj.GetComponent<KPrefabID>().InstanceID;
            Grid.CellToXY(cell, out int x, out int y);
            var view = ColonyView.Decode(HostWorld.Capture(x, y));
            var page = EntityStateCodec.Decode(view.EntityPayload);
            foreach (var e in page.Entities)
            {
                if (e.Id != id || e.Kind != kind) continue;
                var primary = obj.GetComponent<PrimaryElement>();
                if (primary != null)
                {
                    Close(e.Mass, primary.Mass, "mass"); Close(e.Temperature, primary.Temperature, "temperature");
                    if (e.Element != (int)primary.ElementID) throw new InvalidOperationException("Entity element mismatch");
                }
                if (view.X + e.Cell % ColonyView.Width != x || view.Y + e.Cell / ColonyView.Width != y)
                    throw new InvalidOperationException("Entity coordinate mismatch");
                if (kind == ColonyEntityKind.Food) Close(e.Value, obj.GetComponent<Edible>().Calories / 1000f, "kcal");
                if (kind == ColonyEntityKind.Plant && obj.GetComponent<Growing>() is Growing growth)
                    Close(e.Value, Mathf.Clamp01(growth.PercentOfCurrentHarvest()), "growth");
                if (kind == ColonyEntityKind.Critter && obj.GetComponent<Health>() is Health hp && hp.maxHitPoints > 0)
                    Close(e.Extra, Mathf.Clamp01(hp.hitPoints / hp.maxHitPoints), "health");
                if (kind == ColonyEntityKind.Storage)
                {
                    float measured = 0;
                    foreach (var store in obj.GetComponents<Storage>()) measured += store.ExactMassStored();
                    Close(e.Value, measured, "container mass");
                    if(NativeFacilityStorage.Supported(obj)) CheckPanel(obj);
                }
                kinds.Add(kind);
                log(FormattableString.Invariant($"FEATURE DETAIL native entity={kind} id={id} cell={cell} kg={e.Mass:0.###} value={e.Value:0.###}"));
                return true;
            }
            return false;
        }
        static void CheckPanel(GameObject obj)
        {
            var actual=NativeFacilityStorage.Decode(NativeFacilityStorage.Encode(obj,0));
            var wash=obj.GetComponent<HandSanitizer>();
            IStorage washStore=wash==null?null:obj.GetComponent<Storage>();
            var items=new HashSet<GameObject>();
            var masses=new Dictionary<int,float>();
            float total=0, available=0;
            foreach(var store in obj.GetComponentsInChildren<IStorage>())
            {
                if(!store.ShouldShowInUI() && store!=washStore) continue;
                foreach(var item in store.GetItems())
                {
                    if(item==null || !items.Add(item)) continue;
                    var primary=item.GetComponent<PrimaryElement>();
                    if(primary==null || item.GetComponent<KPrefabID>()==null) continue;
                    int tag=item.PrefabID().GetHash();
                    masses.TryGetValue(tag,out float prior);
                    masses[tag]=prior+primary.Mass;
                    total+=primary.Mass;
                    var pickup=item.GetComponent<Pickupable>();
                    available+=primary.Mass*(pickup!=null && pickup.TotalAmount>0 ?
                        Mathf.Clamp01(pickup.UnreservedAmount/pickup.TotalAmount):1);
                }
            }
            Close(actual.Stored,total,"contents panel total");
            Close(actual.Available,available,"contents panel available");
            if(actual.Total!=masses.Count) throw new InvalidOperationException("Contents panel item type count mismatch");
            for(int page=0;page<actual.Pages;page++)
            {
                var part=page==0?actual:NativeFacilityStorage.Decode(NativeFacilityStorage.Encode(obj,page));
                foreach(var row in part.Rows.Values) Close(row.Mass,masses[row.Tag],"contents panel item");
            }
        }
        static void Close(float a,float b,string name)
        {
            if (Math.Abs(a-b) > .03f || float.IsNaN(a) || float.IsNaN(b))
                throw new InvalidOperationException(FormattableString.Invariant($"Entity {name} mismatch wire={a} native={b}"));
        }
    }
}
