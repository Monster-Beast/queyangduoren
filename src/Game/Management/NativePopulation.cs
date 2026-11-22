using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static readonly List<MinionIdentity> worldPopulation=new List<MinionIdentity>();
        static readonly AccessTools.FieldRef<MeterScreen,LocText> populationLabel=
            AccessTools.FieldRefAccess<MeterScreen,LocText>("currentMinions");
        static int populationWorld=-1,localPopulation,totalPopulation;

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
