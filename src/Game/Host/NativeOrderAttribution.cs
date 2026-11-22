using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    // Scope attribution to the host's actual tools/sidebar buttons or an admitted peer's
    // validated command. ONI can call the same setters during prefab load and simulation.
    [HarmonyPatch]
    static class NativeOrderInput
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[]{typeof(DigTool),typeof(MopTool),typeof(ClearTool),typeof(HarvestTool),typeof(DeconstructTool)})
                yield return AccessTools.DeclaredMethod(type,"OnDragTool",new[]{typeof(int),typeof(int)});
            yield return AccessTools.DeclaredMethod(typeof(BuildTool),"TryBuild",new[]{typeof(int)});
            yield return AccessTools.DeclaredMethod(typeof(BaseUtilityBuildTool),"BuildPath",Type.EmptyTypes);
            yield return AccessTools.DeclaredMethod(typeof(Deconstructable),"OnDeconstruct",Type.EmptyTypes);
            yield return AccessTools.DeclaredMethod(typeof(Clearable),"OnClickClear",Type.EmptyTypes);
            yield return AccessTools.DeclaredMethod(typeof(HarvestDesignatable),"OnClickHarvestWhenReady",Type.EmptyTypes);
        }
        static void Prefix(out int __state) => __state=OrderOwnership.EnterHostInput();
        static void Finalizer(int __state) => OrderOwnership.LeaveInput(__state);
    }

    [HarmonyPatch(typeof(DigTool),"PlaceDig",new[]{typeof(int),typeof(int)})]
    static class AttributeDig
    {
        static void Prefix(int __0,out bool __state) => __state=OrderOwnership.Actor!=0 && Grid.IsValidCell(__0) && Diggable.GetDiggable(__0)==null;
        static void Postfix(bool __state,GameObject __result)
        {
            if (__state && __result!=null) OrderOwnership.Record(__result,OrderKind.Dig);
        }
    }

    [HarmonyPatch(typeof(MopTool),"OnDragTool",new[]{typeof(int),typeof(int)})]
    static class AttributeMop
    {
        static void Prefix(int __0,out bool __state) => __state=CoopSession.IsHosting && Grid.IsValidCell(__0) &&
            Grid.Objects[__0,(int)ObjectLayer.MopPlacer]==null;
        static void Postfix(int __0,bool __state)
        {
            if (__state) OrderOwnership.Record(Grid.Objects[__0,(int)ObjectLayer.MopPlacer],OrderKind.Mop);
        }
    }

    [HarmonyPatch]
    static class AttributeConstruction
    {
        static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(BuildingDef),"TryPlace",
            new[]{typeof(GameObject),typeof(Vector3),typeof(Orientation),typeof(IList<Tag>),typeof(string),typeof(bool),typeof(int)});
        static bool Prefix(BuildingDef __instance,Vector3 __1,Orientation __2) => !CoopSession.IsHosting ||
            !HostWorld.PendingPlacement(__instance,Grid.PosToCell(__1),__2);
        static void Postfix(BuildingDef __instance,Vector3 __1,Orientation __2,GameObject __result)
        {
            if (CoopSession.IsHosting && __result!=null)
                HostWorld.ReservePlacement(__instance,Grid.PosToCell(__1),__2,__result);
            if (OrderOwnership.Actor!=0 && __result!=null && __result.GetComponent<Constructable>()!=null)
                OrderOwnership.Record(__result,OrderKind.Build);
        }
    }

    [HarmonyPatch(typeof(Clearable),"MarkForClear",new[]{typeof(bool),typeof(bool)})]
    static class AttributeSweep
    {
        static void Prefix(Clearable __instance,out bool __state) =>
            __state=OrderOwnership.Actor!=0 && !NativeEntitySampler.IsMarked(__instance);
        static void Postfix(Clearable __instance,bool __state)
        {
            if (__state && NativeEntitySampler.IsMarked(__instance)) OrderOwnership.Record(__instance.gameObject,OrderKind.Sweep);
        }
    }

    [HarmonyPatch(typeof(Clearable),"CancelClearing",new Type[0])]
    static class ClearSweepAuthor
    {
        static void Postfix(Clearable __instance) => OrderOwnership.Remove(__instance,OrderKind.Sweep);
    }

    [HarmonyPatch(typeof(HarvestDesignatable),"SetHarvestWhenReady",new[]{typeof(bool)})]
    static class AttributeHarvest
    {
        static void Prefix(HarvestDesignatable __instance,out bool __state) => __state=__instance.HarvestWhenReady;
        static void Postfix(HarvestDesignatable __instance,bool __state)
        {
            if (__state==__instance.HarvestWhenReady) return;
            if (__state) OrderOwnership.Remove(__instance,OrderKind.Harvest);
            else OrderOwnership.Record(__instance.gameObject,OrderKind.Harvest);
        }
    }

    [HarmonyPatch(typeof(Deconstructable),"QueueDeconstruction",new[]{typeof(bool)})]
    static class AttributeDemolition
    {
        static void Prefix(Deconstructable __instance,out bool __state) =>
            __state=OrderOwnership.Actor!=0 && !__instance.IsMarkedForDeconstruction();
        static void Postfix(Deconstructable __instance,bool __state)
        {
            if (__state && __instance.IsMarkedForDeconstruction()) OrderOwnership.Record(__instance.gameObject,OrderKind.Deconstruct);
        }
    }

    [HarmonyPatch(typeof(Deconstructable),"CancelDeconstruction",new Type[0])]
    static class ClearDemolitionAuthor
    {
        static void Postfix(Deconstructable __instance) => OrderOwnership.Remove(__instance,OrderKind.Deconstruct);
    }
}
