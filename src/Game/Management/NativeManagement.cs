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
            if (key.Length>1 && key[0]=='i')
            {
                int id=Number(key.Substring(1));
                if (id<1) return null;
                if (CoopSession.IsNativeClient) return guestSchedules.TryGetValue(id,out var local) ? local : null;
                return ScheduleManager.Instance.GetSchedules().FirstOrDefault(s=>StableId(s)==id);
            }
            var candidates = ScheduleManager.Instance.GetSchedules().Where(s => Key(s) == key).Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        static string LocalScheduleKey(Schedule s)
        {
            foreach (var pair in guestSchedules) if (pair.Value==s) return "i"+pair.Key;
            return null;
        }
        static int Id(Component component) => component.GetComponent<KPrefabID>().InstanceID;
        static MinionIdentity FindDupe(int id) => Components.LiveMinionIdentities.Items.FirstOrDefault(d => d != null && Id(d) == id);
        static List<ChoreGroup> Jobs() => Db.Get().ChoreGroups.resources.Where(g => g.userPrioritizable).OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
        static void Send(string route, int x = 0, int y = 0)
        {
            if (!NativeGuestScene.Ready || !CoopSession.IsNativeClient || CoopSession.NativeView == null) return;
            if (route == "research" || route == "overview" || route.StartsWith("workers/",StringComparison.Ordinal) || route.StartsWith("population/",StringComparison.Ordinal) || route.StartsWith("schedules/", StringComparison.Ordinal) || route.StartsWith("schedule/", StringComparison.Ordinal) ||
                route.StartsWith("choices/", StringComparison.Ordinal) || route.StartsWith("diet/", StringComparison.Ordinal) ||
                route.StartsWith("vitals/", StringComparison.Ordinal) || route.StartsWith("profile/", StringComparison.Ordinal) ||
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
            if (!hasHud && ClusterManager.Instance?.activeWorld!=null) Send("population/"+ClusterManager.Instance.activeWorldId);
            var menu = ManagementMenu.Instance;
            var researchScreen = CurrentResearchScreen;
            if ((researchScreen != null && researchScreen.isActiveAndEnabled && researchScreen.IsScreenActive()) ||
                PlanScreen.Instance?.ActiveCategoryToggleInfo != null)
                Send("research");
            bool schedules = WorkerTableOpen(menu?.scheduleScreen);
            bool priorities = WorkerTableOpen(menu?.jobsScreen);
            bool foods = menu?.consumablesScreen != null && menu.consumablesScreen.isActiveAndEnabled && menu.consumablesScreen.gameObject.activeInHierarchy;
            if (schedules || priorities) Send("schedules/" + nextSchedulePage);
            if (schedules || priorities) Send("workers/"+workerPage);
            if (OverviewOpen && PauseScreen.Instance != null && PauseScreen.Instance.IsActive()) Send("overview");
            if (schedules)
            {
                var list = ScheduleManager.Instance.GetSchedules();
                if (list.Count > 0)
                {
                    string key=LocalScheduleKey(list[(scheduleCursor++ & int.MaxValue) % list.Count]);
                    if (key!=null) Send("schedule/"+key);
                }
            }
            if (schedules || priorities || foods)
            {
                var dupes = Components.LiveMinionIdentities.Items;
                if (dupes.Count > 0)
                {
                    var dupe = dupes[(dupeCursor++ & int.MaxValue) % dupes.Count];
                    if (dupe != null) Send((foods ? "diet/" : "dupe/") + Id(dupe));
                }
            }
            PollBuilding();
            PollVitals();
            PollProfile();
        }
        internal static void Reset()
        {
            if (listening) CoopSession.CommandResult -= Receive;
            listening = applying = false;
            observed.Clear(); pendingEdits.Clear(); lastResearch = null; nextPoll = nextEdit = 0; scheduleCursor = dupeCursor = 0;
            ResetBuilding();
            ResetColony();
            ResetWorkers();
            populationWorld=-1;
            hasHud=false; hudInventory=null; sampledWorld=null;
            hudScreen=null; stressMeter=null; rationMeter=null;
            ResetVitals();
            ResetProfile();
        }

        internal static bool TryExecute(string action, int x, int y, out string result)
        {
            result = null;
            if (action == null || !action.StartsWith("nm/", StringComparison.Ordinal)) return false;
            result = Conflict;
            if (action.Length > (action.StartsWith("nm/rota/",StringComparison.Ordinal) ? 1024 : 256)) return true;
            var p = action.Split('/');
            switch (p[1])
            {
                case "clock": result = ApplyClock(p); break;
                case "name": result = ApplyDupeName(p); break;
                case "population":
                    if (p.Length==3) result=Population(Number(p[2]));
                    break;
                case "workers":
                    if (p.Length==3) result=WorkerPage(Number(p[2]));
                    break;
                case "overview":
                    if (p.Length == 2) result = ColonyOverview();
                    break;
                case "schedules":
                    if (p.Length == 3) result = SchedulePage(Number(p[2]));
                    break;
                case "vitals":
                    if (p.Length == 3) result = ReadHostVitals(Number(p[2]));
                    break;
                case "profile":
                    if (p.Length == 3) result = ReadHostProfile(Number(p[2]));
                    break;
                case "learn":
                case "hat":
                    result = EditSkill(p);
                    break;
                case "choices": result=PersonChoices(p); break;
                case "diet": if (p.Length==3) result=ReadDiet(Number(p[2])); break;
                case "permit": result=SetDiet(p); break;
                case "clinic": result=ClinicEdit(p); break;
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
                case "rota":
                    result=EditScheduleStructure(p);
                    break;
                case "dupe":
                case "assign":
                case "priority":
                    result = DupeRequest(p);
                    break;
                case "readings":
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
                if (current == null || FindSchedule(p[3]) != current || next == null) return Conflict;
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
            return Prefix + "d/" + p[2] + "/" + (current==null ? "none" : "i"+StableId(current)) + "/" + string.Concat(jobs.Select(j => consumer.GetPersonalPriority(j).ToString(CultureInfo.InvariantCulture)));
        }
        static void Receive(string value)
        {
            if (!NativeGuestScene.Ready || !value.StartsWith(Prefix, StringComparison.Ordinal)) return;
            if (value.StartsWith("NM1/choices/",StringComparison.Ordinal))
            {
                SelectedDupe()?.GetComponent<NativePersonMenu>()?.Receive(value);
                return;
            }
            if (value.StartsWith("NM1/p/", StringComparison.Ordinal))
            {
                SelectedProxy?.Receive(value.Substring(6));
                return;
            }
            var p = value.Split('/');
            applying = true;
            try
            {
                if (p[1] == "pop" && p.Length==5) ApplyPopulation(Number(p[2]),Number(p[3]),Number(p[4]));
                else if (p[1]=="workers" && p.Length==6) ApplyWorkerPage(p);
                else if (p[1] == "diet") ApplyDietPage(p);
                else if (p[1] == "permit") ApplyDiet(p);
                else if (p[1] == "g") ReceiveReadings(p);
                else if (p[1] == "sl" && p.Length == 4) ApplySchedulePage(p[2],p[3]);
                else if (p[1] == "ov" && p.Length == 3 && OverviewOpen) ApplyOverview(p[2]);
                else if (p[1] == "r" && p.Length == 3) ApplyResearch(p[2]);
                else if (p[1] == "v" && p.Length == 4) ApplyVitals(Number(p[2]), p[3]);
                else if (p[1] == "pf" && p.Length == 4) ApplyProfile(Number(p[2]), p[3]);
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
                    if (jobs.Count != p[4].Length || p[4].Any(c => c < '0' || c > '5')) return;
                    if (dupe==null)
                    {
                        if (workerCards.TryGetValue(Number(p[2]),out var card) && card!=null)
                            UpdateWorker(card,p[3],jobs,p[4]);
                        return;
                    }
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
                var key = LocalScheduleKey(__instance);
                var blocks = __instance.GetBlocks();
                if (__1 != null && key!=null && __0 >= 0 && __0 < blocks.Count)
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
                var current=__1==null ? null : LocalScheduleKey(__1.GetSchedule());
                var next=LocalScheduleKey(__0);
                if (current!=null && next!=null)
                    Send("assign/" + (__1.GetComponent<GuestWorkerCard>()?.HostId ?? Id(__1)) + "/" + current + "/" + next);
                return false;
            }
        }
        [HarmonyPatch(typeof(ChoreConsumer), "SetPersonalPriority")]
        static class EditPriority
        {
            static bool Prefix(ChoreConsumer __instance, ChoreGroup __0, int __1)
            {
                if (!Guard) return true;
                if (__0 != null)
                    Send("priority/" + Id(__instance) + "/" + __0.Id + "/" + __instance.GetPersonalPriority(__0) + "/" + __1);
                return false;
            }
        }

    }
}
