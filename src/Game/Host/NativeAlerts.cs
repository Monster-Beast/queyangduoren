using HarmonyLib;

namespace Queyang.Game
{
    internal static class NativeAlerts
    {
        static byte? authoritative;
        static int stamp,worldId=-1;

        internal static byte? Capture(WorldContainer world)
        {
            var alert=world.AlertManager;
            return alert==null?(byte?)null:(byte)((alert.IsYellowAlert()?1:0)|(alert.IsRedAlert()?2:0));
        }

        internal static void Apply(ColonyView view,WorldContainer world)
        {
            if(!view.HostAlerts.HasValue || view.DupePoseTick<stamp && world.id==worldId) return;
            authoritative=view.HostAlerts;stamp=view.DupePoseTick;worldId=world.id;
            var manager=world.AlertManager;
            if(manager==null) return;
            bool yellow=(authoritative.Value&1)!=0,red=(authoritative.Value&2)!=0;
            bool changed=manager.IsRedAlertToggledOn()!=red || manager.IsYellowAlert()!=yellow;
            if(manager.IsRedAlertToggledOn()!=red) manager.ToggleRedAlert(red);
            if(manager.IsYellowAlert()!=yellow) manager.SetHasTopPriorityChore(yellow);
            if(changed) Vignette.Instance?.Refresh(null);
        }

        static void Override(AlertStateManager.Instance manager,ref bool option,byte mask)
        {
            var world=ClusterManager.Instance?.activeWorld;
            if(NativeGuestScene.Ready && authoritative.HasValue && world?.id==worldId && world.AlertManager==manager)
                option=(authoritative.Value&mask)!=0;
        }

        [HarmonyPatch(typeof(AlertStateManager.Instance),nameof(AlertStateManager.Instance.SetHasTopPriorityChore))]
        static class GuestYellow { static void Prefix(AlertStateManager.Instance __instance,ref bool __0)=>Override(__instance,ref __0,1); }
        [HarmonyPatch(typeof(AlertStateManager.Instance),nameof(AlertStateManager.Instance.ToggleRedAlert))]
        static class GuestRed { static void Prefix(AlertStateManager.Instance __instance,ref bool __0)=>Override(__instance,ref __0,2); }

        internal static void Reset() { authoritative=null;stamp=0;worldId=-1; }
    }
}
