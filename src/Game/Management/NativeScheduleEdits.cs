using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static string ScheduleCatalogueVersion(IEnumerable<Schedule> list) =>
            Digest(string.Join(",",list.Select(s=>StableId(s)+"."+Key(s)+"."+s.GetBlocks().Count+
                (s.alarmActivated ? "a" : "s")+(s.isDefaultForBionics ? "b" : "n"))));

        static string ScheduleRevision(Schedule s) => Digest(Key(s)+(s.alarmActivated?"1":"0")+
            string.Join(",",s.GetBlocks().Select(b=>b.GroupId)));

        static void RequestSchedule(Schedule target,string operation,string value="")
        {
            string key=target==null?"new":LocalScheduleKey(target);
            string revision=target==null?catalogueVersion:ScheduleRevision(target);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(revision))
            {
                NativeGuestFeedback.Show("正在同步作息表，请稍后重试");
                return;
            }
            Send("rota/"+key+"/"+revision+"/"+operation+"/"+value);
        }

        static string EditScheduleStructure(string[] p)
        {
            if (p.Length!=6) return Conflict;
            var manager=ScheduleManager.Instance;
            var list=manager.GetSchedules();
            if (p[2]=="new")
            {
                if (p[4]!="add" || p[5]!="" || list.Count>=MaxSchedules || ScheduleCatalogueVersion(list)!=p[3]) return Conflict;
                manager.AddDefaultSchedule(false,false);
                return SchedulePage(0);
            }
            var s=FindSchedule(p[2]);
            if (s==null || ScheduleRevision(s)!=p[3]) return Conflict;
            var blocks=s.GetBlocks();
            int row=Number(p[5]);
            int days=blocks.Count/24;
            bool refresh=true;
            switch(p[4])
            {
                case "copy":
                    if (p[5]!="" || list.Count>=MaxSchedules ||
                        Encoding.UTF8.GetByteCount(STRINGS.UI.SCHEDULESCREEN.SCHEDULE_NAME_COPY_PREFIX+s.name)>512) return Conflict;
                    manager.DuplicateSchedule(s); refresh=false; break;
                case "delete":
                    if (p[5]!="" || list.Count<=1) return Conflict;
                    manager.DeleteSchedule(s); refresh=false; break;
                case "name":
                    string name;
                    try { name=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(p[5].Replace('_','/'))); }
                    catch (FormatException) { return Conflict; }
                    catch (DecoderFallbackException) { return Conflict; }
                    if (name.Any(char.IsControl) || Encoding.UTF8.GetByteCount(name)>512) return Conflict;
                    s.name=name; break;
                case "alarm":
                    if (p[5]!="") return Conflict;
                    s.alarmActivated=!s.alarmActivated; break;
                case "reset":
                    if (p[5]!="") return Conflict;
                    blocks.Clear(); blocks.AddRange(Schedule.GetScheduleBlocksFromGroupDefaults(Db.Get().ScheduleGroups.allGroups));
                    s.ProgressTimetableIdx=0; break;
                case "copyday":
                    if (row<0 || row>=days || blocks.Count+24>2048) return Conflict;
                    s.InsertTimetable(row+1,blocks.GetRange(row*24,24).Select(b=>new ScheduleBlock(b.name,b.GroupId)).ToList());
                    break;
                case "removeday":
                    if (row<0 || row>=days || days<=1) return Conflict;
                    s.RemoveTimetable(row); break;
                case "left": case "right": case "up": case "down":
                    if (row<0 || row>=days) return Conflict;
                    if (p[4]=="left" || p[4]=="right") s.RotateBlocks(p[4]=="left",row);
                    else if (!s.ShiftTimetable(p[4]=="up",row)) return Conflict;
                    break;
                default: return Conflict;
            }
            if (refresh)
            {
                if (ScheduleScreen.Instance!=null) RefreshSchedules.Invoke(ScheduleScreen.Instance,new object[]{list});
                ScheduleChanged.Invoke(s,null);
            }
            return SchedulePage(0);
        }

        [HarmonyPatch(typeof(ScheduleScreen),"OnAddScheduleClick")]
        static class AddScheduleButton
        {
            static bool Prefix() { if (!Guard) return true; RequestSchedule(null,"add"); return false; }
        }

        [HarmonyPatch]
        static class ScheduleEntryButtons
        {
            static readonly Dictionary<string,string> operations=new Dictionary<string,string> {
                {"DuplicateSchedule","copy"},{"DeleteSchedule","delete"},{"OnDeleteClicked","delete"},
                {"OnAlarmClicked","alarm"},{"OnResetClicked","reset"} };
            static IEnumerable<MethodBase> TargetMethods() => operations.Keys.Select(n=>AccessTools.DeclaredMethod(typeof(ScheduleScreenEntry),n,Type.EmptyTypes));
            static bool Prefix(ScheduleScreenEntry __instance, MethodBase __originalMethod)
            {
                if (!Guard) return true;
                RequestSchedule(__instance.schedule,operations[__originalMethod.Name]); return false;
            }
        }

        [HarmonyPatch(typeof(ScheduleScreenEntry),"OnNameChanged")]
        static class NameSchedule
        {
            static bool Prefix(ScheduleScreenEntry __instance,string __0)
            {
                if (!Guard) return true;
                if (__0!=null && !__0.Any(char.IsControl) && Encoding.UTF8.GetByteCount(__0)<=512)
                    RequestSchedule(__instance.schedule,"name",Convert.ToBase64String(Encoding.UTF8.GetBytes(__0)).Replace('/','_'));
                return false;
            }
        }

        [HarmonyPatch(typeof(ScheduleScreenEntry),"DuplicateTimetableRow")]
        static class CopyScheduleDay
        {
            static bool Prefix(ScheduleScreenEntry __instance,int __0)
            { if (!Guard) return true; RequestSchedule(__instance.schedule,"copyday",__0.ToString()); return false; }
        }
        [HarmonyPatch(typeof(ScheduleScreenEntry),"RemoveTimetableRow")]
        static class DeleteScheduleDay
        {
            static bool Prefix(ScheduleScreenEntry __instance,GameObject __0,List<GameObject> ___timetableRows)
            { if (!Guard) return true; RequestSchedule(__instance.schedule,"removeday",___timetableRows.IndexOf(__0).ToString()); return false; }
        }
        [HarmonyPatch(typeof(Schedule),nameof(Schedule.RotateBlocks))]
        static class RotateScheduleDay
        {
            static bool Prefix(Schedule __instance,bool __0,int __1)
            { if (!Guard) return true; RequestSchedule(__instance,__0?"left":"right",__1.ToString()); return false; }
        }
        [HarmonyPatch(typeof(Schedule),nameof(Schedule.ShiftTimetable))]
        static class ShiftScheduleDay
        {
            static bool Prefix(Schedule __instance,bool __0,int __1)
            { if (!Guard) return true; RequestSchedule(__instance,__0?"up":"down",__1.ToString()); return false; }
        }
    }
}
