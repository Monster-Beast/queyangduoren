using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeBuriedVisuals
    {
        static readonly FieldInfo cells=AccessTools.Field(typeof(EntombedItemVisualizer),"cellEntombedCounts");
        static readonly FieldInfo renderer=AccessTools.Field(AccessTools.Inner(typeof(EntombedItemVisualizer),"Data"),"controller");
        static KBatchedAnimController Get(EntombedItemVisualizer owner,int cell)
        {
            var data=((IDictionary)cells.GetValue(owner))[cell];
            return data==null ? null : (KBatchedAnimController)renderer.GetValue(data);
        }
        internal static void Capture(EntombedItemVisualizer owner,int cell,ref ColonyCell sample)
        {
            if(!owner.IsEntombedItem(cell)) return;
            sample.Flags|=ColonyView.EntombedFlag;
            var art=Get(owner,cell);
            if(art==null) return;
            sample.BuriedAnimation=art.currentAnim.HashValue;
            sample.BuriedRotation=(ushort)Mathf.RoundToInt(art.transform.eulerAngles.z*65535f/360f);
        }
        internal static void Apply(EntombedItemVisualizer owner,int cell,ColonyCell sample,bool appearance)
        {
            bool required=(sample.Flags&ColonyView.EntombedFlag)!=0;
            bool present=owner.IsEntombedItem(cell);
            if(!required) { if(present)owner.ForceClear(cell);return; }
            if(!present && !owner.AddItem(cell)) return;
            if(!appearance) return;
            var art=Get(owner,cell);
            if(art==null) return;
            var name=new HashedString(sample.BuriedAnimation);
            if(art.currentAnim!=name && art.HasAnimation(name)) art.Play(name,KAnim.PlayMode.Loop);
            float angle=sample.BuriedRotation*360f/65535f;
            if(Mathf.Abs(Mathf.DeltaAngle(art.transform.eulerAngles.z,angle))>.003f)
            { art.transform.rotation=Quaternion.Euler(0,0,angle);art.SetDirty(); }
        }
    }
}
