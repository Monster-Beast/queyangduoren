using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    // Read-only UI data. Never add Storage, Workable, or Operational to a display proxy.
    internal sealed class NativeFacilityStorage : MonoBehaviour
    {
        internal sealed class Row
        {
            internal int Tag;
            internal float Mass, Available, Units, Calories, AvailableCalories;
            internal bool CountUnits;
        }
        internal sealed class Snapshot
        {
            internal readonly SortedDictionary<int,Row> Rows=new SortedDictionary<int,Row>();
            internal float Capacity, Limit, Stored, Available, Clean, Waste, PerUse;
            internal bool Wash, Ready, Operational, Active, Full;
            internal int Page, Pages, Total;
            internal void Add(GameObject item,PrimaryElement primary,int tag,float available)
            {
                if(!Rows.TryGetValue(tag,out var row)) Rows.Add(tag,row=new Row { Tag=tag, CountUnits=primary.MassPerUnit>1 });
                float mass=primary.Mass, calories=item.GetComponent<Edible>()?.Calories??0;
                row.Mass+=mass; row.Available+=available; row.Units+=primary.Units;
                row.Calories+=calories; row.AvailableCalories+=mass>0?calories*(available/mass):0;
            }
        }

        internal static bool Supported(GameObject obj)
        {
            if(obj==null || obj.GetComponent<Building>()==null) return false;
            if(obj.GetComponent<HandSanitizer>()!=null) return true;
            foreach(var store in obj.GetComponentsInChildren<IStorage>())
                if(store.ShouldShowInUI()) return true;
            return false;
        }

        static bool HasKnownMeter(GameObject obj) => obj!=null && (obj.GetComponent<HandSanitizer>()!=null ||
            obj.GetComponent<RationBox>()!=null || obj.GetComponent<Refrigerator>()!=null);

        internal static string Encode(GameObject obj,int page)
        {
            var state=new Snapshot();
            var e=new ColonyEntity{Kind=ColonyEntityKind.Storage};
            NativeEntitySampler.ReadStorage(obj,e,state);
            state.Stored=e.Value; state.Capacity=e.Limit; state.Available=e.Available;
            var setting=obj.GetComponent<IUserControlledCapacity>();
            state.Limit=setting!=null && setting.ControlEnabled()?Mathf.Clamp(setting.UserMaxCapacity,0,state.Capacity):state.Capacity;
            var op=obj.GetComponent<Operational>(); state.Operational=op==null || op.IsOperational; state.Active=op!=null && op.IsActive;
            var wash=obj.GetComponent<HandSanitizer>(); state.Wash=wash!=null;
            if(wash!=null)
            {
                var store=obj.GetComponent<Storage>();
                state.Clean=store.FindPrimaryElement(wash.consumedElement)?.Mass??0;
                state.Waste=store.FindPrimaryElement(wash.outputElement)?.Mass??0;
                state.PerUse=wash.massConsumedPerUse;
                state.Ready=wash.smi!=null && wash.smi.IsReady(); state.Full=wash.smi!=null && wash.smi.OutputFull();
            }
            const int pageSize=64;
            int pages=Math.Max(1,(state.Rows.Count+pageSize-1)/pageSize);
            if(page<0 || page>=pages) page=0;
            using(var stream=new MemoryStream())
            using(var w=new BinaryWriter(stream))
            {
                w.Write((byte)1); w.Write(page); w.Write(pages); w.Write(state.Rows.Count);
                w.Write(state.Capacity); w.Write(state.Limit); w.Write(state.Stored); w.Write(state.Available);
                w.Write(state.Wash);w.Write(state.Operational);w.Write(state.Active);w.Write(state.Ready);w.Write(state.Full);
                w.Write(state.Clean);w.Write(state.Waste);w.Write(state.PerUse);
                int index=0;
                foreach(var row in state.Rows.Values)
                {
                    if(index++<page*pageSize) continue;
                    if(index>(page+1)*pageSize) break;
                    w.Write(row.Tag); w.Write(row.CountUnits); w.Write(row.Mass); w.Write(row.Available);
                    w.Write(row.Units); w.Write(row.Calories); w.Write(row.AvailableCalories);
                }
                return Convert.ToBase64String(stream.ToArray()).Replace('/','_');
            }
        }
        static float Amount(BinaryReader r)
        {
            float f=r.ReadSingle();
            if(!(f>=0 && f<=float.MaxValue)) throw new InvalidDataException("Invalid building storage reading");
            return f;
        }
        internal static Snapshot Decode(string packed)
        {
            if(packed.Length>4096) throw new InvalidDataException("Storage page too large");
            var s=new Snapshot();
            using(var r=new BinaryReader(new MemoryStream(Convert.FromBase64String(packed.Replace('_','/')))))
            {
                if(r.ReadByte()!=1) throw new InvalidDataException("Unknown building storage format");
                s.Page=r.ReadInt32();s.Pages=r.ReadInt32();s.Total=r.ReadInt32();
                if(s.Total<0 || s.Total>65536 || s.Pages!=Math.Max(1,(s.Total+63)/64) || s.Page<0 || s.Page>=s.Pages)
                    throw new InvalidDataException("Bad building storage page");
                s.Capacity=Amount(r);s.Limit=Amount(r);s.Stored=Amount(r);s.Available=Amount(r);
                s.Wash=r.ReadBoolean();s.Operational=r.ReadBoolean();s.Active=r.ReadBoolean();s.Ready=r.ReadBoolean();s.Full=r.ReadBoolean();
                s.Clean=Amount(r);s.Waste=Amount(r);s.PerUse=Amount(r);
                int count=Math.Min(64,s.Total-s.Page*64);
                for(int n=0;n<count;n++)
                {
                    var row=new Row { Tag=r.ReadInt32(),CountUnits=r.ReadBoolean(),Mass=Amount(r),Available=Amount(r),
                        Units=Amount(r),Calories=Amount(r),AvailableCalories=Amount(r) };
                    s.Rows.Add(row.Tag,row);
                }
                if(r.BaseStream.Position!=r.BaseStream.Length) throw new InvalidDataException("Trailing building storage data");
            }
            return s;
        }

        static readonly AccessTools.FieldRef<HandSanitizer,MeterController> CleanMeter=AccessTools.FieldRefAccess<HandSanitizer,MeterController>("cleanMeter");
        static readonly AccessTools.FieldRef<HandSanitizer,MeterController> WasteMeter=AccessTools.FieldRefAccess<HandSanitizer,MeterController>("dirtyMeter");
        static readonly AccessTools.FieldRef<FilteredStorage,MeterController> FoodMeter=AccessTools.FieldRefAccess<FilteredStorage,MeterController>("meter");
        static readonly AccessTools.FieldRef<RationBox,FilteredStorage> Box=AccessTools.FieldRefAccess<RationBox,FilteredStorage>("filteredStorage");
        static readonly AccessTools.FieldRef<Refrigerator,FilteredStorage> Fridge=AccessTools.FieldRefAccess<Refrigerator,FilteredStorage>("filteredStorage");
        static readonly Func<FilteredStorage,float> FoodAmount=AccessTools.MethodDelegate<Func<FilteredStorage,float>>(AccessTools.DeclaredMethod(typeof(FilteredStorage),"GetAmountStored"));
        static readonly Func<FilteredStorage,float> FoodCapacity=AccessTools.MethodDelegate<Func<FilteredStorage,float>>(AccessTools.DeclaredMethod(typeof(FilteredStorage),"GetMaxCapacityMinusStorageMargin"));

        internal static byte SampleFoodMeter(GameObject obj)
        {
            var box=obj.GetComponent<RationBox>();
            var fridge=obj.GetComponent<Refrigerator>();
            var store=box!=null ? Box(box) : fridge!=null ? Fridge(fridge) : null;
            if(store==null || FoodMeter(store)==null) return byte.MaxValue;
            float amount=FoodAmount(store), limit=FoodCapacity(store);
            float fraction=limit>0 ? Mathf.Clamp01(amount/limit) : amount>0 ? 1 : 0;
            return (byte)Mathf.RoundToInt(fraction*254);
        }
        MeterController clean,dirty,food;
        bool initialized;
        internal static void ApplyMeter(KSelectable target,ColonyEntity e)
        {
            if(e.Kind!=ColonyEntityKind.Storage) return;
            var prefab=Assets.TryGetPrefab(new Tag(e.Prefab));
            if(!HasKnownMeter(prefab)) return;
            var display=target.GetComponent<NativeFacilityStorage>()??target.gameObject.AddComponent<NativeFacilityStorage>();
            display.SetMeter(prefab,e);
        }
        void SetMeter(GameObject prefab,ColonyEntity e)
        {
            var wash=prefab.GetComponent<HandSanitizer>();
            if(!initialized)
            {
                var local=GetComponent<HandSanitizer>(); var box=GetComponent<RationBox>();var fridge=GetComponent<Refrigerator>();
                if(local!=null) {clean=CleanMeter(local);dirty=WasteMeter(local);}
                else if(box!=null || fridge!=null)
                {
                    var f=box!=null?Box(box):Fridge(fridge);
                    if(f!=null) food=FoodMeter(f);
                }
                else
                {
                    var a=GetComponent<KBatchedAnimController>();
                    if(wash!=null && wash.hasMeters)
                    {
                        clean=new MeterController(a,"meter_clean_target","meter_clean",wash.cleanMeterOffset,Grid.SceneLayer.NoLayer,"meter_clean_target");
                        dirty=new MeterController(a,"meter_dirty_target","meter_dirty",wash.dirtyMeterOffset,Grid.SceneLayer.NoLayer,"meter_dirty_target");
                    }
                    else if(wash==null && a.AnimFiles.Length>0 && a.AnimFiles[0].GetData().build?.GetSymbol("meter_target")!=null)
                        food=new MeterController(a,"meter_target","meter",Meter.Offset.Infront,Grid.SceneLayer.NoLayer,"meter_frame","meter_level");
                }
                initialized=true;
            }
            if(wash!=null)
            {
                float supplied=0,spent=0;
                foreach(var row in e.Contents)
                {
                    if(row.Tag==ElementLoader.FindElementByHash(wash.consumedElement)?.tag.GetHash()) supplied+=row.Stored;
                    if(row.Tag==ElementLoader.FindElementByHash(wash.outputElement)?.tag.GetHash()) spent+=row.Stored;
                }
                float max=wash.maxUses*wash.massConsumedPerUse;
                float cleanMax=prefab.GetComponent<ConduitConsumer>()?.capacityKG??max;
                if(cleanMax>0) clean?.SetPositionPercent(Mathf.Clamp01(supplied/cleanMax));
                if(max>0) dirty?.SetPositionPercent(Mathf.Clamp01(spent/max));
            }
            else if(e.StorageMeter!=byte.MaxValue) food?.SetPositionPercent(e.StorageMeter/254f);
            else if(e.Limit>0) food?.SetPositionPercent(Mathf.Clamp01(e.Value/e.Limit));
        }
    }
}
