using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Queyang.Game
{
    public static partial class HostWorld
    {
        static readonly Dictionary<string,string> BuildTools = new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["ladder"]=LadderConfig.ID, ["tile"]=TileConfig.ID, ["door"]=DoorConfig.ID,
            ["storage"]=StorageLockerConfig.ID, ["oxygen"]=MineralDeoxidizerConfig.ID, ["generator"]=ManualGeneratorConfig.ID
        };

        static IEnumerable<Orientation> Orientations(BuildingDef def)
        {
            yield return Orientation.Neutral;
            if (def.PermittedRotations==PermittedRotations.FlipH) yield return Orientation.FlipH;
            if (def.PermittedRotations==PermittedRotations.FlipV) yield return Orientation.FlipV;
            if (def.PermittedRotations==PermittedRotations.R90 || def.PermittedRotations==PermittedRotations.R360)
                yield return Orientation.R90;
            if (def.PermittedRotations==PermittedRotations.R360)
            {
                yield return Orientation.R180;
                yield return Orientation.R270;
            }
        }

        static string Options(string action,WorldContainer world)
        {
            var parts=action.Split('/');
            if(parts.Length!=3 || !BuildTools.TryGetValue(parts[1],out var prefab) ||
                !int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out var page) || page>10000)
                return "ERROR Invalid construction palette";
            var def=Assets.GetBuildingDef(prefab);
            var menu=new ManagementPage("选择朝向和主要材料");
            if(def!=null && PlanScreen.Instance!=null && PlanScreen.Instance.IsDefBuildable(def) && def.MaterialCategory.Length>0)
            {
                var materials=MaterialSelector.GetValidMaterials(def.MaterialCategory[0],true)
                    .Where(t=>world.worldInventory.GetAmount(t,true)>=def.Mass[0])
                    .Select(t=>t.ToString()).Distinct().OrderBy(t=>t,StringComparer.Ordinal);
                var choices=(from material in materials from orientation in Orientations(def)
                    select new {material,orientation}).Skip(page*5).Take(6).ToList();
                foreach (var item in choices.Take(5))
                    menu.Add(item.material+" · "+item.orientation,"build/"+parts[1]+"/"+(int)item.orientation+"/"+item.material);
                if (page>0) menu.Add("上一页","buildOptions/"+parts[1]+"/"+(page-1));
                if (choices.Count>5) menu.Add("下一页","buildOptions/"+parts[1]+"/"+(page+1));
            }
            return "B1:"+menu.Pack().Substring(ManagementPage.Prefix.Length);
        }

        static string PlaceSelected(string action,int cell,WorldContainer world)
        {
            var p=action.Split('/');
            if (p.Length!=4 || !BuildTools.TryGetValue(p[1],out var prefab) || p[3].Length==0 ||
                !int.TryParse(p[2],NumberStyles.None,CultureInfo.InvariantCulture,out int value)) return "ERROR Invalid construction options";
            var def=Assets.GetBuildingDef(prefab);
            if (def==null || !Orientations(def).Contains((Orientation)value)) return "ERROR Invalid orientation";
            return Place(prefab,cell,world,out _,(Orientation)value,p[3]);
        }

        static string PlaceNativePlan(string action,int cell,WorldContainer world)
        {
            var parts = action.Split('/');
            if (parts.Length != 4 || !int.TryParse(parts[2],NumberStyles.None,CultureInfo.InvariantCulture,out int rotation))
                return "ERROR Invalid construction plan";
            var def = Assets.GetBuildingDef(parts[1]);
            if (def == null || def.isUtility || !Orientations(def).Contains((Orientation)rotation))
                return "ERROR Invalid building or orientation";
            try { return Place(def.PrefabID,cell,world,out _,(Orientation)rotation,specified:parts[3].Split(',')); }
            catch (KeyNotFoundException) { return "ERROR Building unavailable in the current plan menu"; }
        }
    }
}
