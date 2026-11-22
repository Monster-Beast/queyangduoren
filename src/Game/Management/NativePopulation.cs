using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static readonly List<MinionIdentity> worldPopulation=new List<MinionIdentity>();
        static readonly AccessTools.FieldRef<MeterScreen,LocText> populationLabel=
            AccessTools.FieldRefAccess<MeterScreen,LocText>("currentMinions");
        static int populationWorld=-1,localPopulation,totalPopulation;
        static bool hasHud;
        static WorldInventory hudInventory;
        static WorldContainer sampledWorld;
        static float sampleNext,sampledCalories,sampledStress,hudCalories,hudStress;
        static MeterScreen hudScreen;
        static MeterScreen_Stress stressMeter;
        static MeterScreen_Rations rationMeter;

        internal static void CaptureHud(ColonyView view,WorldContainer world)
        {
            var tracker=RationTracker.Get();
            if (tracker==null || world.worldInventory==null || world.id<0 || world.id>=255) return;
            if (sampledWorld!=world || Time.unscaledTime>=sampleNext)
            {
                sampledWorld=world;
                sampleNext=Time.unscaledTime+.25f;
                sampledCalories=tracker.CountAmount(null,world.worldInventory,true);
                sampledStress=GameUtil.GetMaxStressInActiveWorld();
            }
            view.HasColonyHud=true; view.HudWorld=world.id;
            worldPopulation.Clear();
            Components.LiveMinionIdentities.GetWorldItems(worldPopulation,world.id,false);
            view.HudLocalPopulation=worldPopulation.Count;
            view.HudTotalPopulation=Components.LiveMinionIdentities.Count;
            worldPopulation.Clear();
            view.HudCalories=sampledCalories; view.HudStress=sampledStress;
        }

        internal static void ReceiveHud(ColonyView view)
        {
            var world=ClusterManager.Instance.activeWorld;
            if (world==null || world.id!=view.HudWorld) return;
            bool refresh=!hasHud || hudInventory!=world.worldInventory || hudStress!=view.HudStress || hudCalories!=view.HudCalories;
            hudInventory=world.worldInventory; hudStress=view.HudStress; hudCalories=view.HudCalories; hasHud=true;
            ApplyPopulation(world.id,view.HudLocalPopulation,view.HudTotalPopulation);
            var screen=MeterScreen.Instance;
            if (screen==null) return;
            if (hudScreen!=screen || stressMeter==null || rationMeter==null)
            {
                hudScreen=screen;
                stressMeter=screen.GetComponentInChildren<MeterScreen_Stress>(true);
                rationMeter=screen.GetComponentInChildren<MeterScreen_Rations>(true);
                refresh=true;
            }
            if (!refresh) return;
            // A reloaded world's graph trackers can start after the native meter labels.
            // Set the two authoritative values without requesting a graph refresh on scene load.
            if (stressMeter!=null && stressMeter.Label!=null) stressMeter.Label.text=Mathf.Round(hudStress).ToString();
            if (rationMeter!=null && rationMeter.Label!=null)
                rationMeter.Label.text=GameUtil.GetFormattedCalories((long)hudCalories,GameUtil.TimeSlice.None,true);
        }

        [HarmonyPatch(typeof(GameUtil),nameof(GameUtil.GetMaxStressInActiveWorld),new Type[0])]
        static class GuestHudStress
        {
            static bool Prefix(ref float __result)
            {
                if (!NativeGuestScene.Ready || !hasHud || ClusterManager.Instance?.activeWorldId!=populationWorld) return true;
                __result=hudStress; return false;
            }
        }

        [HarmonyPatch(typeof(WorldResourceAmountTracker<RationTracker>),"CountAmount",new[]{typeof(Dictionary<string,float>),typeof(WorldInventory),typeof(bool)})]
        static class GuestRations
        {
            static bool Prefix(object __instance,Dictionary<string,float> __0,WorldInventory __1,ref float __result)
            {
                if (!(__instance is RationTracker) || !NativeGuestScene.Ready || !hasHud || __1!=hudInventory) return true;
                if (__0!=null)
                {
                    __0.Clear();
                    foreach (var item in EdiblesManager.GetAllLoadedFoodTypes())
                    {
                        string id=((IConsumableUIItem)item).ConsumableId;
                        float units=__1.GetTotalAmount(new Tag(id),false);
                        if (units>0) __0[id]=units;
                    }
                }
                __result=hudCalories; return false;
            }
        }

        static string Population(int world)
        {
            if (world<0 || world>=byte.MaxValue || ClusterManager.Instance?.GetWorld(world)==null) return Conflict;
            worldPopulation.Clear();
            Components.LiveMinionIdentities.GetWorldItems(worldPopulation,world,false);
            int local=0;
            foreach (var d in worldPopulation) if (d!=null) local++;
            worldPopulation.Clear();
            return Prefix+"pop/"+world+"/"+local+"/"+Components.LiveMinionIdentities.Count;
        }

        static void ApplyPopulation(int world,int local,int total)
        {
            if (world<0 || ClusterManager.Instance?.activeWorldId!=world || local<0 || total<local) return;
            bool changed=populationWorld!=world || localPopulation!=local || totalPopulation!=total;
            populationWorld=world; localPopulation=local; totalPopulation=total;
            if (changed && MeterScreen.Instance!=null) DisplayPopulation(MeterScreen.Instance);
        }

        static void DisplayPopulation(MeterScreen screen)
        {
            var label=populationLabel(screen);
            if (label==null || screen.MinionsTooltip==null) return;
            string tip;
            if (DlcManager.FeatureClusterSpaceEnabled())
            {
                label.text=localPopulation+"/"+totalPopulation;
                var body=ClusterManager.Instance.activeWorld.GetComponent<ClusterGridEntity>();
                tip=string.Format(STRINGS.UI.TOOLTIPS.METERSCREEN_POPULATION_CLUSTER,body.Name,localPopulation,totalPopulation);
            }
            else
            {
                label.text=totalPopulation.ToString();
                tip=string.Format(STRINGS.UI.TOOLTIPS.METERSCREEN_POPULATION,totalPopulation);
            }
            screen.MinionsTooltip.ClearMultiStringTooltip();
            screen.MinionsTooltip.AddMultiStringTooltip(tip,screen.ToolTipStyle_Header);
        }

        // The native census counts simulation components. A newly printed guest visual deliberately has none.
        [HarmonyPatch(typeof(MeterScreen),"RefreshMinions",new Type[0])]
        static class PopulationMeter
        {
            static bool Prefix() => !NativeGuestScene.Ready || populationWorld<0 ||
                ClusterManager.Instance?.activeWorldId!=populationWorld;
        }
    }
}
