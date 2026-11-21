using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Queyang.Game
{
    // The host builds the menu from live game objects. Keys are valid only in the current process and session.
    internal static class ColonyManagement
    {
        sealed class Handle { public readonly int Value; public Handle(object obj) { Value = checked(++serial); } }
        static int serial;
        static readonly ConditionalWeakTable<object,Handle> Handles = new ConditionalWeakTable<object,Handle>();
        static string Key(object o) => o == null ? "0" : Handles.GetValue(o,x => new Handle(x)).Value.ToString(CultureInfo.InvariantCulture);
        static ManagementPage Home(string title = "殖民地管理") => new ManagementPage(title)
            .Add("研究项目","tech/0").Add("在研队列","queue/0").Add("复制人排班与优先级","dupes/0").Add("编辑排班时段","schedules/0");
        static ManagementPage Conflict() => Home("目标已经改变，请重新选择。");
        static int Number(string s) => int.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out var n) ? n : -1;
        static ManagementPage List<T>(string title,string route,IList<T> items,string pageNo,Action<ManagementPage,T> row)
        {
            var page = new ManagementPage(title);
            int index = Number(pageNo);
            if (index < 0 || index > (items.Count == 0 ? 0 : (items.Count-1)/5)) return Conflict();
            foreach (var item in items.Skip(index*5).Take(5)) row(page,item);
            if (index > 0) page.Add("上一页",route+"/"+(index-1));
            if ((long)(index+1)*5 < items.Count) page.Add("下一页",route+"/"+(index+1));
            return page.Add("返回首页","home");
        }
        static string QueueVersion()
        {
            var keys = string.Join(",",Research.Instance.GetResearchQueue().Select(i => Key(i.tech)));
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(keys)),0,8).Replace("-","");
        }
        public static string Execute(string command)
        {
            if (command.Length > 160) return Conflict().Pack();
            return Route(command.Substring("manage/".Length).Split('/')).Pack();
        }
        static ManagementPage Route(string[] p)
        {
            var db = Db.Get();
            if (p.Length == 1 && p[0]=="home") return Home();
            if (p.Length == 2 && (p[0]=="tech" || p[0]=="queue"))
            {
                bool queue = p[0]=="queue";
                var items = queue ? Research.Instance.GetResearchQueue().Select(i=>i.tech).ToList() :
                    db.Techs.resources.Where(t=>!t.Disabled && t.FoundNode && !t.IsComplete()).ToList();
                var version = QueueVersion();
                return List(queue ? "点击取消该研究" : "点击加入研究队列",p[0],items,p[1],
                    (m,t)=>m.Add(t.Name,(queue?"cancelTech":"addTech")+"/"+Key(t)+"/"+version));
            }
            if (p.Length == 3 && (p[0]=="addTech" || p[0]=="cancelTech"))
            {
                var tech = db.Techs.resources.FirstOrDefault(t=>Key(t)==p[1] && !t.Disabled && t.FoundNode && !t.IsComplete());
                if (tech == null || p[2] != QueueVersion()) return Conflict();
                if (p[0]=="cancelTech") Research.Instance.CancelResearch(tech,true);
                else
                {
                    // ONI's public setter only seeds an empty queue. Insert prerequisites using the
                    // same game method, then let the public setter sort and notify research stations.
                    if (Research.Instance.GetResearchQueue().Count != 0)
                    {
                        var append = HarmonyLib.AccessTools.DeclaredMethod(typeof(Research), "AddTechToQueue", new[] { typeof(Tech) });
                        if (append == null) return Home("当前游戏版本无法追加研究项目");
                        append.Invoke(Research.Instance, new object[] { tech });
                    }
                    Research.Instance.SetActiveResearch(tech, false);
                }
                return Route(new[]{"queue","0"});
            }
            var schedules = ScheduleManager.Instance.GetSchedules();
            if (p.Length==2 && p[0]=="schedules")
                return List("选择排班","schedules",schedules,p[1],(m,s)=>m.Add(s.name,"blocks/"+Key(s)+"/0"));
            if (p.Length>=3 && (p[0]=="blocks" || p[0]=="slot" || p[0]=="block"))
            {
                var sched=schedules.FirstOrDefault(s=>Key(s)==p[1]);
                if (sched==null) return Conflict();
                var blocks=sched.GetBlocks();
                if (p.Length==3 && p[0]=="blocks")
                    return List("选择时段","blocks/"+p[1],Enumerable.Range(0,blocks.Count).ToList(),p[2],
                        (m,i)=>m.Add((i+1)+" · "+blocks[i].GroupId,"slot/"+p[1]+"/"+i));
                int slot=Number(p[2]);
                if (slot<0 || slot>=blocks.Count) return Conflict();
                var groups=db.ScheduleGroups.allGroups;
                if(p.Length==3 && p[0]=="slot")
                {
                    var m=new ManagementPage("选择新的时段类型");
                    foreach(var gr in groups.Take(12)) m.Add(gr.Name,"block/"+p[1]+"/"+slot+"/"+blocks[slot].GroupId+"/"+Key(gr));
                    return m.Add("返回首页","home");
                }
                if (p.Length!=5 || p[0]!="block" || blocks[slot].GroupId!=p[3]) return Conflict();
                var group=groups.FirstOrDefault(g=>Key(g)==p[4]);
                if(group==null) return Conflict();
                sched.SetBlockGroup(slot,group);
                return Route(new[]{"blocks",p[1],(slot/5).ToString()});
            }

            var dupes=Components.LiveMinionIdentities.Items.Where(d=>d!=null).ToList();
            if(p.Length==2 && p[0]=="dupes")
                return List("选择复制人","dupes",dupes,p[1],(m,d)=>m.Add(d.GetProperName(),"dupe/"+Key(d)));
            if (p.Length<2) return Conflict();
            var dupe=dupes.FirstOrDefault(d=>Key(d)==p[1]);
            if (dupe==null) return Conflict();
            var schedulable=dupe.GetComponent<Schedulable>();
            var current=schedulable==null ? null : schedulable.GetSchedule();
            if(p.Length==2 && p[0]=="dupe")
                return new ManagementPage(dupe.GetProperName()).Add("排班 · "+current?.name,"assignList/"+p[1]+"/0")
                    .Add("工作优先级","groups/"+p[1]+"/0").Add("返回首页","home");
            if(p.Length==3 && p[0]=="assignList")
                return List("选择新排班","assignList/"+p[1],schedules,p[2],(m,s)=>m.Add(s.name,"assign/"+p[1]+"/"+Key(current)+"/"+Key(s)));
            if(p.Length==4 && p[0]=="assign")
            {
                var next=schedules.FirstOrDefault(s=>Key(s)==p[3]);
                if(schedulable==null || next==null || Key(current)!=p[2]) return Conflict();
                if(current!=next){ current?.Unassign(schedulable); next.Assign(schedulable); }
                return Route(new[]{"dupe",p[1]});
            }
            var consumer=dupe.GetComponent<ChoreConsumer>();
            if(consumer==null) return Conflict();
            var jobs=db.ChoreGroups.resources.Where(g=>g.userPrioritizable).ToList();
            if(p.Length==3 && p[0]=="groups")
                return List("选择工作类型","groups/"+p[1],jobs,p[2],
                    (m,g)=>m.Add(g.Name+" · "+consumer.GetPersonalPriority(g),"priority/"+p[1]+"/"+Key(g)));
            if (p.Length<3) return Conflict();
            var job=jobs.FirstOrDefault(g=>Key(g)==p[2]);
            if(job==null || consumer.IsChoreGroupDisabled(job)) return Conflict();
            var old=consumer.GetPersonalPriority(job);
            if(p.Length==3 && p[0]=="priority")
            {
                var m=new ManagementPage(job.Name+" · "+old);
                for (int i=0;i<=5;i++) m.Add(i==0?"禁用":i.ToString(),"setPriority/"+p[1]+"/"+p[2]+"/"+old+"/"+i);
                return m.Add("返回首页","home");
            }
            if(p.Length==5 && p[0]=="setPriority" && old==Number(p[3]))
            {
                var value=Number(p[4]);
                if(value<0 || value>5) return Conflict();
                consumer.SetPersonalPriority(job,value);
                return Route(new[]{"priority",p[1],p[2]});
            }
            return Conflict();
        }
    }
}
