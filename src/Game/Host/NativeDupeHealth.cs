using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Queyang.Game
{
    internal sealed class NativeDupeHealth : MonoBehaviour
    {
        static readonly Func<NameDisplayScreen,GameObject,NameDisplayScreen.Entry> entryFor=
            AccessTools.MethodDelegate<Func<NameDisplayScreen,GameObject,NameDisplayScreen.Entry>>(
                AccessTools.Method(typeof(NameDisplayScreen),"GetEntry"));
        HealthBar bar;
        short last=-2;
        float Fraction()=>Mathf.Max(0,last)*.001f;

        internal static short Capture(KBatchedAnimController body)
        {
            var health=body==null?null:body.GetComponent<Health>();
            return IsShown(health) ? ToFill(health.percent()) : (short)-1;
        }

        internal static bool IsShown(Health health) =>
            health!=null && health.healthBar!=null && health.healthBar.gameObject.activeSelf &&
            !health.IsIncapacitated() && health.hitPoints>0 && health.hitPoints<health.maxHitPoints &&
            !health.HasTag("HideHealthBar");

        internal static short ToFill(float fraction) => (short)Mathf.RoundToInt(1000*Mathf.Clamp01(fraction));

        internal static void Apply(KBatchedAnimController body,short fill,bool selectable=true)
        {
            var display=NameDisplayScreen.Instance;
            if(display==null) return;
            var binding=body.GetComponent<NativeDupeHealth>();
            if(binding==null)
            {
                if(fill<0 && body.GetComponent<Health>()?.healthBar?.gameObject.activeSelf!=true) return;
                binding=body.gameObject.AddComponent<NativeDupeHealth>();
            }
            if(binding.bar==null)
            {
                var entry=entryFor(display,body.gameObject);
                if(entry==null)
                {
                    if(fill<0) return;
                    var overlay=body.GetComponent<CharacterOverlay>();
                    if(overlay!=null) overlay.Register();
                    else display.AddNewEntry(body.gameObject);
                    entry=entryFor(display,body.gameObject);
                    if(entry==null) return;
                }
                if(entry.healthBar==null)
                {
                    entry.bars_go=entry.refs.GetReference<Transform>("Bars").gameObject;
                    var obj=Util.KInstantiateUI(ProgressBarsConfig.Instance.healthBarPrefab,entry.bars_go);
                    entry.healthBar=obj.GetComponent<HealthBar>();
                    entry.healthBar.autoHide=false;
                    obj.GetComponent<KSelectable>().entityName=STRINGS.UI.METERS.HEALTH.TOOLTIP;
                    obj.GetComponent<KSelectableHealthBar>().IsSelectable=selectable;
                    obj.transform.Find("Bar").GetComponent<Image>().color=ProgressBarsConfig.Instance.GetBarColor("HealthBar");
                }
                binding.bar=entry.healthBar;
            }
            if(binding.last==fill && binding.bar.gameObject.activeSelf==(fill>=0)) return;
            binding.last=fill;
            binding.bar.PercentFull=binding.Fraction();
            display.SetHealthDisplay(body.gameObject,binding.Fraction,fill>=0);
        }
        internal void Hide()
        {
            if(bar!=null) bar.SetVisibility(false);
            last=-2;
        }
    }
}
