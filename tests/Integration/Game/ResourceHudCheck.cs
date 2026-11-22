using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class ResourceHudCheck
    {
        readonly FixtureFrame picture;
        bool open;
        internal ResourceHudCheck(string path) { picture=new FixtureFrame(path); }

        static Dictionary<Tag,PinnedResourcesPanel.PinnedResourceRow> Rows =>
            (Dictionary<Tag,PinnedResourcesPanel.PinnedResourceRow>)AccessTools.Field(typeof(PinnedResourcesPanel),"rows")
                .GetValue(PinnedResourcesPanel.Instance);

        internal static bool Publish(CSteamID lobby,Action<string> log)
        {
            var inventory=ClusterManager.Instance.activeWorld.worldInventory;
            if (!inventory.HasValidCount || PinnedResourcesPanel.Instance==null) return false;
            var row=Rows.Values.Where(r=>r.gameObject.activeInHierarchy && r.valueLabel.text.Any(char.IsDigit) &&
                AllResourcesScreen.Instance.units.TryGetValue(r.Tag,out var unit) && unit==GameUtil.MeasureUnit.mass &&
                inventory.GetAmount(r.Tag,false)>0).OrderByDescending(r=>inventory.GetAmount(r.Tag,false)).FirstOrDefault();
            if (row==null) return false;
            var tag=row.Tag;
            float amount=inventory.GetAmount(tag,false),total=inventory.GetTotalAmount(tag,false);
            string data=string.Join("|",tag.Name,amount.ToString("R",CultureInfo.InvariantCulture),
                total.ToString("R",CultureInfo.InvariantCulture),row.valueLabel.text);
            if (!SteamMatchmaking.SetLobbyData(lobby,"qy_resource_check",data))
                throw new InvalidOperationException("Cannot publish host resource UI sample");
            var tracker=WorldResourceAmountTracker<RationTracker>.Get();
            string food="none";
            foreach (var unit in AllResourcesScreen.Instance.units)
            {
                if (unit.Value!=GameUtil.MeasureUnit.kcal) continue;
                float calories=tracker.CountAmountForItemWithID(unit.Key.Name,inventory);
                if (calories<=0) continue;
                float units=inventory.GetTotalAmount(unit.Key,false);
                food=string.Join("|",unit.Key.Name,calories.ToString("R",CultureInfo.InvariantCulture),
                    units.ToString("R",CultureInfo.InvariantCulture));
                log("DUAL_HOST_FOOD tag="+unit.Key+" calories="+calories+" inventoryUnits="+units);
                break;
            }
            if (!SteamMatchmaking.SetLobbyData(lobby,"qy_food_check",food))
                throw new InvalidOperationException("Cannot publish host food sample");
            log("DUAL_HOST_RESOURCE tag="+tag+" pinned="+row.valueLabel.text+" available="+amount+" total="+total);
            return true;
        }

        internal bool Check(CSteamID lobby,Action<string> log)
        {
            string[] data=SteamMatchmaking.GetLobbyData(lobby,"qy_resource_check").Split('|');
            if (data.Length!=4) return false;
            var tag=new Tag(data[0]);
            var inventory=ClusterManager.Instance.activeWorld.worldInventory;
            if (!inventory.HasValidCount || !Rows.TryGetValue(tag,out var pinned) || !pinned.gameObject.activeInHierarchy ||
                pinned.valueLabel.text!=data[3]) return false;
            float amount=float.Parse(data[1],CultureInfo.InvariantCulture),total=float.Parse(data[2],CultureInfo.InvariantCulture);
            if (Math.Abs(inventory.GetAmount(tag,false)-amount)>.01f || Math.Abs(inventory.GetTotalAmount(tag,false)-total)>.01f)
                return false;
            var food=SteamMatchmaking.GetLobbyData(lobby,"qy_food_check").Split('|');
            float kcalRead=0;
            if (food.Length==3)
            {
                kcalRead=WorldResourceAmountTracker<RationTracker>.Get().CountAmountForItemWithID(food[0],inventory);
                if (!Mathf.Approximately(kcalRead,float.Parse(food[1],CultureInfo.InvariantCulture)) ||
                    !Mathf.Approximately(inventory.GetTotalAmount(new Tag(food[0]),false),float.Parse(food[2],CultureInfo.InvariantCulture))) return false;
            }
            else if (food[0]!="none") return false;
            var screen=AllResourcesScreen.Instance;
            if (!open)
            {
                PinnedResourcesPanel.Instance.seeAllButton.onClick();
                screen.SetFilter(tag.ProperNameStripLink());
                open=true;
                return false;
            }
            if (!screen.IsScreenActive()) return false;
            var rows=(IDictionary)AccessTools.Field(typeof(AllResourcesScreen),"resourceRows").GetValue(screen);
            var row=rows[tag];
            if (row==null) return false;
            var go=(GameObject)AccessTools.Property(row.GetType(),"GameObject").GetValue(row,null);
            // The native search field debounces its change using unscaled time. Wait
            // for the real filter and layout so the saved picture actually shows the row.
            foreach (DictionaryEntry entry in rows)
            {
                var other=(Tag)entry.Key;
                if (other==tag || other.ProperNameStripLink().IndexOf(tag.ProperNameStripLink(),StringComparison.OrdinalIgnoreCase)>=0)
                    continue;
                var otherRow=(GameObject)AccessTools.Property(entry.Value.GetType(),"GameObject").GetValue(entry.Value,null);
                if (otherRow.activeSelf) return false;
            }
            if (!go.activeInHierarchy)
            {
                var fold=go.GetComponentInParent<FoldOutPanel>(true);
                if (fold!=null && !fold.container.activeSelf) fold.GetComponentInChildren<MultiToggle>().onClick();
                return false;
            }
            var labels=go.GetComponent<HierarchyReferences>();
            string available=labels.GetReference<LocText>("AvailableLabel").text;
            string totalText=labels.GetReference<LocText>("TotalLabel").text;
            string reserved=labels.GetReference<LocText>("ReservedLabel").text;
            if (available!=GameUtil.GetFormattedMass(amount) || totalText!=GameUtil.GetFormattedMass(total) ||
                reserved!=GameUtil.GetFormattedMass(total-amount)) return false;
            var scroll=go.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            if (scroll!=null && scroll.viewport!=null)
            {
                var rect=(RectTransform)go.transform;
                var point=scroll.viewport.InverseTransformPoint(rect.TransformPoint(rect.rect.center));
                if (!scroll.viewport.rect.Contains(point)) { scroll.verticalNormalizedPosition=1f; return false; }
            }
            if (!picture.Requested) { picture.Capture(); return false; }
            if (!picture.Complete) return false;
            log("DUAL_GUEST_RESOURCE tag="+tag+" pinned="+pinned.valueLabel.text+" available="+available+
                " total="+totalText+" reserved="+reserved+" file=dual-guest-resources.png");
            if (food.Length==3) log("DUAL_GUEST_FOOD tag="+food[0]+" calories="+kcalRead+" hostInventoryUnits="+food[2]);
            screen.Show(false);
            return true;
        }
    }
}
