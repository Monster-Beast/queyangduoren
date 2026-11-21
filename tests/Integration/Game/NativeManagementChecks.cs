using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using HarmonyLib;
using Queyang.Game;
using Packet = Queyang.Core.Wire;

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
            CheckColony(Exec);
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
            CheckDuplicant(dupe, id, Exec);

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
            return "research=true schedule=true priority=true vitals=true profile=true catalogue=true overview=true switch=" + testedSwitch;
        }

        static void CheckColony(Func<string,string> exec)
        {
            var manager=ScheduleManager.Instance;
            var field=AccessTools.DeclaredField(typeof(ScheduleManager),"schedules");
            var original=manager.GetSchedules();
            var groups=Db.Get().ScheduleGroups.allGroups;
            var source=new List<Schedule> { new Schedule("基础",groups,false), new Schedule("晚班",groups,false),
                new Schedule("第三班",groups,false) };
            var replica=new List<Schedule> { new Schedule("基础",groups,false), new Schedule("晚班",groups,false) };
            void Sync()
            {
                field.SetValue(manager,source);
                var pages=Enumerable.Range(0,(source.Count+1)/2).Select(i=>exec("schedules/"+i).Split('/')).ToArray();
                field.SetValue(manager,replica);
                foreach (var p in pages)
                {
                    Expect(p.Length==4 && p[1]=="sl","schedule directory");
                    NativeManagement.ApplySchedulePage(p[2],p[3]);
                }
                Expect(source.Select(s=>s.name).SequenceEqual(replica.Select(s=>s.name)),"schedule directory order");
            }
            try
            {
                Sync();
                var same=replica[1];
                source[1].name="新晚班"; source[1].alarmActivated=true;
                source[1].AddTimetable(source[0].GetBlocks().ToList());
                source.RemoveAt(0);
                Sync();
                Expect(replica.Count==2 && ReferenceEquals(same,replica[0]) && same.alarmActivated &&
                    same.GetBlocks().Select(b=>b.GroupId).SequenceEqual(source[0].GetBlocks().Select(b=>b.GroupId)),
                    "rename, delete and timetable growth");
            }
            finally { field.SetValue(manager,original); NativeManagement.Reset(); }

            string summary=exec("overview");
            Expect(summary.StartsWith("NM1/ov/",StringComparison.Ordinal),"host overview");
            NativeManagement.ApplyOverview(summary.Substring(7));
            string first=NativeManagement.OverviewText;
            NativeManagement.ApplyOverview(summary.Substring(7));
            var inventory=ClusterManager.Instance.activeWorld.worldInventory;
            Expect(first==NativeManagement.OverviewText && first.Contains(GameUtil.GetFormattedCalories(RationTracker.Get().CountAmount(null,inventory,false))),
                "calories and repeated summary");
            NativeManagement.Reset();
        }

        // Uses the disposable terrain room after the physics checks, before discarding that world.
        internal static string CheckUnlockProjection()
        {
            var tech=Db.Get().Techs.resources.First(t=>!t.Disabled && t.FoundNode && !t.IsComplete() &&
                t.unlockedItems.Any(i=>Assets.GetBuildingDef(i.Id)!=null && !i.IsComplete()));
            var entry=tech.unlockedItems.First(i=>Assets.GetBuildingDef(i.Id)!=null && !i.IsComplete());
            var apply=AccessTools.DeclaredMethod(typeof(NativeManagement),"ApplyResearch");
            var canBuild=AccessTools.DeclaredMethod(typeof(PlanScreen),"TechRequirementsMet");
            string Capture()=>HostWorld.Execute("nm/research",0,0).Substring("NM1/r/".Length);
            bool Unlocked()=>(bool)canBuild.Invoke(PlanScreen.Instance,new object[]{entry});
            string initial=Capture();
            Expect(!Unlocked(),"stock build entry initially locked");
            Research.Instance.GetOrAdd(tech).Purchased();
            string complete=Capture();
            Expect(initial!=complete && Unlocked(),"native completion emitted research change");
            apply.Invoke(null,new object[]{initial});
            Expect(!tech.IsComplete() && !Unlocked(),"restore test baseline");
            apply.Invoke(null,new object[]{complete});
            Expect(tech.IsComplete() && Unlocked(),"replayed completion unlocks stock building requirement");
            return tech.Id+" -> "+entry.Id;
        }

        static void CheckDuplicant(MinionIdentity dupe, int id, Func<string,string> exec)
        {
            BinaryReader Query(string path, string type)
            {
                string prefix = "NM1/" + type + "/" + id + "/";
                string reply = exec(path + "/" + id);
                Expect(reply.StartsWith(prefix, StringComparison.Ordinal), path);
                var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(reply.Substring(prefix.Length).Replace('_','/'))));
                Expect(r.ReadByte() == (type=="pf" ? 2 : 1), path + " version");
                return r;
            }
            var driver = dupe.GetComponent<ChoreDriver>();
            var current = driver.GetCurrentChore();
            float stamina = Db.Get().Amounts.Stamina.Lookup(dupe).value;
            float hitPoints = dupe.GetComponent<Health>().hitPoints;
            using (var r = Query("vitals","v"))
            {
                Packet.Text(r,256); Packet.Text(r,256);
                bool working = r.ReadBoolean();
                if (current == null) Expect(!working, "idle duplicant shows a chore");
                void Errand()
                {
                    Expect(Packet.Text(r,256).Length > 0, "empty chore");
                    Packet.Text(r,256); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
                }
                if (working) Errand();
                int count = r.ReadByte();
                for (int i=0; i<count; i++) Errand();
                int n = r.ReadByte(); bool sawStamina = false, sawHealth = false;
                for (int i=0; i<n; i++)
                {
                    int index = r.ReadByte(); float value = r.ReadSingle();
                    r.ReadSingle(); r.ReadSingle(); Packet.Text(r,128);
                    if (index == 0) sawStamina = value == stamina;
                    if (index == 6) sawHealth = value == hitPoints;
                }
                Expect(sawStamina && sawHealth && driver.GetCurrentChore() == current &&
                    Db.Get().Amounts.Stamina.Lookup(dupe).value == stamina, "read-only stamina, health, chore");
                Packet.End(r);
            }
            using (var r = Query("profile","pf"))
            {
                var resume = dupe.GetComponent<MinionResume>();
                var food = dupe.GetComponent<ConsumableConsumer>();
                Expect(r.ReadSingle() == resume.TotalExperienceGained && Packet.Text(r,160) == (resume.CurrentHat ?? "") &&
                    Packet.Text(r,160) == (resume.TargetHat ?? ""), "experience and hats");
                void Ids(System.Collections.Generic.IEnumerable<int> source)
                {
                    int count = r.ReadUInt16();
                    var actual = Enumerable.Range(0,count).Select(_=>r.ReadInt32());
                    Expect(actual.SequenceEqual(source.Distinct().OrderBy(k=>k)), "profile IDs");
                }
                Ids(resume.MasteryBySkillID.Where(p=>p.Value).Select(p=>new HashedString(p.Key).HashValue));
                Ids(resume.GrantedSkillIDs.Select(s=>new HashedString(s).HashValue));
                Ids(food.forbiddenTagSet.Select(t=>t.GetHash()));
                Ids(food.dietaryRestrictionTagSet.Select(t=>t.GetHash()));
                var effects = dupe.GetComponent<Klei.AI.Effects>();
                int count = r.ReadByte();
                for (int i=0; i<count; i++)
                {
                    var instance = effects.Get(new HashedString(r.ReadInt32()));
                    float remaining = r.ReadSingle();
                    string label = Packet.Text(r,256);
                    Expect(instance != null && remaining == (instance.effect.duration>0 ? Math.Max(0,instance.timeRemaining) : -1) &&
                        !string.IsNullOrEmpty(label), "active effect");
                }
                Packet.End(r);
            }
            Expect(exec("vitals/2147483647") == "NM1/v/2147483647/-" &&
                exec("profile/2147483647") == "NM1/pf/2147483647/-", "missing duplicant");
        }
    }
}
