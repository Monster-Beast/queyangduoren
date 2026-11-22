using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        internal static bool PersonSelected(GameObject go) => SelectedDupe()==go && NativeGuestScene.Ready;
        internal static void PersonCommand(string route) => Send(route);

        static IEnumerable<IConsumableUIItem> Consumables()
        {
            foreach (var food in EdiblesManager.GetAllLoadedFoodTypes()) yield return food;
            foreach (var prefab in Assets.GetPrefabsWithTag(GameTags.Medicine))
            {
                var pill=prefab.GetComponent<MedicinalPillWorkable>();
                if (pill!=null) yield return pill;
            }
        }
        static bool EditableFood(IConsumableUIItem item) => item.Display && ConsumerManager.instance!=null &&
            ConsumerManager.instance.isDiscovered(new Tag(item.ConsumableId));
        static string Flag(bool value) => value ? "1" : "0";
        static int Owner(Assignable a) => a.assignee==null ? 0 : SaveId(IdentityObject(a.assignee));

        static string SetDiet(string[] p)
        {
            if (p.Length!=6 || (p[3]!="0" && p[3]!="1") || (p[4]!="0" && p[4]!="1") || p[3]==p[4]) return Conflict;
            var d=FindDupe(Number(p[2])); var item=Consumables().FirstOrDefault(c=>c.ConsumableId==p[5] && EditableFood(c));
            var c=d?.GetComponent<ConsumableConsumer>();
            if (d==null || ClusterUtil.GetMyWorldId(d)!=ClusterManager.Instance.activeWorldId || item==null || c==null) return "ERROR 消费项目或复制人不可用";
            bool next=p[4]=="1";
            if (next && c.IsDietRestricted(item.ConsumableId)) return "ERROR 复制人的饮食限制不允许该物品";
            if (Flag(c.IsPermitted(item.ConsumableId))!=p[3]) return "ERROR 消费许可已经变化";
            c.SetPermitted(item.ConsumableId,next);
            return Prefix+"permit/"+p[2]+"/"+Flag(c.IsPermitted(item.ConsumableId))+"/"+item.ConsumableId;
        }

        static string ReadDiet(int id)
        {
            var d=FindDupe(id);
            var c=d?.GetComponent<ConsumableConsumer>();
            if (d==null || ClusterUtil.GetMyWorldId(d)!=ClusterManager.Instance.activeWorldId || c==null ||
                c.forbiddenTagSet.Count>256 || c.dietaryRestrictionTagSet.Count>256) return Conflict;
            using (var buffer=new MemoryStream())
            using (var w=new BinaryWriter(buffer))
            {
                WriteIds(w,c.forbiddenTagSet.Select(t=>t.GetHash()),256);
                WriteIds(w,c.dietaryRestrictionTagSet.Select(t=>t.GetHash()),256);
                return Prefix+"diet/"+id+"/"+Convert.ToBase64String(buffer.ToArray()).Replace('/','_');
            }
        }

        static void ApplyDietPage(string[] p)
        {
            if (p.Length!=4 || p[3].Length>2800) return;
            var consumer=FindDupe(Number(p[2]))?.GetComponent<ConsumableConsumer>();
            if (consumer==null) return;
            using (var r=new BinaryReader(new MemoryStream(Convert.FromBase64String(p[3].Replace('_','/')))))
            {
                var forbidden=ReadIds(r,256).Select(v=>new Tag(v)).ToArray();
                var diet=ReadIds(r,256).Select(v=>new Tag(v)).ToArray();
                Queyang.Core.Wire.End(r);
                if (consumer.forbiddenTagSet.SetEquals(forbidden) && consumer.dietaryRestrictionTagSet.SetEquals(diet)) return;
                consumer.forbiddenTagSet.Clear(); consumer.forbiddenTagSet.UnionWith(forbidden);
                consumer.dietaryRestrictionTagSet.Clear(); consumer.dietaryRestrictionTagSet.UnionWith(diet);
                consumer.consumableRulesChanged?.Invoke();
            }
        }

        static void ApplyDiet(string[] p)
        {
            if (p.Length!=5 || (p[3]!="0" && p[3]!="1")) return;
            var c=FindDupe(Number(p[2]))?.GetComponent<ConsumableConsumer>();
            if (c!=null)
            {
                var tag=new Tag(p[4]);
                if (p[3]=="1") c.forbiddenTagSet.Remove(tag); else c.forbiddenTagSet.Add(tag);
                c.consumableRulesChanged?.Invoke();
            }
            nextProfile=0;
        }

        static string ClinicEdit(string[] p)
        {
            if (p.Length!=6 || (p[5]!="0" && p[5]!="1")) return Conflict;
            var d=FindDupe(Number(p[2]));
            var clinic=Components.Clinics.Items.FirstOrDefault(c=>c!=null && Id(c)==Number(p[3]));
            if (d==null || clinic==null || ClusterUtil.GetMyWorldId(clinic)!=ClusterManager.Instance.activeWorldId ||
                ClusterUtil.GetMyWorldId(d)!=ClusterManager.Instance.activeWorldId || Grid.Visible[Grid.PosToCell(clinic)]==0)
                return "ERROR 复制人或病床已不可用";
            var a=clinic.GetComponent<Assignable>();
            var proxy=d.assignableProxy.Get();
            if (a==null || proxy==null || !a.CanBeAssigned || Owner(a).ToString()!=p[4]) return "ERROR 病床分配已变化";
            if (p[5]=="0")
            {
                if (!a.IsAssignedTo(proxy)) return Conflict;
                a.Unassign();
            }
            else
            {
                if (a.assignee!=null || !a.CanAssignTo(proxy)) return "ERROR 病床已占用或复制人不满足原版治疗条件";
                a.Assign(proxy);
                if (!a.IsAssignedTo(proxy)) return "ERROR 房主未能分配病床";
            }
            return ReadHostProfile(Id(d));
        }

        static string PersonChoices(string[] p)
        {
            if (p.Length!=5 || Number(p[4])<0 || Number(p[4])>8192) return Conflict;
            int id=Number(p[2]), tab=Number(p[3]), page=Number(p[4]);
            var d=FindDupe(id);
            if (d==null || ClusterUtil.GetMyWorldId(d)!=ClusterManager.Instance.activeWorldId) return Conflict;
            var r=d.GetComponent<MinionResume>();
            var list=new List<KeyValuePair<string,string>>();
            void Add(string action,string label) => list.Add(new KeyValuePair<string,string>(action,DisplayText(label).Replace('\t',' ').Replace('\n',' ').Replace('\r',' ')));
            switch (tab)
            {
                case 0:
                    string stamp=SkillStamp(r);
                    foreach (var skill in Db.Get().Skills.resources)
                        if (!skill.deprecated && global::Game.IsCorrectDlcActiveForCurrentSave(skill) && !r.HasMasteredSkill(skill.Id) &&
                            (skill.requiredDuplicantModel==null || (Tag)skill.requiredDuplicantModel==Db.Get().Personalities.Get(d.personalityResourceId).model) &&
                            r.CanMasterSkill(r.GetSkillMasteryConditions(skill.Id)))
                            Add("learn/"+id+"/"+stamp+"/"+skill.Id,"学习 "+skill.Name+" · "+r.AvailableSkillpoints+" 点可用");
                    break;
                case 1:
                    string hats="hat/"+id+"/"+Digest(r.TargetHat??"")+"/";
                    if (!string.IsNullOrEmpty(r.TargetHat)) Add(hats+"-","摘下职业帽");
                    foreach (var h in r.GetAllHats())
                        if (!string.IsNullOrEmpty(h.Hat) && h.Hat!=r.TargetHat) Add(hats+h.Hat,"选择职业帽 · "+h.Source);
                    break;
                case 2:
                    var consumer=d.GetComponent<ConsumableConsumer>();
                    foreach (var item in Consumables().Where(EditableFood))
                    {
                        string name=item.ConsumableId;
                        if (consumer.IsDietRestricted(name)) continue;
                        bool old=consumer.IsPermitted(name);
                        Add("permit/"+id+"/"+Flag(old)+"/"+Flag(!old)+"/"+name,(old?"禁用 ":"允许 ")+item.ConsumableName);
                    }
                    break;
                case 3:
                    var proxy=d.assignableProxy.Get();
                    foreach (var clinic in Components.Clinics.Items)
                    {
                        if (clinic==null || ClusterUtil.GetMyWorldId(clinic)!=ClusterManager.Instance.activeWorldId ||
                            Grid.Visible[Grid.PosToCell(clinic)]==0) continue;
                        var a=clinic.GetComponent<Assignable>();
                        if (a==null || !a.CanBeAssigned || proxy==null) continue;
                        bool own=a.IsAssignedTo(proxy);
                        if (!own && (a.assignee!=null || !a.CanAssignTo(proxy))) continue;
                        Grid.CellToXY(Grid.PosToCell(clinic),out int x,out int y);
                        Add("clinic/"+id+"/"+Id(clinic)+"/"+Owner(a)+"/"+Flag(!own),(own?"取消治疗床位":"分配治疗床位")+" ("+x+", "+y+")");
                    }
                    break;
                default: return Conflict;
            }
            const int size=4;
            int pages=Math.Max(1,(list.Count+size-1)/size);
            page=Math.Min(page,pages-1);
            return Prefix+"choices/"+id+"/"+tab+"/"+page+"/"+pages+"\n"+
                string.Join("\n",list.Skip(page*size).Take(size).Select(e=>e.Key+"\t"+e.Value));
        }

        [HarmonyPatch(typeof(ConsumablesTableScreen),"on_click_consumable_info",new[]{typeof(GameObject)})]
        static class GuestConsumption
        {
            static readonly System.Reflection.MethodInfo row=AccessTools.Method(typeof(TableScreen),"GetWidgetRow",new[]{typeof(GameObject)});
            static bool Prefix(ConsumablesTableScreen __instance,GameObject __0)
            {
                if (!Guard) return true;
                var selected=(TableRow)row.Invoke(__instance,new object[]{__0});
                // The "all" and default rows also edit the colony's future-person defaults.
                // Individual rows use the normal ONI click path down to SetPermitted.
                if (selected?.GetIdentity() is MinionIdentity) return true;
                NativeGuestFeedback.Show("请在具体复制人的行中切换食物或药物。"); return false;
            }
        }

        internal static bool GuestPermit(ConsumableConsumer c,string food,bool next)
        {
            if (!Guard) return true;
            if (c.GetComponent<MinionIdentity>()!=null)
                Send("permit/"+Id(c)+"/"+Flag(c.IsPermitted(food))+"/"+Flag(next)+"/"+food);
            return false;
        }
    }

    // Newly printed people are rendered without a MinionResume or ChoreConsumer.
    // Present host-validated actions in ONI's existing selection menu.
    internal sealed class NativePersonMenu : KMonoBehaviour
    {
        static readonly string[] tabs={"学习技能","职业帽","食物和药物","治疗床位"};
        static readonly string[] empty={"当前没有可学习的技能","没有其他可选职业帽","没有可调整的消费许可","没有可分配的治疗床位"};
        int id,tab=-1,page,pages;
        string last;
        readonly List<KeyValuePair<string,string>> rows=new List<KeyValuePair<string,string>>();

        protected override void OnSpawn()
        {
            base.OnSpawn();
            var marker=GetComponent<NativeDupeIdentityMarker>();
            if (marker!=null) id=marker.HostId;
            Subscribe((int)GameHashes.RefreshUserMenu,Build);
            if (NativeManagement.PersonSelected(gameObject)) global::Game.Instance.userMenu.Refresh(gameObject);
        }

        internal void Poll(int target)
        {
            if (id!=target) { id=target; global::Game.Instance.userMenu.Refresh(gameObject); }
            if (tab>=0) NativeManagement.PersonCommand("choices/"+id+"/"+tab+"/"+page);
        }
        internal void Receive(string reply)
        {
            if (!NativeManagement.PersonSelected(gameObject) || reply==last) return;
            var lines=reply.Split('\n'); var h=lines[0].Split('/');
            if (h.Length!=6 || NativeManagement.Number(h[2])!=id || NativeManagement.Number(h[3])!=tab || lines.Length>5) return;
            int got=NativeManagement.Number(h[4]), count=NativeManagement.Number(h[5]);
            if (count<1 || got!=Math.Min(page,count-1)) return;
            page=got; pages=count; last=reply; rows.Clear();
            foreach (string line in lines.Skip(1))
            {
                var kv=line.Split('\t');
                if (kv.Length==2) rows.Add(new KeyValuePair<string,string>(kv[0],kv[1]));
            }
            global::Game.Instance.userMenu.Refresh(gameObject);
        }
        void Go(int nextTab,int nextPage)
        {
            tab=nextTab; page=nextPage; pages=0; last=null; rows.Clear();
            Poll(id); global::Game.Instance.userMenu.Refresh(gameObject);
        }
        void Add(string label,System.Action handler)
        {
            global::Game.Instance.userMenu.AddButton(gameObject,new KIconButtonMenu.ButtonInfo("action_follow_cam",label,handler,tooltipText:label),0);
        }
        void Build(object _)
        {
            if (!NativeGuestScene.Ready || id<=0) return;
            if (tab<0)
            {
                for (int i=0;i<tabs.Length;i++) { int t=i; Add(tabs[t],()=>Go(t,0)); }
                return;
            }
            Add("返回复制人操作",()=>Go(-1,0));
            foreach (var row in rows) { string cmd=row.Key; Add(row.Value,()=>{if (NativeManagement.PersonSelected(gameObject)) NativeManagement.PersonCommand(cmd);}); }
            if (rows.Count==0 && pages>0) Add(empty[tab],()=>NativeGuestFeedback.Show(empty[tab]));
            if (page>0) Add("上一页",()=>Go(tab,page-1));
            if (page+1<pages) Add("下一页",()=>Go(tab,page+1));
        }
    }
}
