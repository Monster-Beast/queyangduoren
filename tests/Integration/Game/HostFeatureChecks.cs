using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Queyang.Core;
using Queyang.Game;

namespace Queyang.Integration
{
    // Uses the actual copied colony. No substitute implementations of native schedules/research/filters.
    internal static class HostFeatureChecks
    {
        public static void Run(Action<string> record)
        {
            var root = Environment.GetEnvironmentVariable("QY_SMOKE_ROOT");
            if (string.IsNullOrEmpty(root) || !string.Equals(root, Util.GetKleiRootPath(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Supplemental checks require an isolated ONI profile.");
            Check(record, "authority", Authority);
            Check(record, "research", ResearchQueue);
            Check(record, "schedule", ScheduleBlockEdit);
            Check(record, "personal-priority", DupePriority);
            Check(record, "building", () => BuildingSettings(record));
            Check(record, "viewport", () => ViewportCost(record));
            if (Environment.GetEnvironmentVariable("QY_SMOKE_PERSISTENCE") == "1")
                Check(record, "native-save", () => HostPersistenceSave.Run(record));
            record("FEATURE_CHECKS_DONE");
        }

        private static void Check(Action<string> record, string name, System.Action test)
        {
            try { test(); record("FEATURE PASS " + name); }
            catch (NotSupportedException e) { record("FEATURE SKIP " + name + ": " + e.Message); }
            catch (Exception e) { record("FEATURE FAIL " + name + ": " + e); }
        }

        private static void Require(bool ok, string failure)
        {
            if (!ok) throw new InvalidOperationException(failure);
        }

        private static List<KeyValuePair<string, string>> Menu(string route)
        {
            var result = HostWorld.Execute("manage/" + route, 0, 0);
            Require(result.StartsWith("M1:", StringComparison.Ordinal), "Management page missing");
            using (var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(result.Substring(3)))))
            {
                Queyang.Core.Wire.Text(r); // Title is localized; tests follow operation IDs.
                int count = r.ReadByte();
                var rows = new List<KeyValuePair<string, string>>();
                for (int i = 0; i < count; i++)
                    rows.Add(new KeyValuePair<string, string>(Queyang.Core.Wire.Text(r), Queyang.Core.Wire.Text(r)));
                Queyang.Core.Wire.End(r);
                return rows;
            }
        }

        private static bool Is(string route, string family) =>
            route.StartsWith(family + "/", StringComparison.Ordinal);

        private static string FirstRoute(string menu, string family) =>
            Menu(menu).First(row => Is(row.Value, family)).Value;

        private static void Authority()
        {
            var view = ColonyView.Decode(HostWorld.Capture(-1, -1));
            int i = Array.FindIndex(view.Cells, c => (c.Flags & 1) != 0);
            Require(i >= 0, "No visible cells");
            int x = view.X + i % ColonyView.Width, y = view.Y + i / ColonyView.Width;
            var speed = SpeedControlScreen.Instance;
            int original = speed.GetSpeed();
            bool paused = speed.IsPaused;
            foreach (string command in new[] { "pause", "speed", "save", "bogus" })
                Require(HostWorld.Execute(command, x, y).StartsWith("ERROR "), "Unauthorized " + command);
            Require(speed.GetSpeed() == original && speed.IsPaused == paused, "Time control changed");
            Require(HostWorld.Execute("dig", int.MaxValue, int.MinValue).StartsWith("ERROR "), "Bad coordinate accepted");
            Require(HostWorld.Execute("cancel:0", x, y).StartsWith("ERROR "), "Invalid ID accepted");
            Require(HostWorld.Execute("inspect", x, y).StartsWith("OK "), "Visible cell inspection rejected");
        }

        private static void ResearchQueue()
        {
            var research = Research.Instance;
            var original = research.GetResearchQueue().ToArray();
            try
            {
                research.SetActiveResearch(null, true);
                var options = Menu("tech/0").Where(row => Is(row.Value, "addTech")).ToArray();
                if (options.Length < 2) throw new NotSupportedException("Need two available research projects");
                Menu(options[0].Value);
                int count = research.GetResearchQueue().Count;
                Require(count > 0, "Accepting a technology did not modify Research.GetResearchQueue");
                Menu(options[1].Value);
                Require(research.GetResearchQueue().Count == count, "Stale research revision modified the queue");
                var queued = new HashSet<string>(research.GetResearchQueue().Select(i => i.tech.Name), StringComparer.Ordinal);
                var next = Menu("tech/0").FirstOrDefault(row => Is(row.Value, "addTech") && !queued.Contains(row.Key));
                if (next.Value == null) throw new NotSupportedException("No independent project on the first page");
                Menu(next.Value);
                Require(research.GetResearchQueue().Count > count, "Appending to an existing research queue had no effect");
                var cancel = Menu("queue/0").First(row => Is(row.Value, "cancelTech"));
                Menu(cancel.Value);
                Require(!research.GetResearchQueue().Any(i => i.tech.Name == cancel.Key), "Cancellation had no effect");
            }
            finally
            {
                research.SetActiveResearch(null, true);
                var append = HarmonyLib.AccessTools.DeclaredMethod(typeof(Research), "AddTechToQueue", new[] { typeof(Tech) });
                foreach (var job in original)
                    if (!research.IsBeingResearched(job.tech)) append.Invoke(research, new object[] { job.tech });
                if (original.Length > 0) research.SetActiveResearch(original[0].tech, false);
            }
        }

        private static void ScheduleBlockEdit()
        {
            var schedule = ScheduleManager.Instance.GetSchedules().First();
            var first = schedule.GetBlocks()[0];
            var original = Db.Get().ScheduleGroups.allGroups.First(g => g.Id == first.GroupId);
            try
            {
                string schedulePage = FirstRoute("schedules/0", "blocks");
                string slotPage = FirstRoute(schedulePage, "slot");
                var oldMenu = Menu(slotPage);
                var diff = oldMenu.First(row => Is(row.Value, "block") && row.Key != original.Name);
                Menu(diff.Value);
                string applied = schedule.GetBlocks()[0].GroupId;
                Require(applied != original.Id, "Time slot was not edited");
                var stale = oldMenu.First(row => Is(row.Value, "block") && row.Key == original.Name);
                Menu(stale.Value);
                Require(schedule.GetBlocks()[0].GroupId == applied, "Stale schedule edit applied");
            }
            finally { schedule.SetBlockGroup(0, original); }
        }

        private static void DupePriority()
        {
            string dupePage = FirstRoute("dupes/0", "dupe");
            var consumer = Components.LiveMinionIdentities.Items.First(d => d != null).GetComponent<ChoreConsumer>();
            var groupPage = FirstRoute(dupePage, "groups");
            var menu = Menu(groupPage);
            var groups = Db.Get().ChoreGroups.resources.Where(g => g.userPrioritizable).ToList();
            var group = groups.Take(5).FirstOrDefault(g => !consumer.IsChoreGroupDisabled(g));
            if (group == null) throw new NotSupportedException("First duplicant cannot do the first five jobs");
            var groupRoute = menu.First(row => Is(row.Value, "priority") &&
                row.Key.StartsWith(group.Name + " · ", StringComparison.Ordinal)).Value;
            int original = consumer.GetPersonalPriority(group);
            try
            {
                var entries = Menu(groupRoute);
                int next = original == 3 ? 2 : 3;
                Menu(entries.First(row => row.Value.EndsWith("/" + next, StringComparison.Ordinal)).Value);
                Require(consumer.GetPersonalPriority(group) == next, "Personal priority unchanged");
                Menu(entries.First(row => row.Value.EndsWith("/" + original, StringComparison.Ordinal)).Value);
                Require(consumer.GetPersonalPriority(group) == next, "Stale priority write was accepted");
            }
            finally { consumer.SetPersonalPriority(group, original); }
        }

        private static void BuildingSettings(Action<string> record)
        {
            int checkedTargets = 0;
            var world = ClusterManager.Instance.activeWorld;
            foreach (var obj in Components.BuildingCompletes.Items)
            {
                if (obj == null) continue;
                int anchor = Grid.PosToCell(obj);
                if (!Grid.IsValidCell(anchor) || Grid.WorldIdx[anchor] != world.id || Grid.Visible[anchor] == 0) continue;
                Grid.CellToXY(anchor, out int x, out int y);
                var v = ColonyView.Decode(HostWorld.Capture(x, y));
                int id = v.Cells[(y - v.Y) * ColonyView.Width + x - v.X].TargetId;
                if (id == 0) continue;
                string query = "building/" + id + "/0";
                string menu = HostWorld.Execute(query, x, y);
                if (!menu.StartsWith("BLD1 ") || !menu.Contains("\n")) continue;
                string action = menu.Split('\n')[1].Split('\t')[0];
                string changed = HostWorld.Execute(action, x, y);
                Require(changed.StartsWith("BLD1 ") && changed != menu, "Building field did not change");
                Require(HostWorld.Execute(action, x, y).StartsWith("ERROR "), "Stale building field accepted");
                checkedTargets++;
                record("FEATURE DETAIL building " + obj.Def.PrefabID);
                if (checkedTargets == 3) break;
            }
            if (checkedTargets == 0) throw new NotSupportedException("No editable completed building in the copied colony");
        }

        private static void ViewportCost(Action<string> record)
        {
            HostWorld.Capture(-1, -1); // warm up the codec before sampling.
            var durations = new List<double>();
            int max = 0;
            for (int i = 0; i < 20; i++)
            {
                var clock = Stopwatch.StartNew();
                byte[] bytes = HostWorld.Capture(-1, -1);
                clock.Stop();
                durations.Add(clock.Elapsed.TotalMilliseconds);
                max = Math.Max(max, bytes.Length);
                ColonyView.Decode(bytes);
            }
            durations.Sort();
            record(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "FEATURE DETAIL main-thread capture+encode 20 warm single-viewport samples p95={0:0.00}ms maxPayload={1}B",
                durations[18], max));
            Require(max < Queyang.Core.Wire.MaxFrame, "Viewport exceeded the declared budget");
        }
    }
}
