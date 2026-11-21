using System;
using System.Linq;
using HarmonyLib;
using Queyang.Game;

namespace Queyang.Integration
{
    internal static class NativeManagementChecks
    {
        static void Expect(bool condition, string field)
        {
            if (!condition) throw new InvalidOperationException("Native management mismatch: " + field);
        }

        internal static string Run()
        {
            var root = Environment.GetEnvironmentVariable("QY_SMOKE_ROOT");
            if (string.IsNullOrEmpty(root) || !string.Equals(root, Util.GetKleiRootPath(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native management tests require the isolated profile.");
            string Exec(string action) => HostWorld.Execute("nm/" + action, 0, 0);
            string QueueVersion() => NativeManagement.Digest(string.Join(",", Research.Instance.GetResearchQueue().Select(q => q.tech.Id)));

            var research = Research.Instance;
            var oldQueue = research.GetResearchQueue().ToArray();
            try
            {
                var tech = Db.Get().Techs.resources.First(t => !t.Disabled && t.FoundNode && !t.IsComplete() && !research.IsBeingResearched(t));
                var initial = Exec("research");
                Expect(initial.StartsWith("NM1/r/"), "research snapshot");
                var route = "tech/select/" + tech.Id + "/" + QueueVersion();
                var updated = Exec(route);
                Expect(updated.StartsWith("NM1/r/") && updated != initial && research.IsBeingResearched(tech), "set research");
                Expect(Exec(route).StartsWith("ERROR "), "reject stale research");
                Expect(Exec("tech/cancel/" + tech.Id + "/" + QueueVersion()).StartsWith("NM1/r/") && !research.IsBeingResearched(tech), "cancel research");
            }
            finally
            {
                research.SetActiveResearch(null, true);
                var append = AccessTools.DeclaredMethod(typeof(Research), "AddTechToQueue", new[] { typeof(Tech) });
                foreach (var old in oldQueue) if (!research.IsBeingResearched(old.tech)) append.Invoke(research, new object[] { old.tech });
                if (oldQueue.Length != 0) research.SetActiveResearch(oldQueue[0].tech, false);
            }

            var schedule = ScheduleManager.Instance.GetSchedules().First();
            var key = NativeManagement.Digest(schedule.name);
            var groups = Db.Get().ScheduleGroups.allGroups;
            var slot = groups.First(g => g.Id == schedule.GetBlocks()[0].GroupId);
            var next = groups.First(g => g != slot);
            try
            {
                var route = "block/" + key + "/0/" + slot.Id + "/" + next.Id;
                Expect(Exec("schedule/" + key).StartsWith("NM1/s/"), "schedule snapshot");
                Expect(Exec(route).StartsWith("NM1/s/") && schedule.GetBlocks()[0].GroupId == next.Id, "paint schedule");
                Expect(Exec(route).StartsWith("ERROR "), "reject stale slot");
            }
            finally { schedule.SetBlockGroup(0, slot); }

            var dupe = Components.LiveMinionIdentities.Items.First(d => d != null);
            var consumer = dupe.GetComponent<ChoreConsumer>();
            var job = Db.Get().ChoreGroups.resources.First(j => j.userPrioritizable && !consumer.IsChoreGroupDisabled(j));
            int id = dupe.GetComponent<KPrefabID>().InstanceID, oldPriority = consumer.GetPersonalPriority(job);
            try
            {
                int priority = oldPriority == 4 ? 3 : 4;
                var route = "priority/" + id + "/" + job.Id + "/" + oldPriority + "/" + priority;
                Expect(Exec("dupe/" + id).StartsWith("NM1/d/"), "personal snapshot");
                Expect(Exec(route).StartsWith("NM1/d/") && consumer.GetPersonalPriority(job) == priority, "personal priority");
                Expect(Exec(route).StartsWith("ERROR ") && Exec("priority/" + id + "/" + job.Id + "/" + priority + "/999").StartsWith("ERROR "), "reject stale or invalid priority");
            }
            finally { consumer.SetPersonalPriority(job, oldPriority); }

            Expect(Exec("assign/" + id + "/bogus/bogus").StartsWith("ERROR "), "reject invalid assignment");
            Expect(HostWorld.Execute("nm/switch/1/1/0", -1, -1).StartsWith("ERROR "), "reject invalid building coordinate");
            bool testedSwitch = false;
            foreach (var b in Components.BuildingCompletes.Items)
            {
                if (b == null || b.GetComponent<BuildingEnabledButton>() == null) continue;
                int cell = Grid.PosToCell(b);
                if (!Grid.IsValidCell(cell) || Grid.Visible[cell] == 0 || HostWorld.FindBuilding(cell) != b.gameObject) continue;
                Grid.CellToXY(cell, out int x, out int y);
                string buildingId = b.GetComponent<KPrefabID>().InstanceID.ToString();
                string Query() => HostWorld.Execute("nm/building/" + buildingId, x, y);
                string Switch(string state) => HostWorld.Execute("nm/switch/" + buildingId + "/" + state.Split('/')[3] + "/" + state.Split('/')[4], x, y);
                var first = Query();
                if (!first.StartsWith("NM1/b/")) continue;
                try
                {
                    string changed = Switch(first);
                    Expect(changed.StartsWith("NM1/b/") && Query() != first, "queue building switch");
                    Expect(Switch(first).StartsWith("ERROR "), "reject stale building switch");
                }
                finally
                {
                    var actual = Query();
                    if (actual.StartsWith("NM1/b/") && actual != first) Switch(actual);
                }
                Expect(Query() == first, "cancel building switch");
                testedSwitch = true;
                break;
            }
            return "research=true schedule=true priority=true switch=" + testedSwitch;
        }
    }
}
