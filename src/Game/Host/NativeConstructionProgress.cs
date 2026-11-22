using System;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeConstructionProgress
    {
        static readonly AccessTools.FieldRef<Workable,ProgressBar> barOf=
            AccessTools.FieldRefAccess<Workable,ProgressBar>("progressBar");

        internal static ProgressBar Source(Constructable plan)=>barOf(plan);

        internal static void Apply(KSelectable target,ColonyEntity state,Func<float> fraction,ref ProgressBar bar)
        {
            // Any native bar on the temporary save reads its frozen local Workable.
            var original=target.GetComponent<Constructable>();
            if(original!=null && Source(original)!=null) original.ShowProgressBar(false);
            if((state.Flags&ColonyEntity.ConstructionBar)==0)
            {
                Clear(ref bar);
                return;
            }
            if(bar==null)
            {
                var def=Assets.GetBuildingDef(target.PrefabID().Name);
                // The proxy has no Building component. ONI otherwise gives it the
                // default one-cell center, which would offset an even-width plan.
                float correction=original==null && def!=null && def.WidthInCells%2==0?.5f:0;
                bar=ProgressBar.CreateProgressBar(target.gameObject,fraction,new Vector3(correction,0,0));
                bar.SetVisibility(true);
            }
            bar.PercentFull=state.Value;
        }

        internal static void Clear(ref ProgressBar bar)
        {
            if(bar==null) return;
            bar.SetVisibility(false);
            UnityEngine.Object.Destroy(bar.gameObject);
            bar=null;
        }
    }
}
