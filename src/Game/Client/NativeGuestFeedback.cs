using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Queyang.Game
{
    internal static class NativeGuestFeedback
    {
        static float expires;
        internal static string Text { get; private set; }
        internal static bool Visible => !string.IsNullOrEmpty(Text) && Time.unscaledTime < expires;
        internal static void Show(string text)
        {
            Text = text;
            expires = Time.unscaledTime + 3.5f;
        }

        internal static void Result(string text)
        {
            if (CoopSession.IsNativeClient && text.StartsWith("ERROR", StringComparison.Ordinal))
                Show("房主拒绝操作 · " + text);
        }

        static bool Refuse(string label)
        {
            if (!NativeGuestScene.Ready) return true;
            Show(label + "由房主操作。");
            return false;
        }

        [HarmonyPatch(typeof(PlayerController), "ActivateTool")]
        static class ToolSelection
        {
            static bool Prefix(InterfaceTool __0)
            {
                if (!NativeGuestScene.Ready || __0 == null) return true;
                if (__0 is SelectTool || __0 is ClusterMapSelectTool || __0 is PrebuildTool || __0 is BuildTool ||
                    __0 is BaseUtilityBuildTool || __0 is DigTool || __0 is ClearTool ||
                    __0 is CancelTool || __0 is DeconstructTool || __0 is MopTool ||
                    __0 is HarvestTool || __0 is PrioritizeTool) return true;
                return Refuse("此工具");
            }
        }

        [HarmonyPatch]
        static class Print
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ImmigrantScreen), "InitializeImmigrantScreen");
                yield return AccessTools.Method(typeof(ImmigrantScreen), "OnProceed");
                yield return AccessTools.Method(typeof(ImmigrantScreen), "OnRejectAll");
                yield return AccessTools.Method(typeof(ImmigrantScreen), "OnRejectionConfirmed");
            }
            static bool Prefix() => Refuse("打印舱选人和补给");
        }

        [HarmonyPatch]
        static class Skills
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SkillWidget), "OnPointerClick");
                yield return AccessTools.Method(typeof(MinionResume), "MasterSkill");
                yield return AccessTools.Method(typeof(SkillsScreen), "OnHatDropEntryClick");
            }
            static bool Prefix() => Refuse("技能和职业帽分配");
        }

        [HarmonyPatch(typeof(DetailsScreen), "OnNameChanged")]
        static class Rename
        {
            static bool Prefix() => Refuse("对象重命名");
        }

        // ESC still opens the vanilla pause menu. UI-driven modal pauses and the
        // clock widgets refreshed from host data bypass these player commands.
        [HarmonyPatch(typeof(SpeedControlScreen), "OnKeyDown")]
        static class SpeedKeys
        {
            static bool Prefix(KButtonEvent __0)
            {
                if (!NativeGuestScene.Ready) return true;
                if (__0.TryConsume(global::Action.TogglePause)) return Refuse("暂停或继续");
                if (__0.TryConsume(global::Action.SpeedUp) || __0.TryConsume(global::Action.SlowDown) ||
                    __0.TryConsume(global::Action.CycleSpeed)) return Refuse("调整倍速");
                return true;
            }
        }

        [HarmonyPatch]
        static class SpeedButtons
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                // The native button callbacks are direct calls to these two methods.
                yield return AccessTools.Method(typeof(SpeedControlScreen), "TogglePause");
                yield return AccessTools.Method(typeof(SpeedControlScreen), "SetSpeed");
                yield return AccessTools.Method(typeof(SpeedControlScreen), "ToggleRidiculousSpeed");
                yield return AccessTools.Method(typeof(SpeedControlScreen), "DebugStepFrame");
            }
            static bool Prefix() => Refuse("暂停和倍速");
        }

        [HarmonyPatch]
        static class SaveButtons
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PauseScreen), "OnSave");
                yield return AccessTools.Method(typeof(PauseScreen), "OnSaveAs");
                yield return AccessTools.Method(typeof(PauseScreen), "OnLoad");
            }
            [HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                if (NativeGuestScene.Ready) Show("存档由房主保存和载入。");
            }
        }

        [HarmonyPatch(typeof(UserMenu), "AddButton")]
        static class ContextButtons
        {
            static void Prefix(GameObject __0, KIconButtonMenu.ButtonInfo __1)
            {
                if (!NativeGuestScene.Ready || __1?.onClick == null || __0 == null) return;
                var source = __1.onClick.Method.DeclaringType;
                if (source == typeof(BuildingEnabledButton) || source == typeof(Navigator) ||
                    __0.GetComponent<NativeProxySettings>() != null) return;
                string action = ContextAction(source, __1.onClick.Method.Name);
                if (action != null)
                {
                    var target = __0;
                    __1.onClick = () => NativeToolCommands.Enqueue(action, Grid.PosToCell(target.transform.position));
                    return;
                }
                __1.tooltipText = __1.text + " · 此操作由房主执行";
                __1.onClick = () => Show("此对象操作由房主执行。");
            }

            static string ContextAction(Type source, string method)
            {
                if ((source == typeof(Diggable) || source == typeof(Moppable)) && method == "OnCancel" ||
                    source == typeof(Constructable) && method == "OnPressCancel" ||
                    source == typeof(Clearable) && method == "OnClickCancel") return "cancel";
                if (source == typeof(Clearable) && method == "OnClickClear") return "sweep";
                return null;
            }
        }

        [HarmonyPatch(typeof(UserMenu), "AddSlider")]
        static class ContextSliders
        {
            static void Prefix(UserMenu.SliderInfo __1)
            {
                if (!NativeGuestScene.Ready || __1 == null) return;
                __1.toolTip = "此滑条由房主调整。";
                __1.interactable = false;
                __1.onMinChange = __1.onMaxChange = null;
            }
        }

        [HarmonyPatch(typeof(UserMenuScreen), "OnPriorityClicked")]
        static class ContextPriority
        {
            static bool Prefix(GameObject ___selected, PrioritySetting __0)
            {
                if (!NativeGuestScene.Ready) return true;
                if (__0.priority_class != PriorityScreen.PriorityClass.basic || ___selected == null)
                    return Refuse("此优先级");
                NativeToolCommands.Enqueue("priority" + __0.priority_value, Grid.PosToCell(___selected.transform.position));
                return false;
            }
        }

        [HarmonyPatch(typeof(DetailsScreen), "Refresh")]
        static class SpecialSettings
        {
            static void Postfix(DetailsScreen __instance)
            {
                if (!NativeGuestScene.Ready) return;
                foreach (var pane in __instance.GetComponentsInChildren<SideScreenContent>())
                {
                    if (pane is TreeFilterableSideScreen || pane is ComplexFabricatorSideScreen ||
                        pane is ResearchSideScreen || pane is ConditionListSideScreen || pane is MinionTodoSideScreen ||
                        pane is ProgressBarSideScreen || pane is RelatedEntitiesSideScreen || pane is NoConfigSideScreen) continue;
                    if (pane.transform.Find("Co-op read-only controls") != null) continue;
                    // Preserve labels, layout and parent scrolling. The graphic catches
                    // pointer edits. CanvasGroup also disables keyboard slider/input edits.
                    var group = pane.GetComponent<CanvasGroup>();
                    if (group == null) group = pane.gameObject.AddComponent<CanvasGroup>();
                    group.interactable = false;
                    var guard = new GameObject("Co-op read-only controls", typeof(RectTransform),
                        typeof(LayoutElement), typeof(Image), typeof(GuestSettingClick));
                    guard.GetComponent<LayoutElement>().ignoreLayout = true;
                    var rect = (RectTransform)guard.transform;
                    rect.SetParent(pane.transform, false);
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.offsetMin = rect.offsetMax = Vector2.zero;
                    var image = guard.GetComponent<Image>();
                    image.color = Color.clear;
                    image.canvasRenderer.cullTransparentMesh = false;
                }
            }
        }
    }

    internal sealed class GuestSettingClick : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData _) => NativeGuestFeedback.Show("此专用面板的设置由房主修改。");
    }
}
