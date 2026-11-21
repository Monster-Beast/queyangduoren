using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // Drive normal ONI harvest and incubation transitions after the baseline has been recorded.
    // All files and mutation targets belong to the private smoke profile.
    internal sealed class NativeEcology
    {
        readonly string dir;
        readonly Action<string> log;
        readonly GameObject plant, egg;
        readonly int plantId, eggId, x, y, previousLastId;
        readonly float started;
        readonly Tag foodTag, babyTag;
        int foodId, babyId, stage;
        float lastCapture;
        string state = "spawn";

        static int Id(GameObject o) => o.GetComponent<KPrefabID>().InstanceID;
        static void Require(bool ok, string text) { if (!ok) throw new InvalidDataException("Ecology: " + text); }
        internal NativeEcology(string dir, Action<string> log)
        {
            Require(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QY_SMOKE_ROOT")) &&
                string.Equals(Util.GetKleiRootPath(),Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"),StringComparison.OrdinalIgnoreCase) &&
                CoopSession.IsHosting, "isolated host required");
            this.dir = dir; this.log = log; started = Time.realtimeSinceStartup;
            var dupe = Components.LiveMinionIdentities.Items.First(d => d != null && ClusterUtil.GetMyWorldId(d) == ClusterManager.Instance.activeWorldId);
            int origin = Grid.PosToCell(dupe);
            var center = Grid.CellToXY(origin);
            var view = ColonyView.Decode(HostWorld.Capture(center.x, center.y));
            var cells = Enumerable.Range(0,view.Cells.Length).Where(i=>(view.Cells[i].Flags & 63)==1)
                .Select(i=>Grid.XYToCell(view.X + i % ColonyView.Width,view.Y+i / ColonyView.Width))
                .Where(c=>Grid.IsValidCell(c) && Grid.IsValidCell(Grid.CellAbove(c)) && Grid.IsValidCell(Grid.CellBelow(c)) &&
                    !Grid.Solid[Grid.CellAbove(c)] && Grid.Solid[Grid.CellBelow(c)] && Grid.Temperature[c] > 284 && Grid.Temperature[c] < 302 &&
                    Grid.Element[c].id == SimHashes.Oxygen && Grid.Mass[c] > .2f && Grid.Objects[c,(int)ObjectLayer.Plants]==null)
                .OrderBy(c=>Grid.GetCellDistance(c,origin)).ToList();
            Require(cells.Count != 0, "no visible temperate oxygenated planting cell in fixture");
            int cell = cells[0];
            var coord = Grid.CellToXY(cell); x = coord.x; y = coord.y;
            GameObject Spawn(string prefab)
            {
                var source = Assets.TryGetPrefab(prefab);
                Require(source != null, "missing ONI prefab " + prefab);
                var obj = Util.KInstantiate(source, Grid.CellToPosCBC(cell,Grid.SceneLayer.Move));
                obj.SetActive(true); return obj;
            }
            plant = Spawn(BasicSingleHarvestPlantConfig.ID); egg = Spawn(HatchConfig.EGG_ID);
            plantId = Id(plant); eggId = Id(egg); previousLastId = Math.Max(plantId,eggId);
            foodTag = plant.GetComponent<Crop>().primaryCropId;
            babyTag = egg.GetSMI<IncubationMonitor.Instance>().def.spawnedCreature;
            plant.GetComponent<Growing>().OverrideMaturityLevel(1f);
            var incubation = egg.GetSMI<IncubationMonitor.Instance>().incubation;
            incubation.SetValue(incubation.GetMax()*.4f);
            log("NATIVE_ECOLOGY_FIXTURE plant=" + plantId + " egg=" + eggId + " crop=" + foodTag + " baby=" + babyTag);
        }

        internal bool Tick()
        {
            if (Time.realtimeSinceStartup-started > 30) throw new TimeoutException("Ecology " + state);
            if (Time.realtimeSinceStartup < lastCapture + .25f) return false;
            lastCapture = Time.realtimeSinceStartup;
            var bytes = HostWorld.Capture(x,y);
            var page = EntityStateCodec.Decode(ColonyView.Decode(bytes).EntityPayload);
            ColonyEntity Find(int id) => page.Entities.FirstOrDefault(e=>e.Id==id);
            var crop = Find(plantId);
            void Save(string name) => File.WriteAllBytes(Path.Combine(dir,"ecology-"+name+".view"),bytes);
            if (stage == 0)
            {
                var shell = Find(eggId);
                state = "waiting for ripe mealwood and viable egg, crop=" + (crop == null ? "absent" : crop.Value+" flags="+crop.Flags) +
                    " egg=" + (shell == null ? "absent" : shell.Value.ToString());
                if (shell == null || crop == null || (crop.Flags & 8) == 0 || crop.Value < .99f || shell.Value <= 0 || shell.Value >= 1) return false;
                Save("ripe");
                plant.GetComponent<Harvestable>().Harvest();
                var incubation = egg.GetSMI<IncubationMonitor.Instance>().incubation;
                incubation.SetValue(incubation.GetMax());
                stage++;
            }
            else if (stage == 1)
            {
                var food = page.Entities.FirstOrDefault(e=>e.Id>previousLastId && e.Kind==ColonyEntityKind.Food && e.Prefab==foodTag.GetHash());
                var baby = page.Entities.FirstOrDefault(e=>e.Id>previousLastId && e.Kind==ColonyEntityKind.Critter && e.Prefab==babyTag.GetHash());
                state = "waiting for harvested fruit and hatchling, food=" + (food!=null) + " baby=" + (baby!=null) +
                    " eggGone=" + (egg==null) + " regrowth=" + crop?.Value;
                if (crop==null || crop.Value>=.95f || food==null || baby==null || egg!=null || Find(eggId)!=null ||
                    eggId < page.From || eggId > page.Through) return false;
                Require(food.Value>0 && food.Limit>.5f && baby.Extra>0, "harvested kcal, freshness or hatchling health");
                Save("harvest"); foodId=food.Id; babyId=baby.Id;
                var rot=KPrefabIDTracker.Get().GetInstance(food.Id).gameObject.GetSMI<Rottable.Instance>();
                Require(rot!=null, "harvested food has no rot meter");
                rot.RotValue *= .25f;
                stage++;
            }
            else
            {
                var food=Find(foodId);
                state="waiting for harvested food freshness " + food?.Limit;
                if (food==null || food.Limit < 0 || food.Limit > .45f) return false;
                Save("aged");
                using(var w=new BinaryWriter(File.Create(Path.Combine(dir,"ecology-ids.bin"))))
                { w.Write(plantId); w.Write(eggId); w.Write(foodId); w.Write(babyId); }
                log("NATIVE_ECOLOGY_HOST harvest=true eggHatched=true foodKcal="+food.Value+" freshness="+food.Limit);
                return true;
            }
            return false;
        }

        internal static void Replay(string dir, Action<string> log)
        {
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir,"ecology-ids.bin"))) return;
            using (var r=new BinaryReader(File.OpenRead(Path.Combine(dir,"ecology-ids.bin"))))
            {
                int plant=r.ReadInt32(), egg=r.ReadInt32(), food=r.ReadInt32(), baby=r.ReadInt32();
                void Apply(string name) => NativeWorldMirror.Apply(ColonyView.Decode(File.ReadAllBytes(Path.Combine(dir,"ecology-"+name+".view"))));
                ColonyEntity Visible(int id,ColonyEntityKind kind)
                {
                    var select=NativeEntityDisplay.GetSelectable(id);
                    var row=NativeEntityDisplay.GetObservedState(id);
                    Require(select != null && select.IsSelectable && row != null && row.Kind==kind, "missing selectable "+kind);
                    return row;
                }
                Apply("ripe");
                Require((Visible(plant,ColonyEntityKind.Plant).Flags&8)!=0 && Visible(egg,ColonyEntityKind.Egg).Value>0, "ripe and incubating display");
                var shell=NativeEntityDisplay.GetSelectable(egg);
                PlayerController.Instance.ActivateTool(SelectTool.Instance);
                SelectTool.Instance.Select(shell,false);
                Require(shell.IsSelected && DetailsScreen.Instance.target==shell.gameObject, "egg details did not open");
                Apply("harvest");
                Require(SelectTool.Instance.selected!=shell && DetailsScreen.Instance.target!=shell.gameObject &&
                    NativeEntityDisplay.GetSelectable(egg)==null, "disappeared egg is still selected");
                Require(Visible(plant,ColonyEntityKind.Plant).Value < .95f && Visible(baby,ColonyEntityKind.Critter).Extra>0, "regrowth and hatchling");
                var fruit=Visible(food,ColonyEntityKind.Food); float fresh=fruit.Limit;
                Apply("aged");
                Require(Visible(food,ColonyEntityKind.Food).Limit < fresh-.25f, "freshness did not decrease");
                log("NATIVE_ECOLOGY_GUEST harvest=true hatchling=true selectedEggClosed=true freshnessDelta=true");
            }
        }
    }
}
