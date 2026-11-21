using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static GameObject selectedSettings;
        static TreeFilterable filter;
        static ComplexFabricator fabricator;
        static readonly Dictionary<int, bool> allowed = new Dictionary<int, bool>();
        static readonly Dictionary<int, bool> edits = new Dictionary<int, bool>();
        static readonly Dictionary<int, int> counts = new Dictionary<int, int>();
        static readonly FieldInfo RecipeCounts = AccessTools.DeclaredField(typeof(ComplexFabricator), "recipeQueueCounts");
        static int Hash(string id) => new HashedString(id).HashValue;
        static string Hex(int id) => id.ToString("X8", CultureInfo.InvariantCulture);
        static bool TryHex(string text, out int n) => int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n);
        static void SelectSettings(GameObject target)
        {
            if (ReferenceEquals(target, selectedSettings)) return;
            selectedSettings = target;
            filter = target == null ? null : target.GetComponent<TreeFilterable>();
            fabricator = target == null ? null : target.GetComponent<ComplexFabricator>();
            allowed.Clear(); edits.Clear(); counts.Clear();
        }
        static void PollSettings()
        {
            if (filter == null && fabricator == null) return;
            Component component = filter != null ? (Component)filter : fabricator;
            if (edits.Count != 0)
            {
                bool on = edits.First().Value;
                var batch = edits.Where(e => e.Value == on).Take(24).Select(e => e.Key).ToArray();
                foreach (int key in batch) edits.Remove(key);
                BuildingCommand(component, "filter/" + Id(component) + "/" + (on ? "1/" : "0/") + string.Concat(batch.Select(Hex)));
            }
            else BuildingCommand(component, "settings/" + Id(component));
        }
        internal static string BuildingSettings(GameObject target, string[] p)
        {
            var f = target.GetComponent<TreeFilterable>();
            var machine = target.GetComponent<ComplexFabricator>();
            if (f == null && machine == null) return Conflict;
            var tags = HostWorld.EditableTags(f);
            var recipes = machine == null ? Array.Empty<ComplexRecipe>() : machine.GetRecipes().Where(HostWorld.RecipeAvailable).ToArray();
            if (p[1] == "filter")
            {
                if (f == null || p.Length != 5 || (p[3] != "1" && p[3] != "0") || p[4].Length == 0 ||
                    p[4].Length > 192 || p[4].Length % 8 != 0) return Conflict;
                bool on = p[3] == "1";
                var next = new HashSet<Tag>(f.AcceptedTags);
                for (int i = 0; i < p[4].Length; i += 8)
                {
                    if (!TryHex(p[4].Substring(i, 8), out int code)) return Conflict;
                    var matches = tags.Where(t => Hash(t.ToString()) == code).Take(2).ToArray();
                    if (matches.Length != 1) return Conflict;
                    // Filter edits are generated against the last confirmed opposite state.
                    // Validate all affected tags in the staged copy before touching ONI.
                    if (next.Contains(matches[0]) == on) return Conflict;
                    if (on) next.Add(matches[0]); else next.Remove(matches[0]);
                }
                if (!f.AcceptedTags.SetEquals(next)) f.UpdateFilters(next);
            }
            else if (p[1] == "recipe")
            {
                if (machine == null || p.Length != 7 || !TryHex(p[3], out int code)) return Conflict;
                var found = recipes.Where(r => Hash(r.id) == code).Take(2).ToArray();
                if (found.Length != 1) return Conflict;
                var r = found[0];
                int old = machine.GetRecipeQueueCount(r);
                if (old.ToString(CultureInfo.InvariantCulture) != p[4]) return Conflict;
                if (p[5] == "inc" && p[6] == "0") machine.IncrementRecipeQueueCount(r);
                else if (p[5] == "dec" && (p[6] == "0" || p[6] == "1")) machine.DecrementRecipeQueueCount(r, p[6] == "1");
                else if (p[5] == "set" && int.TryParse(p[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) &&
                    (n == ComplexFabricator.QUEUE_INFINITE || (n >= 0 && n <= ComplexFabricator.MAX_QUEUE_SIZE))) machine.SetRecipeQueueCount(r, n);
                else return Conflict;
            }
            else if (p.Length != 3) return Conflict;

            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                if (tags.Count * 5L + recipes.Length * 8L > 5950) return "ERROR Building settings exceed message limit";
                w.Write((ushort)tags.Count);
                foreach (var tag in tags) { w.Write(Hash(tag.ToString())); w.Write(f.ContainsTag(tag)); }
                w.Write((ushort)recipes.Length);
                foreach (var r in recipes) { w.Write(Hash(r.id)); w.Write(machine.GetRecipeQueueCount(r)); }
                return Prefix + "f/" + p[2] + "/" + Convert.ToBase64String(s.ToArray()).Replace('/', '_');
            }
        }
        static void ApplySettings(string[] p)
        {
            if (p.Length != 4 || (filter == null && fabricator == null) || selectedSettings == null ||
                selectedSettings.GetComponent<KPrefabID>()?.InstanceID != Number(p[2])) return;
            using (var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(p[3].Replace('_', '/')))))
            {
                allowed.Clear(); counts.Clear();
                int tags = r.ReadUInt16();
                for (int i = 0; i < tags; i++) allowed.Add(r.ReadInt32(), r.ReadBoolean());
                int recipes = r.ReadUInt16();
                for (int i = 0; i < recipes; i++) counts.Add(r.ReadInt32(), r.ReadInt32());
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Invalid building settings");
            }
            bool dirty = false;
            if (filter != null)
            {
                foreach (var tag in HostWorld.EditableTags(filter))
                {
                    if (!allowed.TryGetValue(Hash(tag.ToString()), out bool on)) continue;
                    dirty |= on ? filter.AcceptedTags.Add(tag) : filter.AcceptedTags.Remove(tag);
                }
                if (dirty) filter.OnFilterChanged?.Invoke(filter.AcceptedTags);
            }
            if (fabricator != null)
            {
                var queues = (Dictionary<string, int>)RecipeCounts.GetValue(fabricator);
                foreach (var recipe in fabricator.GetRecipes())
                {
                    if (!counts.TryGetValue(Hash(recipe.id), out int n) || (queues.TryGetValue(recipe.id, out int old) && n == old)) continue;
                    queues[recipe.id] = n;
                    dirty = true;
                }
                if (dirty) fabricator.Trigger((int)GameHashes.FabricatorOrdersUpdated, fabricator);
            }
        }

        [HarmonyPatch]
        static class FilterClick
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.DeclaredMethod(typeof(TreeFilterable), "AddTagToFilter");
                yield return AccessTools.DeclaredMethod(typeof(TreeFilterable), "RemoveTagFromFilter");
            }
            static bool Prefix(TreeFilterable __instance, Tag __0, MethodBase __originalMethod)
            {
                if (!Guard) return true;
                int key = Hash(__0.ToString());
                bool on = __originalMethod.Name == "AddTagToFilter";
                if (__instance == filter && allowed.TryGetValue(key, out bool prior))
                {
                    if (prior == on) edits.Remove(key);
                    else edits[key] = on;
                }
                return false;
            }
        }
        [HarmonyPatch(typeof(TreeFilterable), "UpdateFilters")]
        static class FilterSet
        {
            static bool Prefix() => !Guard;
        }
        static bool RecipeEdit(ComplexFabricator source, ComplexRecipe recipe, string operation)
        {
            if (!Guard) return true;
            if (source == fabricator && recipe != null && counts.TryGetValue(Hash(recipe.id), out int n))
                BuildingCommand(source, "recipe/" + Id(source) + "/" + Hex(Hash(recipe.id)) + "/" +
                    n.ToString(CultureInfo.InvariantCulture) + "/" + operation);
            return false;
        }
        [HarmonyPatch(typeof(ComplexFabricator), "IncrementRecipeQueueCount")]
        static class Increase { static bool Prefix(ComplexFabricator __instance, ComplexRecipe __0) => RecipeEdit(__instance, __0, "inc/0"); }
        [HarmonyPatch(typeof(ComplexFabricator), "DecrementRecipeQueueCount")]
        static class Decrease { static bool Prefix(ComplexFabricator __instance, ComplexRecipe __0, bool __1) => RecipeEdit(__instance, __0, __1 ? "dec/1" : "dec/0"); }
        [HarmonyPatch(typeof(ComplexFabricator), "SetRecipeQueueCount")]
        static class SetCount { static bool Prefix(ComplexFabricator __instance, ComplexRecipe __0, int __1) => RecipeEdit(__instance, __0, "set/" + __1.ToString(CultureInfo.InvariantCulture)); }
    }
}
