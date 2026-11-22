using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeBuildingWork
    {
        sealed class Display
        {
            internal int Owner,Cell;
            internal KSelectable Target;
            internal float Fill;
            internal ProgressBar Bar;
            internal float Read()=>Fill;
        }
        // Retarget is called by both Workable and independent producers such as
        // ComplexFabricator. Their bars live on worldSpaceCanvas, outside the building.
        static readonly Dictionary<ProgressBar,Building> sources=new Dictionary<ProgressBar,Building>();
        static readonly Dictionary<int,Display> displays=new Dictionary<int,Display>();
        static readonly AccessTools.FieldRef<ProgressBar,bool> requested=AccessTools.FieldRefAccess<ProgressBar,bool>("lastVisibilityValue");
        static readonly AccessTools.FieldRef<ProgressBar,bool> autoHide=AccessTools.FieldRefAccess<ProgressBar,bool>("autoHide");
        static readonly AccessTools.FieldRef<ProgressBar,Func<float>> update=AccessTools.FieldRefAccess<ProgressBar,Func<float>>("updatePercentFull");
        static bool creating;

        [HarmonyPatch(typeof(ProgressBar),"Retarget")]
        static class TargetChanged
        {
            static void Postfix(ProgressBar __instance,GameObject __0)
            {
                if(creating) return;
                var owner=__0?.GetComponentInParent<Building>();
                if(owner!=null && owner.GetComponent<Constructable>()==null) sources[__instance]=owner;
                else sources.Remove(__instance);
            }
        }
        [HarmonyPatch(typeof(ProgressBar),"OnCleanUp")]
        static class Removed
        {
            static void Prefix(ProgressBar __instance)=>sources.Remove(__instance);
        }

        static bool TryFill(ProgressBar bar,out float amount)
        {
            amount=0;
            if(bar==null || !requested(bar)) return false;
            var read=update(bar);
            if(read==null || Util.IsNullOrDestroyed(read.Target)) return false;
            amount=read();
            if(float.IsNaN(amount) || float.IsInfinity(amount)) return false;
            amount=Mathf.Clamp01(amount);return true;
        }

        internal static void Capture(ColonyView view,WorldContainer world,int sequence)
        {
            if(view.LightMask==null) return;
            var rows=new List<ColonyWorkBar>();
            foreach(var pair in sources)
            {
                var b=pair.Value;var bar=pair.Key;
                if(b==null || !b.gameObject.activeInHierarchy) continue;
                int cell=b.GetCell();
                if(!Grid.IsValidCell(cell) || Grid.WorldIdx[cell]!=world.id) continue;
                Grid.CellToXY(cell,out int x,out int y);
                x-=view.X;y-=view.Y;
                if((uint)x>=ColonyView.Width || (uint)y>=ColonyView.Height) continue;
                int i=y*ColonyView.Width+x;
                if((view.Cells[i].Flags&1)==0 || !TryFill(bar,out float amount)) continue;
                int owner=b.GetComponent<KPrefabID>()?.InstanceID??0;
                if(owner<=0) continue;
                Vector3 pos=bar.transform.position;Color32 tint=bar.barColor;
                rows.Add(new ColonyWorkBar{Cell=(ushort)i,Owner=owner,Token=bar.GetInstanceID(),Fill=amount,
                    X=pos.x,Y=pos.y,Z=pos.z,AutoHide=autoHide(bar),Color=(uint)(tint.r|tint.g<<8|tint.b<<16|tint.a<<24)});
            }
            view.SetWorkBars(rows,sequence>=0?sequence:Mathf.FloorToInt(Time.unscaledTime));
        }

        internal static void AddSelectedReadings(GameObject target,List<string> lines)
        {
            var building=target.GetComponent<Building>();
            if(building==null || target.GetComponent<Constructable>()!=null) return;
            string label=target.GetComponent<ResearchCenter>()!=null?"研究进度":target.GetComponent<ComplexFabricator>()!=null?"配方进度":"工作进度";
            int count=0;
            foreach(var pair in sources)
                if(pair.Value==building && TryFill(pair.Key,out float fraction))
                {
                    count++;
                    lines.Add(label+(count>1?" "+count:"")+" "+fraction.ToString("P1"));
                }
            if(count>0) return;
            // Workables with a finite task can still expose a reading in the sidebar
            // when their own prefab intentionally hides the world-space progress bar.
            foreach(var w in target.GetComponentsInChildren<Workable>())
            {
                if(w.worker==null || w.GetWorkTime()<=0 || w.GetWorkTime()==float.PositiveInfinity) continue;
                float value=w.GetPercentComplete();
                if(value>=0 && value<=1) lines.Add(label+" "+value.ToString("P1")+" · "+w.worker.gameObject.GetProperName());
            }
        }

        static KSelectable Find(int cell,int owner)
        {
            var target=NativeWorldMirror.GetBuildingSelectable(cell,owner);
            if(target!=null) return target;
            var local=KPrefabIDTracker.Get().GetInstance(owner)?.GetComponent<Building>();
            return local!=null && local.GetCell()==cell ? local.GetComponent<KSelectable>() : null;
        }

        internal static void Apply(ColonyView view)
        {
            if(view.WorkBars==null) return;
            foreach(var source in sources.Keys)
                if(source!=null && requested(source)) source.SetVisibility(false);
            var current=new HashSet<int>();
            foreach(var state in view.WorkBars)
            {
                int cell=Grid.XYToCell(view.X+state.Cell%ColonyView.Width,view.Y+state.Cell/ColonyView.Width);
                var target=Find(cell,state.Owner);
                if(target==null) continue;
                current.Add(state.Token);
                if(!displays.TryGetValue(state.Token,out var d) || d.Target!=target || d.Bar==null)
                {
                    Remove(state.Token);
                    displays[state.Token]=d=new Display{Owner=state.Owner,Cell=cell,Target=target,Fill=state.Fill};
                    creating=true;
                    try { d.Bar=ProgressBar.CreateProgressBar(target.gameObject,d.Read); }
                    finally { creating=false; }
                }
                d.Fill=state.Fill;
                autoHide(d.Bar)=state.AutoHide;
                d.Bar.transform.position=new Vector3(state.X,state.Y,state.Z);
                d.Bar.barColor=new Color32((byte)state.Color,(byte)(state.Color>>8),(byte)(state.Color>>16),(byte)(state.Color>>24));
                // CreateProgressBar initializes the widget before Retarget supplies
                // its world position. ONI can hide it until visibility is recomputed.
                if(!requested(d.Bar) || !d.Bar.gameObject.activeSelf) d.Bar.SetVisibility(true);
                d.Bar.PercentFull=state.Fill;
            }
            foreach(var p in displays.ToArray())
            {
                if(current.Contains(p.Key)) continue;
                var d=p.Value;
                Grid.CellToXY(d.Cell,out int x,out int y);x-=view.X;y-=view.Y;
                if(d.Target==null || (uint)x>=ColonyView.Width || (uint)y>=ColonyView.Height) { Remove(p.Key);continue; }
                long key=((long)(y*ColonyView.Width+x)<<32)|(uint)p.Key;
                if(key>=view.WorkBarFrom && key<=view.WorkBarThrough) Remove(p.Key);
            }
        }
        static void Remove(int token)
        {
            if(!displays.TryGetValue(token,out var d)) return;
            NativeConstructionProgress.Clear(ref d.Bar);displays.Remove(token);
        }
        internal static ProgressBar GetDisplayed(int buildingSaveId,int index=0)=>displays.OrderBy(p=>p.Key)
            .Where(p=>p.Value.Owner==buildingSaveId).Select(p=>p.Value.Bar).Skip(index).FirstOrDefault();
        internal static void Reset()
        {
            foreach(var d in displays.Values) NativeConstructionProgress.Clear(ref d.Bar);
            displays.Clear();
            foreach(var p in sources.Where(p=>p.Key==null || p.Value==null).ToArray()) sources.Remove(p.Key);
        }
    }
}
