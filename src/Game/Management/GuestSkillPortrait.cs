using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    [HarmonyPatch(typeof(FullBodyUIMinionWidget),nameof(FullBodyUIMinionWidget.SetPortraitAnimator))]
    internal static class GuestSkillPortrait
    {
        static void Postfix(FullBodyUIMinionWidget __instance)
        {
            if (NativeGuestScene.Ready && __instance.isActiveAndEnabled)
                __instance.StartCoroutine(Refresh(__instance));
        }

        static IEnumerator Refresh(FullBodyUIMinionWidget widget)
        {
            // UI batch registration occurs after the portrait copied the selected person's
            // accessories. Resolve the final face, clothing atlas and override indices together.
            yield return null;
            var anim=widget.animController;
            if (anim==null || !anim.isActiveAndEnabled || !NativeGuestScene.Ready || anim.GetBatch()==null) yield break;
            var controller=anim.GetComponent<SymbolOverrideController>();
            if(controller==null) yield break;
            controller.MarkDirty();
            anim.ApplySymbolOverrides();
            anim.SetDirty();
        }
    }
}
