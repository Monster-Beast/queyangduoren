using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        const string Prefix = "NM1/";
        const string Conflict = "ERROR Management target or value changed";
        static bool listening, applying;
        static float nextPoll, nextEdit;
        sealed class Edit { internal string Route; internal int X, Y; }
        static readonly Queue<Edit> pendingEdits = new Queue<Edit>();
        static int scheduleCursor, dupeCursor;
        static readonly HashSet<string> observed = new HashSet<string>();
        static string lastResearch;
        static readonly FieldInfo ResearchPanel = AccessTools.DeclaredField(typeof(ManagementMenu), "researchScreen");
        static ResearchScreen CurrentResearchScreen => ManagementMenu.Instance == null ? null : ResearchPanel.GetValue(ManagementMenu.Instance) as ResearchScreen;
        internal static bool Guard => !applying && NativeGuestScene.Active && global::Game.Instance != null &&
            global::Game.Instance.GameStarted() && !global::Game.Instance.IsLoading();

        internal static int Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : -1;
        internal static string Digest(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value)), 0, 12).Replace("-", "");
        }
        static string Key(Schedule s) => s == null ? "none" : Digest(s.name ?? "");
        static Schedule FindSchedule(string key)
        {
            var candidates = ScheduleManager.Instance.GetSchedules().Where(s => Key(s) == key).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        static int Id(Component component) => component.GetComponent<KPrefabID>().InstanceID;
        static MinionIdentity FindDupe(int id) => Components.LiveMinionIdentities.Items.FirstOrDefault(d => d != null && Id(d) == id);
        static List<ChoreGroup> Jobs() => Db.Get().ChoreGroups.resources.Where(g => g.userPrioritizable).OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        static void Send(string route, int x = 0, int y = 0)
        {
            if (!NativeGuestScene.Ready || !CoopSession.IsNativeClient || CoopSession.NativeView == null) return;
            if (route == "research" || route.StartsWith("schedule/", StringComparison.Ordinal) ||
                route.StartsWith("proxy/", StringComparison.Ordinal) ||
                route.StartsWith("dupe/", StringComparison.Ordinal) || route.StartsWith("building/", StringComparison.Ordinal) ||
                route.StartsWith("settings/", StringComparison.Ordinal))
                CoopSession.Command("nm/" + route, x, y);
            else if (pendingEdits.Count < 256)
                pendingEdits.Enqueue(new Edit { Route = route, X = x, Y = y });
        }
        internal static void Tick()
        {
            if (!listening) { CoopSession.CommandResult += Receive; listening = true; }
            if (pendingEdits.Count > 0 && Time.unscaledTime >= nextEdit)
            {
                var edit = pendingEdits.Dequeue();
                CoopSession.Command("nm/" + edit.Route, edit.X, edit.Y);
                nextEdit = Time.unscaledTime + .22f;
            }
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + .7f;
            var menu = ManagementMenu.Instance;
            var researchScreen = CurrentResearchScreen;
            if (researchScreen != null && researchScreen.isActiveAndEnabled && researchScreen.IsScreenActive())
                Send("research");
            bool schedules = menu?.scheduleScreen != null && menu.scheduleScreen.isActiveAndEnabled && menu.scheduleScreen.gameObject.activeInHierarchy;
            bool priorities = menu?.jobsScreen != null && menu.jobsScreen.isActiveAndEnabled && menu.jobsScreen.gameObject.activeInHierarchy;
            if (schedules)
            {
                var list = ScheduleManager.Instance.GetSchedules();
                if (list.Count > 0) Send("schedule/" + Key(list[(scheduleCursor++ & int.MaxValue) % list.Count]));
            }
            if (schedules || priorities)
            {
                var dupes = Components.LiveMinionIdentities.Items;
                if (dupes.Count > 0)
                {
                    var dupe = dupes[(dupeCursor++ & int.MaxValue) % dupes.Count];
                    if (dupe != null) Send("dupe/" + Id(dupe));
                }
            }
            PollBuilding();
        }
        internal static void Reset()
        {
            if (listening) CoopSession.CommandResult -= Receive;
            listening = applying = false;
            observed.Clear(); pendingEdits.Clear(); lastResearch = null; nextPoll = nextEdit = 0; scheduleCursor = dupeCursor = 0;
            ResetBuilding();
        }

        internal static bool TryExecute(string action, int x, int y, out string result)
        {
            result = null;
            if (action == null || !action.StartsWith("nm/", StringComparison.Ordinal)) return false;
            result = Conflict;
            if (action.Length > 256) return true;
            var p = action.Split('/');
            switch (p[1])
            {
                case "proxy":
                case "setproxy":
                    result = HostWorld.ProxyOptions(action, x, y);
                    break;
                case "research":
                case "tech":
                    result = ResearchRequest(p);
                    break;
                case "schedule":
                case "block":
                    if (p.Length != (p[1] == "schedule" ? 3 : 6)) break;
                    var s = FindSchedule(p[2]);
                    if (s == null) break;
                    var groups = Db.Get().ScheduleGroups.allGroups;
                    var blocks = s.GetBlocks();
                    if (blocks.Count > 2048) break;
                    if (p.Length == 6)
                    {
                        int slot = Number(p[3]);
                        var group = groups.FirstOrDefault(g => g.Id == p[5]);
                        if (slot < 0 || slot >= blocks.Count || group == null || blocks[slot].GroupId != p[4]) break;
                        s.SetBlockGroup(slot, group);
                    }
                    var blockIds = blocks.Select(b => (byte)(groups.FindIndex(g => g.Id == b.GroupId) + 1)).ToArray();
                    result = Prefix + "s/" + p[2] + "/" + Convert.ToBase64String(blockIds).Replace('/', '_');
                    break;
                case "dupe":
                case "assign":
                case "priority":
                    result = DupeRequest(p);
                    break;
                case "building":
                case "switch":
                case "settings":
                case "filter":
                case "recipe":
                    result = HostWorld.NativeBuilding(p, x, y);
                    break;
            }
            return true;
        }

        static string DupeRequest(string[] p)
        {
            int length = p[1] == "dupe" ? 3 : p[1] == "assign" ? 5 : 6;
            if (p.Length != length) return Conflict;
            var dupe = FindDupe(Number(p[2]));
            if (dupe == null) return Conflict;
            var consumer = dupe.GetComponent<ChoreConsumer>();
            var person = dupe.GetComponent<Schedulable>();
            if (consumer == null || person == null) return Conflict;
            var current = person.GetSchedule();
            var jobs = Jobs();
            if (p[1] == "assign")
            {
                var next = FindSchedule(p[4]);
                if (current == null || Key(current) != p[3] || next == null) return Conflict;
                if (current != next) { current.Unassign(person); next.Assign(person); current = next; }
            }
            if (p[1] == "priority")
            {
                var job = jobs.FirstOrDefault(j => j.Id == p[3]);
                int n = Number(p[5]);
                if (job == null || consumer.IsChoreGroupDisabled(job) || n < 0 || n > 5 ||
                    consumer.GetPersonalPriority(job) != Number(p[4])) return Conflict;
                consumer.SetPersonalPriority(job, n);
            }
            return Prefix + "d/" + p[2] + "/" + Key(current) + "/" + string.Concat(jobs.Select(j => consumer.GetPersonalPriority(j).ToString(CultureInfo.InvariantCulture)));
        }
        static void Receive(string value)
        {
            if (!NativeGuestScene.Ready || !value.StartsWith(Prefix, StringComparison.Ordinal)) return;
            if (value.StartsWith("NM1/p/", StringComparison.Ordinal))
            {
                SelectedProxy?.Receive(value.Substring(6));
                return;
            }
            var p = value.Split('/');
            applying = true;
            try
            {
                if (p[1] == "r" && p.Length == 3) ApplyResearch(p[2]);
                else if (p[1] == "b") ApplyBuilding(p);
                else if (p[1] == "f") ApplySettings(p);
                else if (p[1] == "s" && p.Length == 4)
                {
                    var s = FindSchedule(p[2]);
                    if (s == null) return;
                    var ids = Convert.FromBase64String(p[3].Replace('_', '/'));
                    var blocks = s.GetBlocks();
                    var groups = Db.Get().ScheduleGroups.allGroups;
                    if (ids.Length != blocks.Count || ids.Any(i => i == 0 || i > groups.Count)) return;
                    for (int i = 0; i < ids.Length; i++)
                        if (blocks[i].GroupId != groups[ids[i] - 1].Id) s.SetBlockGroup(i, groups[ids[i] - 1]);
                    observed.Add("s/" + p[2]);
                }
                else if (p[1] == "d" && p.Length == 5)
                {
                    var dupe = FindDupe(Number(p[2]));
                    var jobs = Jobs();
                    if (dupe == null || jobs.Count != p[4].Length || p[4].Any(c => c < '0' || c > '5')) return;
                    var person = dupe.GetComponent<Schedulable>();
                    var s = FindSchedule(p[3]);
                    if (person != null && s != null && person.GetSchedule() != s)
                    { person.GetSchedule()?.Unassign(person); s.Assign(person); }
                    var c = dupe.GetComponent<ChoreConsumer>();
                    if (c == null) return;
                    for (int i = 0; i < jobs.Count; i++)
                        if (c.GetPersonalPriority(jobs[i]) != p[4][i] - '0') c.SetPersonalPriority(jobs[i], p[4][i] - '0');
                    observed.Add("d/" + p[2]);
                }
            }
            finally { applying = false; }
        }

        [HarmonyPatch(typeof(Schedule), "SetBlockGroup")]
        static class EditSlot
        {
            static bool Prefix(Schedule __instance, int __0, ScheduleGroup __1)
            {
                if (!Guard) return true;
                var key = Key(__instance);
                var blocks = __instance.GetBlocks();
                if (__1 != null && observed.Contains("s/" + key) && __0 >= 0 && __0 < blocks.Count)
                    Send("block/" + key + "/" + __0 + "/" + blocks[__0].GroupId + "/" + __1.Id);
                return false;
            }
        }
        [HarmonyPatch(typeof(ScheduleMinionWidget), "ChangeAssignment")]
        static class EditAssignment
        {
            static bool Prefix(Schedule __0, Schedulable __1)
            {
                if (!Guard) return true;
                if (__1 != null && __0 != null && observed.Contains("d/" + Id(__1)))
                    Send("assign/" + Id(__1) + "/" + Key(__1.GetSchedule()) + "/" + Key(__0));
                return false;
            }
        }
        [HarmonyPatch(typeof(ChoreConsumer), "SetPersonalPriority")]
        static class EditPriority
        {
            static bool Prefix(ChoreConsumer __instance, ChoreGroup __0, int __1)
            {
                if (!Guard) return true;
                if (__0 != null && observed.Contains("d/" + Id(__instance)))
                    Send("priority/" + Id(__instance) + "/" + __0.Id + "/" + __instance.GetPersonalPriority(__0) + "/" + __1);
                return false;
            }
        }

        // Operations outside the replicated schedule model must not create guest-only schedules.
        [HarmonyPatch]
        static class ScheduleStructure
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var name in new[] { "DuplicateSchedule", "DeleteSchedule", "OnNameChanged", "OnAlarmClicked", "OnResetClicked",
                    "DuplicateTimetableRow", "AddTimetableRow", "RemoveTimetableRow" })
                    yield return AccessTools.DeclaredMethod(typeof(ScheduleScreenEntry), name);
                yield return AccessTools.DeclaredMethod(typeof(ScheduleManager), "AddDefaultSchedule");
                yield return AccessTools.DeclaredMethod(typeof(ScheduleScreen), "OnAddScheduleClick");
                yield return AccessTools.DeclaredMethod(typeof(Schedule), "RotateBlocks");
            }
            static bool Prefix() => !Guard;
        }
    }
}
