using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    public static partial class HostWorld
    {
        private const int PageSize = 6;

        private sealed class Setting
        {
            internal readonly string Key, Label;
            internal readonly System.Action Apply;

            internal Setting(string key, string label, System.Action apply)
            {
                Key = key;
                Label = label;
                Apply = apply;
            }

            // Includes the field value observed when the menu was opened. Recompute before applying.
            internal string Token()
            {
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Key)), 0, 8).Replace("-", "");
            }
        }

        private static string BuildingOptions(string action, int cell, int x, int y)
        {
            var parts = action.Split('/');
            int id, index;
            bool proxy = parts[0] == "proxy" || parts[0] == "setproxy";
            bool edit = parts[0] == "b" || parts[0] == "setproxy";
            if (parts.Length != (edit ? 4 : 3) || !Natural(parts[1], out id) || !Natural(parts[2], out index))
                return "ERROR Invalid building request";
            var target = GetTarget(cell);
            TargetKey key;
            if (target == null || !TargetKeys.TryGetValue(target, out key) || key.Value != id ||
                target.GetComponent<BuildingComplete>() == null) return "ERROR Building changed or unfinished";
            var options = Settings(target, proxy);
            if (edit)
            {
                if (index >= options.Count || !string.Equals(options[index].Token(), parts[3], StringComparison.Ordinal))
                    return "ERROR Setting changed; refresh this building";
                options[index].Apply();
                options = Settings(target, proxy);
            }
            int pages = Math.Max(1, (options.Count + PageSize - 1) / PageSize);
            int page = Math.Min(pages - 1, edit ? index / PageSize : index);
            var text = new StringBuilder().AppendFormat(CultureInfo.InvariantCulture,
                "BLD1 {0} {1} {2} {3} {4}", x, y, id, page, pages);
            for (int i = page * PageSize; i < Math.Min(options.Count, (page + 1) * PageSize); i++)
            {
                string label = options[i].Label.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
                // Unicode text elements avoid splitting a surrogate pair. Bound bytes to respect the wire limit.
                var graphemes = StringInfo.GetTextElementEnumerator(label);
                var shortLabel = new StringBuilder();
                int bytes = 0;
                while (graphemes.MoveNext())
                {
                    string part = graphemes.GetTextElement();
                    bytes += Encoding.UTF8.GetByteCount(part);
                    if (bytes > 90) break;
                    shortLabel.Append(part);
                }
                text.AppendFormat(CultureInfo.InvariantCulture, "\n{0}/{1}/{2}/{3}\t{4}",
                    proxy ? "setproxy" : "b", id, i, options[i].Token(), shortLabel);
            }
            return proxy ? "NM1/p/" + text : text.ToString();
        }

        internal static string ProxyOptions(string action, int x, int y) =>
            World != null && VisibleCell(World, x, y, out int cell) ?
                BuildingOptions(action.Substring(3), cell, x, y) : "ERROR Building unavailable";

        private static bool Natural(string value, out int number) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;

        private static readonly Lazy<Action<BuildingEnabledButton>> ToggleMenu =
            new Lazy<Action<BuildingEnabledButton>>(() => {
                var method = AccessTools.DeclaredMethod(typeof(BuildingEnabledButton), "OnMenuToggle", Type.EmptyTypes);
                return method == null ? null : AccessTools.MethodDelegate<Action<BuildingEnabledButton>>(method);
            });

        private static readonly System.Reflection.FieldInfo ToggleIndex =
            AccessTools.DeclaredField(typeof(BuildingEnabledButton), "ToggleIdx");

        internal static string NativeBuilding(string[] p, int x, int y)
        {
            var world = World;
            if (p.Length < 3 || world == null || !VisibleCell(world, x, y, out int cell))
                return "ERROR Building unavailable";
            var target = FindBuilding(cell);
            var button = target == null ? null : target.GetComponent<BuildingEnabledButton>();
            var toggle = target == null ? null : target.GetComponent<Toggleable>();
            if (target == null || target.GetComponent<BuildingComplete>() == null ||
                target.GetComponent<KPrefabID>().InstanceID != NativeManagement.Number(p[2])) return "ERROR Building changed";
            if (p[1] == "settings" || p[1] == "filter" || p[1] == "recipe")
                return NativeManagement.BuildingSettings(target, p);
            if (button == null || toggle == null || p.Length != (p[1] == "switch" ? 5 : 3)) return "ERROR No building switch";
            int slot = (int)ToggleIndex.GetValue(button);
            bool queued = toggle.IsToggleQueued(slot);
            if (p.Length == 5)
            {
                if (p[3] != (button.IsEnabled ? "1" : "0") || p[4] != (queued ? "1" : "0")) return "ERROR Switch changed";
                ToggleMenu.Value(button);
                queued = toggle.IsToggleQueued(slot);
            }
            return "NM1/b/" + p[2] + "/" + (button.IsEnabled ? "1" : "0") + "/" + (queued ? "1" : "0");
        }

        private static List<Setting> Settings(GameObject building, bool proxy)
        {
            var options = new List<Setting>();
            var button = building.GetComponent<BuildingEnabledButton>();
            var toggleable = building.GetComponent<Toggleable>();
            if (button != null && toggleable != null && ToggleIndex != null && ToggleMenu.Value != null)
            {
                bool queued = toggleable.IsToggleQueued((int)ToggleIndex.GetValue(button));
                string label = queued ? "取消已排队的开关操作" : button.IsEnabled ? "停用建筑" : "启用建筑";
                options.Add(new Setting("power/" + button.IsEnabled + "/" + queued, label, () => ToggleMenu.Value(button)));
            }
            if (proxy) AddProxyControls(options, building);
            AddFilters(options, building.GetComponent<TreeFilterable>());
            AddRecipes(options, building.GetComponent<ComplexFabricator>());
            return options;
        }

        private static void AddFilters(List<Setting> options, TreeFilterable filter)
        {
            foreach (Tag tag in EditableTags(filter).OrderBy(t => t.ToString(), StringComparer.Ordinal))
            {
                bool on = filter.ContainsTag(tag);
                options.Add(new Setting("filter/" + tag + "/" + on, (on ? "停止收纳 " : "允许收纳 ") + tag, () => {
                    if (on) filter.RemoveTagFromFilter(tag);
                    else filter.AddTagToFilter(tag);
                }));
            }
        }

        internal static HashSet<Tag> EditableTags(TreeFilterable filter)
        {
            var storage = filter == null ? null : filter.GetFilterStorage();
            var tags = new HashSet<Tag>();
            if (storage == null || !filter.showUserMenu || storage.storageFilters == null) return tags;
            var discovery = DiscoveredResources.Instance;
            foreach (Tag category in storage.storageFilters)
            {
                HashSet<Tag> members;
                if (discovery.TryGetDiscoveredResourcesFromTag(category, out members) && members != null)
                    tags.UnionWith(members);
                if (discovery.IsDiscovered(category)) tags.Add(category);
            }
            if (filter.ForbiddenTags != null) tags.ExceptWith(filter.ForbiddenTags);
            return tags;
        }

        private static void AddRecipes(List<Setting> options, ComplexFabricator fabricator)
        {
            if (fabricator == null) return;
            foreach (var recipe in fabricator.GetRecipes().OrderBy(r => r.id, StringComparer.Ordinal))
            {
                if (!RecipeAvailable(recipe)) continue;
                int count = fabricator.GetRecipeQueueCount(recipe);
                if (count == ComplexFabricator.QUEUE_INFINITE) continue;
                string label = recipe.GetUIName(false) + " [" + count + "]";
                string state = recipe.id + "/" + count.ToString(CultureInfo.InvariantCulture);
                if (count < ComplexFabricator.MAX_QUEUE_SIZE)
                    options.Add(new Setting("add/" + state, "+1 " + label, () => fabricator.SetRecipeQueueCount(recipe, count + 1)));
                if (count > 0)
                    options.Add(new Setting("remove/" + state, "−1 " + label, () => fabricator.SetRecipeQueueCount(recipe, count - 1)));
            }
        }

        internal static bool RecipeAvailable(ComplexRecipe recipe) =>
            recipe.IsRequiredTechOrPOIUnlocked() && !recipe.IsAnyProductDeprecated() &&
            (!recipe.RequiresAllIngredientsDiscovered || recipe.ingredients.All(ingredient =>
                DiscoveredResources.Instance.IsDiscovered(ingredient.material) ||
                (ingredient.possibleMaterials != null && ingredient.possibleMaterials.Any(DiscoveredResources.Instance.IsDiscovered))));
    }
}
