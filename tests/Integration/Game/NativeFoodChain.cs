using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class NativeFoodChain
    {
        readonly string dir;
        readonly Action<string> log;
        readonly GameObject food, baby;
        readonly Storage store;
        readonly int foodId, babyId, storeId;
        readonly float foodMass, foodKcal, started;
        readonly HashSet<int> existing = new HashSet<int>();
        int stage, rotId, meatId, foodCell, deathCell;
        float meatMass, meatKcal;
        string waiting;
        Equippable vest;
        MinionIdentity wearer;
        int vestId, wearerId;
        int vestBuild, vestSymbol;

        static int Id(GameObject obj) => obj.GetComponent<KPrefabID>().InstanceID;
        static void Require(bool ok, string reason)
        {
            if (!ok) throw new InvalidDataException("Native food chain: "+reason);
        }
        static bool Near(float a, float b) => Math.Abs(a-b) < .005f;

        static KAnim.Build.Symbol Rendered(GameObject wearer,int hash)
        {
            return wearer.GetComponent<SymbolOverrideController>().GetSymbolOverrides
                .Where(entry=>entry.targetSymbol.HashValue==hash)
                .OrderByDescending(entry=>entry.priority).Select(entry=>entry.sourceSymbol).FirstOrDefault();
        }
        static bool VisibleSymbol(GameObject owner,int target,int build)
        {
            if (!Assets.TryGetAnim(new HashedString(build),out var file)) return false;
            return Rendered(owner,target)?.build==file.GetData().build &&
                owner.GetComponent<KBatchedAnimController>().GetSymbolVisiblity(new KAnimHashedString(target));
        }

        internal NativeFoodChain(string dir, Action<string> log)
        {
            Require(CoopSession.IsHosting && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QY_SMOKE_ROOT")) &&
                string.Equals(Util.GetKleiRootPath(),Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"),StringComparison.OrdinalIgnoreCase),
                "isolated host required");
            this.dir=dir; this.log=log; started=Time.realtimeSinceStartup;
            using(var r=new BinaryReader(File.OpenRead(Path.Combine(dir,"ecology-ids.bin"))))
            { r.ReadInt32(); r.ReadInt32(); foodId=r.ReadInt32(); babyId=r.ReadInt32(); }
            food=KPrefabIDTracker.Get().GetInstance(foodId)?.gameObject;
            baby=KPrefabIDTracker.Get().GetInstance(babyId)?.gameObject;
            Require(food!=null && baby!=null,"missing harvested food or hatchling");
            foodMass=food.GetComponent<PrimaryElement>().Mass;
            foodKcal=food.GetComponent<Edible>().Calories/1000f;
            var tag=food.GetComponent<KPrefabID>().PrefabTag;
            int origin=Grid.PosToCell(food);
            store=Components.BuildingCompletes.Items
                .Where(b=>b!=null && ClusterUtil.GetMyWorldId(b)==ClusterManager.Instance.activeWorldId &&
                    Grid.Visible[Grid.PosToCell(b)]!=0 &&
                    (b.PrefabID()==StorageLockerConfig.ID || b.PrefabID()==RationBoxConfig.ID || b.PrefabID()==RefrigeratorConfig.ID))
                .Select(b=>b.GetComponent<Storage>())
                .Where(s=>s!=null && s.capacityKg-s.ExactMassStored()>=foodMass &&
                    !s.items.Any(g=>g!=null && g.PrefabID()==tag))
                .OrderBy(s=>Grid.GetCellDistance(Grid.PosToCell(s),origin)).FirstOrDefault();
            Require(store!=null,"no visible suitable locker or food container with spare capacity and no matching stack");
            storeId=Id(store.gameObject);
            Require(food.GetComponent<Pickupable>().storage==null && foodMass>0 && foodKcal>0,"harvested sample unavailable");
        }

        // Capture the ID's actual rotating page, including an empty interval after destruction.
        bool Record(string name, int cell, int id, Func<ColonyEntity,bool> match)
        {
            Grid.CellToXY(cell,out int x,out int y);
            for (int sample=0; ; sample++)
            {
                var data=HostWorld.CapturePage(x-ColonyView.Width/2,y-ColonyView.Height/2,sample);
                var page=EntityStateCodec.Decode(ColonyView.Decode(data).EntityPayload);
                if (id<page.From || id>page.Through)
                {
                    if (page.Through==int.MaxValue) throw new InvalidDataException("Entity page missing "+id);
                    continue;
                }
                var row=page.Entities.FirstOrDefault(e=>e.Id==id);
                waiting=name+" id="+id+" cell="+cell+" observed="+row?.Kind+" place="+row?.Place;
                if (!match(row)) return false;
                File.WriteAllBytes(Path.Combine(dir,"foodchain-"+name+".view"),data);
                return true;
            }
        }

        void SnapshotDrops(string prefab)
        {
            existing.Clear();
            foreach(var p in Components.Pickupables.Items)
                if(p!=null && p.PrefabID()==prefab) existing.Add(Id(p.gameObject));
        }
        Pickupable NewDrop(string prefab,int cell) => Components.Pickupables.Items.FirstOrDefault(p=>
            p!=null && p.PrefabID()==prefab && !existing.Contains(Id(p.gameObject)) &&
            Grid.IsValidCell(Grid.PosToCell(p)) && Grid.WorldIdx[Grid.PosToCell(p)]==Grid.WorldIdx[cell] &&
            Grid.GetCellDistance(Grid.PosToCell(p),cell)<=3);

        internal bool Tick()
        {
            if (stage==8) return true;
            if (Time.realtimeSinceStartup-started>52) throw new TimeoutException("Food chain stage="+stage+" "+waiting);
            int containerCell=store.GetComponent<Building>().GetCell();
            bool IsFood(ColonyEntity e) => e!=null && e.Kind==ColonyEntityKind.Food && Near(e.Mass,foodMass) && Near(e.Value,foodKcal);
            switch(stage)
            {
                case 0:
                    if (!Record("ground",Grid.PosToCell(food),foodId,e=>IsFood(e) && e.Place==ColonyItemPlace.Ground)) return false;
                    float before=store.ExactMassStored();
                    store.Store(food);
                    Require(store.items.Contains(food) && Near(store.ExactMassStored()-before,foodMass),"Store did not add original sample mass");
                    // Capture while the sample is still in the locker, before a duplicant or a
                    // filter can empty it on the next simulation update.
                    stage=1;
                    goto case 1;
                case 1:
                    Require(Record("stored",containerCell,foodId,e=>IsFood(e) && e.Place==ColonyItemPlace.Stored && e.OwnerId==storeId),
                        waiting+" container="+storeId+" contains="+store.items.Contains(food)+" count="+store.items.Count);
                    Require(Record("contents",containerCell,storeId,e=>e!=null && e.Kind==ColonyEntityKind.Storage &&
                        Near(e.Value,store.ExactMassStored())),waiting+" container="+storeId);
                    float full=store.ExactMassStored();
                    store.Drop(food);
                    Require(!store.items.Contains(food) && Near(full-store.ExactMassStored(),foodMass),"Drop did not release original sample mass");
                    break;
                case 2:
                    foodCell=Grid.PosToCell(food);
                    if (!Record("released",foodCell,foodId,e=>IsFood(e) && e.Place==ColonyItemPlace.Ground)) return false;
                    SnapshotDrops(RotPileConfig.ID);
                    food.GetSMI<Rottable.Instance>().RotValue=-1f;
                    waiting="ONI rot conversion at "+foodCell;
                    break;
                case 3:
                    var rot=NewDrop(RotPileConfig.ID,foodCell);
                    if (food!=null || rot==null) return false;
                    rotId=Id(rot.gameObject);
                    Require(rot.GetComponent<Edible>()==null && Near(rot.PrimaryElement.Mass,foodMass),"rot still edible or mass changed");
                    if (!Record("spoiled",foodCell,foodId,e=>e==null) ||
                        !Record("rot",Grid.PosToCell(rot),rotId,e=>e!=null && Near(e.Mass,foodMass) && e.Kind==ColonyEntityKind.Item)) return false;
                    deathCell=Grid.PosToCell(baby);
                    if (!Record("alive",deathCell,babyId,e=>e!=null && e.Kind==ColonyEntityKind.Critter && e.Extra>0)) return false;
                    SnapshotDrops(MeatConfig.ID);
                    baby.GetSMI<DeathMonitor.Instance>().Kill(Db.Get().Deaths.Slain);
                    waiting="ONI hatchling death and meat drop at "+deathCell;
                    break;
                case 4:
                    var meat=NewDrop(MeatConfig.ID,deathCell);
                    if (baby!=null || meat==null) return false;
                    meatId=Id(meat.gameObject);
                    meatMass=meat.PrimaryElement.Mass;
                    meatKcal=meat.GetComponent<Edible>().Calories/1000f;
                    Require(meatMass>0 && meatKcal>0,"inedible or empty meat drop");
                    if (!Record("dead",deathCell,babyId,e=>e==null) ||
                        !Record("meat",Grid.PosToCell(meat),meatId,e=>e!=null && e.Kind==ColonyEntityKind.Food &&
                            Near(e.Mass,meatMass) && Near(e.Value,meatKcal))) return false;
                    using(var w=new BinaryWriter(File.Create(Path.Combine(dir,"foodchain.bin"))))
                    {
                        w.Write(foodId); w.Write(storeId); w.Write(rotId); w.Write(babyId); w.Write(meatId);
                        w.Write(foodMass); w.Write(foodKcal); w.Write(meatMass); w.Write(meatKcal);
                    }
                    log($"NATIVE_FOOD_CHAIN_HOST storeDrop={foodId} container={storeId} foodKg={foodMass} lostKcal={foodKcal} rot={rotId} hatchling={babyId} meat={meatId} meatKg={meatMass} meatKcal={meatKcal}");
                    var prefab=Assets.TryGetPrefab(WarmVestConfig.ID);
                    var slot=prefab.GetComponent<Equippable>().slot;
                    wearer=Components.LiveMinionIdentities.Items.FirstOrDefault(d=>d!=null &&
                        ClusterUtil.GetMyWorldId(d)==ClusterManager.Instance.activeWorldId &&
                        d.GetEquipment()!=null && !d.GetEquipment().IsSlotOccupied(slot));
                    Require(wearer!=null,"no duplicant with an empty clothing slot");
                    var vestObject=Util.KInstantiate(prefab,Grid.CellToPosCBC(Grid.PosToCell(wearer),Grid.SceneLayer.Ore));
                    vestObject.SetActive(true);
                    vest=vestObject.GetComponent<Equippable>();
                    vestId=Id(vestObject); wearerId=Id(wearer.gameObject);
                    break;
                case 5:
                    if (!Record("vest",Grid.PosToCell(vest),vestId,e=>e!=null && e.Place==ColonyItemPlace.Ground)) return false;
                    vest.Assign(wearer);
                    if (!vest.isEquipped) wearer.GetEquipment().Equip(vest);
                    Require(vest.isEquipped && wearer.GetEquipment().IsEquipped(vest),"native equip failed");
                    break;
                case 6:
                    var asset=vest.GetBuildOverride();
                    Require(asset!=null,"vest has no wearable build");
                    vestBuild=new HashedString(asset.name).HashValue;
                    var visible=asset.GetData().build.symbols.FirstOrDefault(s=>s!=null &&
                        VisibleSymbol(wearer.gameObject,s.hash.HashValue,vestBuild));
                    if (visible==null) { waiting="native visible clothing symbol "+asset.name; return false; }
                    vestSymbol=visible.hash.HashValue;
                    if (!Record("worn",Grid.PosToCell(wearer),vestId,e=>e!=null && e.Place==ColonyItemPlace.Carried &&
                        e.OwnerId==wearerId && e.IsWorn && e.WearBuild==vestBuild && e.WearSymbols.Any(b=>(b&0xaa)!=0))) return false;
                    wearer.GetEquipment().Unequip(vest);
                    Require(!vest.isEquipped && !wearer.GetEquipment().IsEquipped(vest),"native unequip failed");
                    break;
                case 7:
                    if (!Record("removed",Grid.PosToCell(vest),vestId,e=>e!=null && e.Place==ColonyItemPlace.Ground &&
                        (e.Flags&ColonyEntity.Worn)==0)) return false;
                    using(var w=new BinaryWriter(File.Create(Path.Combine(dir,"gear.bin"))))
                    {
                        Require(!VisibleSymbol(wearer.gameObject,vestSymbol,vestBuild),"unequipped vest remains visible on host");
                        w.Write(vestId); w.Write(wearerId); w.Write(vestBuild); w.Write(vestSymbol);
                    }
                    log($"NATIVE_EQUIPMENT_HOST wearer={wearerId} vest={vestId} equippedAndRemoved=true visibleBuild={vestBuild} symbol={vestSymbol}");
                    stage++;
                    return true;
            }
            stage++;
            return false;
        }

        internal static void Replay(string dir,Action<string> log)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (!File.Exists(Path.Combine(dir,"foodchain.bin")))
            {
                log("NATIVE_FOOD_CHAIN_SKIPPED recording predates food chain fixture");
                return;
            }
            using(var r=new BinaryReader(File.OpenRead(Path.Combine(dir,"foodchain.bin"))))
            {
                int food=r.ReadInt32(), box=r.ReadInt32(), rot=r.ReadInt32(), baby=r.ReadInt32(), meat=r.ReadInt32();
                float kg=r.ReadSingle(), kcal=r.ReadSingle(), meatKg=r.ReadSingle(), meatKcal=r.ReadSingle();
                void Apply(string name) => NativeWorldMirror.Apply(ColonyView.Decode(File.ReadAllBytes(Path.Combine(dir,"foodchain-"+name+".view"))));
                ColonyEntity Row(int id) => NativeEntityDisplay.GetObservedState(id);
                void Select(int id)
                {
                    var target=NativeEntityDisplay.GetSelectable(id);
                    Require(target!=null && target.IsSelectable,"missing selectable "+id);
                    PlayerController.Instance.ActivateTool(SelectTool.Instance);
                    SelectTool.Instance.Select(target,false);
                    Require(target.IsSelected && DetailsScreen.Instance.target==target.gameObject,"open selection "+id);
                }
                void Closed(int id) => Require(NativeEntityDisplay.GetSelectable(id)==null &&
                    SelectTool.Instance.selected==null,"old selection still open "+id);
                Apply("ground"); Select(food);
                Apply("stored"); Closed(food);
                Require(Row(food)?.Place==ColonyItemPlace.Stored && Row(food).OwnerId==box && Near(Row(food).Value,kcal),"stored meal or kcal");
                Apply("contents");
                Require(Row(box)?.Kind==ColonyEntityKind.Storage && Row(box).Value>=kg,"container mass");
                Apply("released"); Select(food);
                Require(Row(food).Place==ColonyItemPlace.Ground && Near(Row(food).Mass,kg),"released meal");
                Apply("spoiled"); Closed(food);
                Require(Row(food)==null,"destroyed meal still tracked");
                Apply("rot"); Select(rot);
                Require(Row(rot).Prefab==new Tag(RotPileConfig.ID).GetHash() && Near(Row(rot).Mass,kg),"rot identity or mass");
                Apply("alive"); Select(baby);
                Apply("dead"); Closed(baby);
                Apply("meat"); Select(meat);
                Require(Row(baby)==null && Row(meat).Prefab==new Tag(MeatConfig.ID).GetHash() &&
                    Near(Row(meat).Mass,meatKg) && Near(Row(meat).Value,meatKcal),"post-death meat or calories");
                SelectTool.Instance.Select(null,true);
                log("NATIVE_FOOD_CHAIN_GUEST storedReleased=true selectedMealClosed=true rotMass=true selectedCritterClosed=true meatKcal="+meatKcal);
                if (!File.Exists(Path.Combine(dir,"gear.bin")))
                {
                    log("NATIVE_EQUIPMENT_SKIPPED recording predates equipment fixture");
                    return;
                }
                using(var gear=new BinaryReader(File.OpenRead(Path.Combine(dir,"gear.bin"))))
                {
                    int vest=gear.ReadInt32(), wearer=gear.ReadInt32();
                    bool hasSymbols=gear.BaseStream.Position<gear.BaseStream.Length;
                    int build=hasSymbols?gear.ReadInt32():0, symbol=hasSymbols?gear.ReadInt32():0;
                    Apply("vest"); Select(vest);
                    Apply("worn"); Closed(vest);
                    Require(Row(vest)?.OwnerId==wearer && (Row(vest).Flags&ColonyEntity.Worn)!=0,"missing worn equipment");
                    var carrier=NativeWorldMirror.GetDupeSelectable(wearer);
                    if (hasSymbols) Require(carrier!=null && VisibleSymbol(carrier.gameObject,symbol,build),"guest vest symbol did not appear");
                    Apply("removed"); Select(vest);
                    Require(Row(vest).Place==ColonyItemPlace.Ground && (Row(vest).Flags&ColonyEntity.Worn)==0,"vest still assigned");
                    if (hasSymbols) Require(!VisibleSymbol(carrier.gameObject,symbol,build),"guest vest symbol still visible after removal");
                    SelectTool.Instance.Select(null,true);
                    log("NATIVE_EQUIPMENT_GUEST wornOwner=true oldGroundSelectionClosed=true unequippedSelectable=true symbolReadback="+hasSymbols);
                }
            }
        }
    }
}
