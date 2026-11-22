using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static readonly FieldInfo Selection = AccessTools.DeclaredField(typeof(SelectTool), "selected");
        static readonly FieldInfo ButtonEnabled = AccessTools.DeclaredField(typeof(BuildingEnabledButton), "buildingEnabled");
        static readonly FieldInfo ButtonQueued = AccessTools.DeclaredField(typeof(BuildingEnabledButton), "queuedToggle");
        static readonly FieldInfo ButtonIndex = AccessTools.DeclaredField(typeof(BuildingEnabledButton), "ToggleIdx");
        static BuildingEnabledButton selectedButton;
        static bool switchKnown, enabled, queued;
        static void ResetBuilding()
        {
            selectedButton=null; switchKnown=false; inspectedId=0; buildingReadout=null; visibleInfoPanel=null;
            if(statusPanel!=null) statusPanel.Commit();
            statusPanel=null; statusTarget=null; storagePanel=null;
            ResetStoragePanel();
            SelectSettings(null);
        }
        static void PollBuilding()
        {
            var selected = SelectTool.Instance == null ? null : Selection.GetValue(SelectTool.Instance) as KSelectable;
            PollBuildingReadings(selected);
            if (selected != null)
            {
                var proxy = selected.GetComponent<NativeProxySettings>();
                if (proxy != null) proxy.Poll();
            }
            var button = selected == null ? null : selected.GetComponent<BuildingEnabledButton>();
            if (button != selectedButton) { selectedButton = button; switchKnown = false; }
            if (button != null) BuildingCommand(button, "building/" + Id(button));
            SelectSettings(selected == null ? null : selected.gameObject);
            PollSettings();
        }
        static void BuildingCommand(Component button, string route)
        {
            var b = button.GetComponent<Building>();
            if (b == null) return;
            Grid.CellToXY(b.GetCell(), out int x, out int y);
            Send(route, x, y);
        }
        static void ApplyBuilding(string[] p)
        {
            if (p.Length != 5 || selectedButton == null || Number(p[2]) != Id(selectedButton)) return;
            bool nextEnabled = p[3] == "1", nextQueued = p[4] == "1";
            if (switchKnown && nextEnabled == enabled && nextQueued == queued) return;
            enabled = nextEnabled; queued = nextQueued; switchKnown = true;
            ButtonEnabled.SetValue(selectedButton, enabled);
            ButtonQueued.SetValue(selectedButton, queued);
            global::Game.Instance.userMenu.Refresh(selectedButton.gameObject);
        }

        [HarmonyPatch(typeof(BuildingEnabledButton), "OnMenuToggle")]
        static class SwitchClick
        {
            static bool Prefix(BuildingEnabledButton __instance)
            {
                if (!Guard) return true;
                if (switchKnown && __instance == selectedButton)
                    BuildingCommand(__instance, "switch/" + Id(__instance) + "/" + (enabled ? 1 : 0) + "/" + (queued ? 1 : 0));
                return false;
            }
        }

        [HarmonyPatch(typeof(Toggleable), "IsToggleQueued")]
        static class SwitchQueueDisplay
        {
            static bool Prefix(Toggleable __instance, int __0, ref bool __result)
            {
                if (!NativeGuestScene.Ready || !switchKnown || selectedButton == null ||
                    __instance.gameObject != selectedButton.gameObject || __0 != (int)ButtonIndex.GetValue(selectedButton)) return true;
                __result = queued;
                return false;
            }
        }
    }
}
