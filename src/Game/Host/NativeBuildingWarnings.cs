using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeBuildingWarnings
    {
        sealed class Mirror
        {
            internal readonly Dictionary<int,Guid> Current=new Dictionary<int,Guid>();
        }
        static readonly AccessTools.FieldRef<StatusItemGroup,Vector3> offset=
            AccessTools.FieldRefAccess<StatusItemGroup,Vector3>("offset");
        static readonly Dictionary<int,StatusItem> native=new Dictionary<int,StatusItem>();
        static readonly Dictionary<int,StatusItem> projected=new Dictionary<int,StatusItem>();
        static ConditionalWeakTable<StatusItemGroup,Mirror> managed=new ConditionalWeakTable<StatusItemGroup,Mirror>();
        static int Hash(StatusItem item)=>new HashedString(item.Id).HashValue;

        static void EnsureIcons()
        {
            if(native.Count!=0) return;
            foreach(var status in Db.Get().BuildingStatusItems.resources)
                if(status.ShouldShowIcon()) native[Hash(status)]=status;
        }

        internal static void Capture(ColonyView view,int sequence)
        {
            if(view.LightMask==null) return;
            EnsureIcons();
            var rows=new List<ColonyBuildingWarning>();
            for(int i=0;i<view.Cells.Length;i++)
            {
                var sample=view.Cells[i];
                if((sample.Flags&1)==0 || sample.BuildingSaveId<=0 || sample.AnchorDX!=0 || sample.AnchorDY!=0) continue;
                var group=KPrefabIDTracker.Get().GetInstance(sample.BuildingSaveId)?.GetComponent<KSelectable>()?.GetStatusItemGroup();
                if(group==null || group.Count==0) continue;
                var ids=Icons(group).Select(e=>Hash(e.item)).Distinct().Take(12).ToArray();
                if(ids.Length==0) continue;
                var v=offset(group);
                rows.Add(new ColonyBuildingWarning {Cell=(ushort)i,Icons=ids,X=v.x,Y=v.y,Z=v.z});
            }
            view.SetBuildingWarnings(rows,sequence>=0?sequence:Mathf.FloorToInt(Time.unscaledTime));
        }

        static IEnumerable<StatusItemGroup.Entry> Icons(StatusItemGroup group)
        {
            foreach(var entry in group)
                if(entry.item.ShouldShowIcon() && native.ContainsKey(Hash(entry.item))) yield return entry;
        }

        internal static void Apply(ColonyView view)
        {
            if(view.BuildingWarnings==null) return;
            EnsureIcons();
            int next=0;
            for(int i=view.WarningFirst;i<=view.WarningLast;i++)
            {
                var row=next<view.BuildingWarnings.Length && view.BuildingWarnings[next].Cell==i?view.BuildingWarnings[next++]:null;
                var sample=view.Cells[i];
                if((sample.Flags&1)==0 || sample.BuildingSaveId<=0 || sample.AnchorDX!=0 || sample.AnchorDY!=0) continue;
                int x=view.X+i%ColonyView.Width,y=view.Y+i/ColonyView.Width;
                if(x>=Grid.WidthInCells || y>=Grid.HeightInCells) continue;
                var target=NativeWorldMirror.GetBuildingSelectable(Grid.XYToCell(x,y),sample.BuildingSaveId);
                if(target==null) continue;
                var group=target.GetStatusItemGroup();
                if(group==null) continue;
                if(!managed.TryGetValue(group,out var mirror))
                {
                    mirror=managed.GetOrCreateValue(group);
                    foreach(var stale in Icons(group).ToArray()) target.RemoveStatusItem(stale.id,true);
                }
                foreach(int id in mirror.Current.Keys.ToArray())
                {
                    if(row!=null && Array.IndexOf(row.Icons,id)>=0) continue;
                    target.RemoveStatusItem(mirror.Current[id],true);mirror.Current.Remove(id);
                }
                if(row==null) continue;
                var def=target.GetComponent<Building>()==null?Assets.GetBuildingDef(target.PrefabID().Name):null;
                float center=def==null?0:((def.WidthInCells-1)%2)*.5f;
                target.SetStatusIndicatorOffset(new Vector3(row.X+center,row.Y,row.Z));
                foreach(int id in row.Icons)
                {
                    if(mirror.Current.ContainsKey(id) || !native.TryGetValue(id,out var original)) continue;
                    Guid handle=target.AddStatusItem(DisplayIcon(original));
                    if(handle!=Guid.Empty) mirror.Current[id]=handle;
                }
            }
        }

        static StatusItem DisplayIcon(StatusItem original)
        {
            int id=Hash(original);
            if(!projected.TryGetValue(id,out var icon))
            {
                // Retain native art and overlay filters without creating a local notification.
                icon=new StatusItem(original.Id,original.Name,original.tooltipText,original.iconName,
                    original.iconType,original.notificationType,false,original.render_overlay,original.status_overlays);
                icon.sprite=original.sprite;
                icon.unique=original.unique;
                projected[id]=icon;
            }
            return icon;
        }

        internal static void ApplyExcavationPriority(KSelectable target,bool urgent)
        {
            var group=target?.GetStatusItemGroup();
            if(group==null) return;
            var original=Db.Get().BuildingStatusItems.EmergencyPriority;
            if(group.HasStatusItem(original)) target.RemoveStatusItem(original,true);
            if(urgent)
            {
                var icon=DisplayIcon(original);
                if(!group.HasStatusItem(icon)) target.AddStatusItem(icon);
            }
            else if(projected.TryGetValue(Hash(original),out var icon) && group.HasStatusItem(icon))
                target.RemoveStatusItem(icon,true);
        }

        static bool AllowLocal(StatusItemGroup group,StatusItem item)=>!NativeGuestScene.Ready ||
            item==null || !item.ShouldShowIcon() || !managed.TryGetValue(group,out _) ||
            !native.TryGetValue(Hash(item),out var status) || !ReferenceEquals(status,item);

        [HarmonyPatch(typeof(StatusItemGroup),nameof(StatusItemGroup.AddStatusItem))]
        static class SuppressLocalShow
        {
            static bool Prefix(StatusItemGroup __instance,StatusItem __0)=>AllowLocal(__instance,__0);
        }
        [HarmonyPatch(typeof(StatusItemGroup),nameof(StatusItemGroup.RemoveStatusItem),new[]{typeof(StatusItem),typeof(bool)})]
        static class SuppressLocalHide
        {
            static bool Prefix(StatusItemGroup __instance,StatusItem __0)=>AllowLocal(__instance,__0);
        }

        internal static void Hide(KSelectable target)
        {
            var group=target?.GetStatusItemGroup();
            if(group==null || !managed.TryGetValue(group,out var mirror)) return;
            foreach(Guid handle in mirror.Current.Values) target.RemoveStatusItem(handle,true);
            mirror.Current.Clear();
        }

        internal static void Reset()
        {
            managed=new ConditionalWeakTable<StatusItemGroup,Mirror>();
            native.Clear(); projected.Clear();
        }
    }
}
