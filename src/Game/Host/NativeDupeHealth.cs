using System;
using HarmonyLib;
using UnityEngine;

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
            var h=body==null?null:body.GetComponent<Health>();
            if(h==null || h.healthBar==null || !h.healthBar.gameObject.activeSelf || h.IsIncapacitated() ||
                h.hitPoints<=0 || h.hitPoints>=h.maxHitPoints || body.HasTag("HideHealthBar")) return -1;
            return (short)Mathf.RoundToInt(1000*Mathf.Clamp01(h.percent()));
        }

        internal static void Apply(KBatchedAnimController body,short fill)
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
                }
                if(entry.healthBar==null)
                {
                    entry.bars_go=entry.refs.GetReference<Transform>("Bars").gameObject;
                    var obj=Util.KInstantiateUI(ProgressBarsConfig.Instance.healthBarPrefab,entry.bars_go);
                    entry.healthBar=obj.GetComponent<HealthBar>();
                    entry.healthBar.autoHide=false;
                    obj.GetComponent<KSelectable>().entityName=STRINGS.UI.METERS.HEALTH.TOOLTIP;
                    obj.GetComponent<KSelectableHealthBar>().IsSelectable=true;
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
