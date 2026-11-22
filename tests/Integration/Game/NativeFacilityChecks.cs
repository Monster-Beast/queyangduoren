using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class NativeFacilityChecks
    {
        readonly Action<string> log;
        readonly TreeFilterable filter;
        readonly ComplexFabricator machine;
        readonly Door door;
        readonly float started;

        internal NativeFacilityChecks(Action<string> log)
        {
            Require(CoopSession.IsHosting && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QY_SMOKE_ROOT")) &&
                string.Equals(Util.GetKleiRootPath(),Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"),StringComparison.OrdinalIgnoreCase),
                "isolated colony required");
            this.log=log; started=Time.realtimeSinceStartup;
            var world=ClusterManager.Instance.activeWorldId;
            var visible=Components.BuildingCompletes.Items.Where(b=>b!=null && ClusterUtil.GetMyWorldId(b)==world &&
                Grid.Visible[b.GetComponent<Building>().GetCell()]!=0).ToArray();
            filter=visible.Select(b=>b.GetComponent<TreeFilterable>()).FirstOrDefault(f=>HostWorld.EditableTags(f).Count>=3);
            machine=visible.Select(b=>b.GetComponent<ComplexFabricator>()).FirstOrDefault(m=>
                m!=null && m.GetRecipes().Any(HostWorld.RecipeAvailable));
            door=visible.Select(b=>b.GetComponent<Door>()).FirstOrDefault(d=>d!=null && d.GetComponent<AccessControl>()!=null);
            if (filter==null) filter=Build(StorageLockerConfig.ID,world).GetComponent<TreeFilterable>();
            if (machine==null) machine=Build(MicrobeMusherConfig.ID,world).GetComponent<ComplexFabricator>();
            if (door==null) door=Build(DoorConfig.ID,world).GetComponent<Door>();
        }

        internal static GameObject Build(string prefab,int world)
        {
            var def=Assets.GetBuildingDef(prefab);
            var origin=Grid.PosToCell(Components.LiveMinionIdentities.Items.First(d=>d!=null && ClusterUtil.GetMyWorldId(d)==world));
            var cells=Enumerable.Range(0,Grid.CellCount).Where(c=>Grid.WorldIdx[c]==world && Grid.Visible[c]!=0 &&
                Grid.IsValidBuildingCell(c) && !Grid.Solid[c] && Grid.IsValidCell(Grid.CellBelow(c)) && Grid.Solid[Grid.CellBelow(c)])
                .OrderBy(c=>Grid.GetCellDistance(origin,c));
            foreach(int c in cells)
            {
                if (def.PlacementOffsets.Any(off=>
                {
                    int pos=Grid.OffsetCell(c,off);
                    return !Grid.IsValidCell(pos) || Grid.WorldIdx[pos]!=world || Grid.Visible[pos]==0 ||
                        Grid.Solid[pos] || Grid.Objects[pos,(int)ObjectLayer.Building]!=null;
                })) continue;
                var materials=def.MaterialCategory.Select(kind=>MaterialSelector.GetValidMaterials(kind,true).First()).ToArray();
                var built=def.Build(c,Orientation.Neutral,null,materials,295f,false,-1f);
                if (built!=null) return built;
            }
            throw new InvalidOperationException("No space to stage native facility "+prefab);
        }
        static void Require(bool ok,string message)
        { if(!ok) throw new InvalidOperationException("Native facilities: "+message); }
        static int Id(Component component) => component.GetComponent<KPrefabID>().InstanceID;
        static string Hex(string name) => new HashedString(name).HashValue.ToString("X8",CultureInfo.InvariantCulture);
        static string Cmd(Component target,string route)
        {
            Grid.CellToXY(target.GetComponent<Building>().GetCell(),out int x,out int y);
            return HostWorld.Execute("nm/"+route,x,y);
        }
        static bool Ready(Component target) => target!=null &&
            HostWorld.FindBuilding(target.GetComponent<Building>().GetCell())==target.gameObject;
        internal bool Tick()
        {
            if(Time.realtimeSinceStartup-started>8) throw new TimeoutException("Native facilities failed to register");
            if (!Ready(filter) || !Ready(machine) || !Ready(door) || !machine.GetRecipes().Any(HostWorld.RecipeAvailable)) return false;
            CheckFilters(); CheckRecipes(); CheckDoor();
            return true;
        }
        void CheckFilters()
        {
            var tags=HostWorld.EditableTags(filter).OrderBy(t=>t.Name,StringComparer.Ordinal).Take(3).ToArray();
            Require(tags.Length==3,"fewer than three editable storage tags");
            var original=new HashSet<Tag>(filter.AcceptedTags);
            string Set(bool on,params Tag[] batch) => Cmd(filter,$"filter/{Id(filter)}/{(on?1:0)}/"+string.Concat(batch.Select(t=>Hex(t.Name))));
            try
            {
                var baseline=new HashSet<Tag>(original);
                baseline.ExceptWith(tags);
                filter.UpdateFilters(baseline);
                Require(Set(true,tags[0],tags[1]).StartsWith("NM1/f/") && filter.ContainsTag(tags[0]) && filter.ContainsTag(tags[1]),
                    "atomic add did not set both native tags");
                var expected=new HashSet<Tag>(baseline) { tags[0],tags[1] };
                // The first tag is eligible. The second makes this whole request stale.
                Require(Set(true,tags[2],tags[0]).StartsWith("ERROR ") && expected.SetEquals(filter.AcceptedTags),
                    "late conflict partially applied a batch");
                Require(Set(false,tags[0],tags[1]).StartsWith("NM1/f/") && baseline.SetEquals(filter.AcceptedTags),"batch removal");
                log($"NATIVE_FILTER_HOST storage={Id(filter)} add=2 staleSecondTag=true unchangedAll=true remove=2");
            }
            finally { filter.UpdateFilters(original); }
        }
        void CheckRecipes()
        {
            var recipe=machine.GetRecipes().First(HostWorld.RecipeAvailable);
            int original=machine.GetRecipeQueueCount(recipe);
            string Edit(int old,string op,int value) => Cmd(machine,$"recipe/{Id(machine)}/{Hex(recipe.id)}/{old}/{op}/{value}");
            int Count() => machine.GetRecipeQueueCount(recipe);
            try
            {
                machine.SetRecipeQueueCount(recipe,0);
                Require(Edit(0,"set",2).StartsWith("NM1/f/") && Count()==2,"queue set");
                Require(Edit(0,"set",4).StartsWith("ERROR ") && Count()==2,"stale queue changed the winning count");
                Require(Edit(2,"inc",0).StartsWith("NM1/f/") && Count()==3,"queue increment");
                Require(Edit(3,"dec",0).StartsWith("NM1/f/") && Count()==2,"queue decrement");
                Require(Edit(2,"set",ComplexFabricator.QUEUE_INFINITE).StartsWith("NM1/f/") &&
                    Count()==ComplexFabricator.QUEUE_INFINITE,"continuous production");
                log($"NATIVE_RECIPE_HOST machine={Id(machine)} recipe={recipe.id} counts=0,2,3,2,infinite staleRejected=true");
            }
            finally { machine.SetRecipeQueueCount(recipe,original); }
        }

        string DoorAction(string text)
        {
            int cell=door.GetComponent<Building>().GetCell();
            Grid.CellToXY(cell,out int x,out int y);
            var view=ColonyView.Decode(HostWorld.Capture(x,y));
            int token=view.Cells[(y-view.Y)*ColonyView.Width+x-view.X].TargetId;
            int pages=1;
            for (int page=0;page<pages;page++)
            {
                var response=Cmd(door,$"proxy/{token}/{page}");
                Require(response.StartsWith("NM1/p/BLD1 "),"door native menu missing: "+response);
                string[] lines=response.Substring(6).Split('\n');
                pages=int.Parse(lines[0].Split(' ')[5],CultureInfo.InvariantCulture);
                foreach(string line in lines.Skip(1))
                {
                    var row=line.Split('\t');
                    if(row.Length==2 && row[1]==text) return row[0];
                }
            }
            throw new InvalidOperationException("Native door option missing: "+text);
        }
        void CheckDoor()
        {
            var access=door.GetComponent<AccessControl>();
            var people=Components.LiveMinionIdentities.Items.Where(d=>d!=null && ClusterUtil.GetMyWorldId(d)==ClusterUtil.GetMyWorldId(door))
                .Where(d=>d.assignableProxy.Get()!=null).Take(2).ToArray();
            Require(people.Length==2,"need two living duplicants to check door permission isolation");
            var first=people[0].assignableProxy.Get(); var second=people[1].assignableProxy.Get();
            var initial=door.RequestedState;
            var old=access.GetSetPermission(first); var other=access.GetSetPermission(second);
            try
            {
                bool open=initial!=Door.ControlState.Opened;
                var next=open?Door.ControlState.Opened:Door.ControlState.Auto;
                string route=DoorAction(open?"保持开启":"自动门禁");
                Require(Cmd(door,route).StartsWith("NM1/p/") && door.RequestedState==next,"door mode was not queued");
                Require(Cmd(door,route).StartsWith("ERROR ") && door.RequestedState==next,"stale mode accepted");
                var permission=old==AccessControl.Permission.GoLeft ? AccessControl.Permission.Neither : AccessControl.Permission.GoLeft;
                string label=people[0].GetProperName()+" · "+(permission==AccessControl.Permission.GoLeft?"向左":"禁止");
                route=DoorAction(label);
                Require(Cmd(door,route).StartsWith("NM1/p/") && access.GetSetPermission(first)==permission &&
                    access.GetSetPermission(second)==other,"door per-duplicant permission changed the wrong user");
                Require(Cmd(door,route).StartsWith("ERROR ") && access.GetSetPermission(first)==permission &&
                    access.GetSetPermission(second)==other,"stale permission accepted");
                log($"NATIVE_DOOR_HOST door={Id(door)} requested={next} dupe={Id(people[0])} permission={permission} peerDupe={Id(people[1])} otherUnchanged=true staleRejected=true");
            }
            finally { access.SetPermission(first,old); door.QueueStateChange(initial); }
        }
    }
}
