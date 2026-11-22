using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal sealed class NativeBuildingMeters : MonoBehaviour
    {
        sealed class Entry
        {
            internal MeterController Meter;
            internal ColonyMeter Recipe;
            internal readonly Dictionary<int,uint> Tints=new Dictionary<int,uint>();
            internal bool Proxy;
        }
        readonly List<Entry> slots=new List<Entry>();
        static readonly ConditionalWeakTable<MeterController,Entry> byController=new ConditionalWeakTable<MeterController,Entry>();
        static readonly List<ColonyMeter> rows=new List<ColonyMeter>();
        static readonly AccessTools.FieldRef<KBatchedAnimController,bool> rebuild=AccessTools.FieldRefAccess<KBatchedAnimController,bool>("_forceRebuild");
        static bool creatingReplica;

        internal static IEnumerable<KeyValuePair<ushort,KBatchedAnimController>> MovingParts(GameObject building)
        {
            var group=building.GetComponent<NativeBuildingMeters>();
            if(group==null) yield break;
            for(int i=0;i<group.slots.Count && i<256;i++)
            {
                var anim=group.slots[i]?.Meter?.meterController;
                if(anim!=null) yield return new KeyValuePair<ushort,KBatchedAnimController>((ushort)(i+1),anim);
            }
        }

        internal static KBatchedAnimController GetPart(KSelectable building,ushort part)
        {
            var group=building.GetComponent<NativeBuildingMeters>();
            int index=part-1;
            return group!=null && index>=0 && index<group.slots.Count?group.slots[index]?.Meter?.meterController:null;
        }

        [HarmonyPatch(typeof(MeterController),"Initialize")]
        static class Observe
        {
            static void Postfix(MeterController __instance,KAnimControllerBase __0,string __1,string __2,Meter.Offset __3,
                Grid.SceneLayer __4,Vector3 __5,string[] __6)
            {
                if (creatingReplica || __0==null || __instance.meterController==null || !__0.TryGetComponent<Building>(out _) ||
                    NativeFacilityStorage.HasKnownMeter(__0.gameObject) || string.IsNullOrEmpty(__2)) return;
                var group=__0.GetComponent<NativeBuildingMeters>()??__0.gameObject.AddComponent<NativeBuildingMeters>();
                var recipe=new ColonyMeter{Target=__1??"",Clip=__2,Offset=(byte)__3,Layer=(int)__4,X=__5.x,Y=__5.y,Z=__5.z,
                    Hidden=__6??Array.Empty<string>()};
                if(!recipe.SupportsRecipe())return;
                group.slots.Add(new Entry{Meter=__instance,Recipe=recipe});
                byController.Add(__instance,group.slots[group.slots.Count-1]);
            }
        }
        [HarmonyPatch(typeof(MeterController),nameof(MeterController.SetSymbolTint))]
        static class ObserveTint
        {
            static void Postfix(MeterController __instance,KAnimHashedString __0,Color32 __1)
            {
                if(byController.TryGetValue(__instance,out var e)) e.Tints[__0.HashValue]=NativeOrderColors.Pack(__1);
            }
        }
        internal static void Capture(ColonyView v,int sequence)
        {
            rows.Clear();
            for(int i=0;i<v.Cells.Length;i++)
            {
                int id=v.Cells[i].BuildingSaveId;
                if(id<=0) continue;
                var obj=KPrefabIDTracker.Get().GetInstance(id);
                if(obj==null || !obj.TryGetComponent<NativeBuildingMeters>(out var group)) continue;
                for(int j=0;j<group.slots.Count && j<256;j++)
                {
                    var e=group.slots[j];var a=e.Meter?.meterController;
                    if(a==null || e.Recipe.Hidden.Length>32 || e.Tints.Count>16) continue;
                    var row=e.Recipe.Copy();row.Key=i*256+j;
                    row.Animation=a.currentAnim.HashValue;row.Rotation=a.Rotation;
                    float f=a.GetPositionPercent();row.Position=float.IsNaN(f)?0f:Mathf.Clamp01(f);
                    row.Visible=a.gameObject.activeInHierarchy && a.IsVisible();
                    if(e.Tints.Count>0)
                    {
                        var colors=e.Tints.ToArray();row.TintSymbols=colors.Select(p=>p.Key).ToArray();row.Tints=colors.Select(p=>p.Value).ToArray();
                    }
                    rows.Add(row);
                }
            }
            v.SelectMeterPage(rows,sequence<0?Time.frameCount:sequence);
        }

        internal static void Apply(ColonyView v)
        {
            if(v.Meters==null)return;
            int next=0;
            for(int i=v.MeterFirst/256;i<=v.MeterLast/256;i++)
            {
                int start=next;
                while(next<v.Meters.Length && v.Meters[next].Key/256==i)next++;
                int id=v.Cells[i].BuildingSaveId;
                if(id<=0)continue;
                int x=v.X+i%ColonyView.Width,y=v.Y+i/ColonyView.Width;
                if(x>=Grid.WidthInCells || y>=Grid.HeightInCells)continue;
                var obj=NativeWorldMirror.GetBuildingSelectable(Grid.XYToCell(x,y),id);
                if(obj==null)continue;
                var group=obj.GetComponent<NativeBuildingMeters>();
                if(group==null && start==next)continue;
                if(group==null)group=obj.gameObject.AddComponent<NativeBuildingMeters>();
                int cursor=start;
                for(int j=Math.Max(0,v.MeterFirst-i*256);j<=Math.Min(255,v.MeterLast-i*256);j++)
                {
                    if(cursor<next && v.Meters[cursor].Key%256==j)group.Show(j,v.Meters[cursor++],v.DupePoseTick);
                    else if(j<group.slots.Count) group.slots[j]?.Meter?.meterController?.SetVisiblity(false);
                    else if(cursor==next) break;
                }
            }
        }
        void Show(int slot,ColonyMeter s,int tick)
        {
            while(slots.Count<=slot)slots.Add(null);
            var e=slots[slot];
            if(e==null || e.Meter.meterController==null || e.Recipe.Target!=s.Target || e.Recipe.Clip!=s.Clip)
            {
                var a=GetComponent<KBatchedAnimController>();
                if(a==null || a.AnimFiles==null || a.AnimFiles.Length==0 || !a.HasAnimation(s.Clip))return;
                if(e!=null && e.Meter.meterController!=null)
                {
                    e.Meter.meterController.SetVisiblity(false);
                    if(e.Proxy) {e.Meter.Unlink();Destroy(e.Meter.gameObject);}
                }
                MeterController meter;
                creatingReplica=true;
                try {meter=new MeterController(a,s.Target,s.Clip,(Meter.Offset)s.Offset,(Grid.SceneLayer)s.Layer,new Vector3(s.X,s.Y,s.Z),s.Hidden);}
                finally {creatingReplica=false;}
                e=new Entry{Meter=meter,Recipe=s,Proxy=true};slots[slot]=e;
            }
            var anim=e.Meter.meterController;
            anim.SetVisiblity(s.Visible);
            bool owns=NativeBuildingAnimation.Owns(anim,tick);
            if(!owns && s.Animation!=0 && anim.currentAnim.HashValue!=s.Animation && anim.HasAnimation(new HashedString(s.Animation)))
                anim.Play(new HashedString(s.Animation),KAnim.PlayMode.Paused);
            anim.Rotation=s.Rotation;
            var clip=anim.GetCurrentAnim();
            if(!owns && clip!=null)
            {
                anim.SetPositionPercent(s.Position);
                int absolute=clip.firstFrameIdx+clip.GetFrameIdx(anim.PlayMode,anim.GetElapsedTime());
                if(anim.GetCurrentFrameIndex()!=absolute)
                {
                    // UpdateAnim returns early for paused KAnim meters unless the native
                    // force-rebuild flag is set. A UI value can be correct while the GPU stays at 0.
                    rebuild(anim)=true;anim.UpdateAnim(0f);
                }
            }
            for(int i=0;i<s.Tints.Length;i++)anim.SetSymbolTint(new KAnimHashedString(s.TintSymbols[i]),NativeOrderColors.Unpack(s.Tints[i]));
        }
        internal void Hide()
        {
            foreach(var slot in slots) if(slot?.Meter.meterController!=null)slot.Meter.meterController.SetVisiblity(false);
        }
        void OnDestroy()
        {
            foreach(var slot in slots)if(slot!=null && slot.Proxy)slot.Meter.Unlink();
        }
    }
}
