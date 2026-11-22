using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamDualWork
    {
        const string TestName="Co-op night shift";
        int stage,doneRound,prior,initialCount;
        bool clicked,opened;
        int reportedStage=-1;
        float lastReport;
        string jobId,groupId;
        string baselineLiveIds;
        FixtureFrame frame;
        static string Data(CSteamID lobby,string key)=>SteamMatchmaking.GetLobbyData(lobby,"qy_work_"+key);
        static void Set(CSteamID lobby,string key,string value)
        {
            if(!SteamMatchmaking.SetLobbyData(lobby,"qy_work_"+key,value)) throw new IOException("Work fixture metadata failed");
        }
        static T Field<T>(object obj,string name)=>(T)AccessTools.Field(obj.GetType(),name).GetValue(obj);
        bool Picture(string page,int round,bool host)
        {
            if (frame==null)
            {
                string dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                frame=new FixtureFrame(Path.Combine(dir,$"dual-{(host?"host":"guest")}-{page}-{round}.png"));
                frame.Capture(); return false;
            }
            if (!frame.Complete) return false;
            frame=null; return true;
        }
        void Next(){stage++;clicked=false;opened=false;}

        internal bool Verify(CSteamID lobby,int round,bool host,Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_BASELINE_ONLY")=="1" || Environment.GetEnvironmentVariable("QY_SKIP_WORK")=="1" || doneRound==round) return true;
            var manager=ManagementMenu.Instance;
            if (manager==null) return false;
            bool newcomer=Environment.GetEnvironmentVariable("QY_SKIP_PERSON")!="1";
            if (!int.TryParse(SteamMatchmaking.GetLobbyData(lobby,newcomer?"qy_person_id":"qy_skill_id"),out int id)) return false;
            if (!host && newcomer && round==1 && baselineLiveIds==null)
            {
                var roster=SteamMatchmaking.GetLobbyData(lobby,"qy_census_printed");
                if (string.IsNullOrEmpty(roster)) return false;
                baselineLiveIds=string.Join(",",roster.Split(',').Where(value=>int.Parse(value)!=id));
                VerifyLiveIds(baselineLiveIds);
                log("DUAL_WORK_LIVE_BEFORE round=1 closedTables=True liveIds="+baselineLiveIds);
            }
            if (stage<5 && !opened)
            {
                manager.OnButtonClick(Field<ManagementMenu.ManagementMenuToggleInfo>(manager,"scheduleInfo"));
                opened=true;
            }
            var dupe=Components.LiveMinionIdentities.Items.FirstOrDefault(m=>m!=null && m.GetComponent<KPrefabID>().InstanceID==id);
            var stored=dupe!=null ? null : UnityEngine.Object.FindObjectsByType<StoredMinionIdentity>(FindObjectsSortMode.None)
                .FirstOrDefault(m=>m.GetComponent("GuestWorkerCard") is Component card && Field<int>(card,"HostId")==id);
            IAssignableIdentity identity=(IAssignableIdentity)dupe??stored;
            if (identity==null) return false;
            var consumer=dupe!=null ? (IPersonalPriorityManager)dupe.GetComponent<ChoreConsumer>() : stored;
            if (host && jobId==null)
            {
                var j=Db.Get().ChoreGroups.resources.First(k=>k.userPrioritizable && !consumer.IsChoreGroupDisabled(k) && k.Id!="Dig");
                jobId=j.Id; prior=consumer.GetPersonalPriority(j); initialCount=ScheduleManager.Instance.GetSchedules().Count;
                Set(lobby,"job",jobId);Set(lobby,"prior",prior.ToString());Set(lobby,"count",initialCount.ToString());
            }
            if (!host && jobId==null)
            {
                if (!int.TryParse(Data(lobby,"prior"),out prior) || !int.TryParse(Data(lobby,"count"),out initialCount)) return false;
                jobId=Data(lobby,"job"); if(string.IsNullOrEmpty(jobId)){jobId=null;return false;}
            }
            var list=ScheduleManager.Instance.GetSchedules();
            var s=list.FirstOrDefault(item=>item.name==TestName);
            var scheduleScreen=manager.scheduleScreen;
            if (reportedStage!=stage || Time.unscaledTime-lastReport>=8f)
            {
                log($"DUAL_WORK_PROGRESS round={round} stage={stage} schedules={list.Count} renamed={s!=null} clicked={clicked} status={Queyang.Game.CoopSession.Status}");
                reportedStage=stage;lastReport=Time.unscaledTime;
            }
            if (stage<5 && !scheduleScreen.gameObject.activeInHierarchy) return false;
            var entries=stage<5 ? scheduleScreen.GetComponentsInChildren<ScheduleScreenEntry>(false) : null;
            if (stage==0)
            {
                if (!host && round==1 && !clicked)
                {
                    // Wait until the native rows have been bound to their host IDs.
                    var candidate=entries.FirstOrDefault();
                    if (candidate==null || !ReadyKey(candidate.schedule)) return false;
                    Field<KButton>(scheduleScreen,"addScheduleButton").SignalClick(KKeyCode.Mouse0);clicked=true;
                }
                if (list.Count!=initialCount+1) return false;
                stage++;clicked=false;
            }
            if (stage==1)
            {
                if (!host && round==1 && s==null && !clicked)
                {
                    var entry=entries.FirstOrDefault(e=>e.schedule==list.Last());
                    if (entry==null || !ReadyKey(entry.schedule)) return false;
                    var title=Field<EditableTitleBar>(entry,"title");
                    // The new row enters the hierarchy before KMonoBehaviour.Start binds its input.
                    if (!title.isSpawned) return false;
                    if (!title.inputField.gameObject.activeInHierarchy)
                    {
                        title.editNameButton.SignalClick(KKeyCode.Mouse0);
                        return false;
                    }
                    if (!title.inputField.isFocused) return false;
                    title.inputField.text=TestName;
                    title.inputField.DeactivateInputField();
                    clicked=true;
                    log("DUAL_WORK_NAME_SUBMITTED nativeEditButton=True nativeText="+title.inputField.text);
                }
                if (s==null) return false;
                stage++;clicked=false;
            }
            if (stage==2)
            {
                var entry=entries.FirstOrDefault(e=>e.schedule==s);
                if (entry==null) return false;
                if (host && groupId==null)
                {
                    groupId=Db.Get().ScheduleGroups.allGroups.First(g=>g.Id!=s.GetBlock(9).GroupId).Id;
                    Set(lobby,"group",groupId);
                }
                if (!host) groupId=Data(lobby,"group");
                if (string.IsNullOrEmpty(groupId)) return false;
                if (!host && round==1 && !clicked)
                {
                    var paint=Field<Dictionary<string,GameObject>>(entry,"paintButtons")[groupId];
                    paint.GetComponent<MultiToggle>().onClick();
                    var blocks=Field<Dictionary<GameObject,List<ScheduleBlockButton>>>(entry,"blockButtonsByTimetableRow");
                    if (!entry.PaintBlock(blocks.Values.First()[9])) throw new IOException("Native schedule painter did not select the new block");
                    clicked=true;
                }
                if (s.GetBlock(9).GroupId!=groupId) return false;
                stage++;clicked=false;
            }
            var person=((Component)identity).GetComponent<Schedulable>();
            if (stage==3)
            {
                if (!host && round==1 && !clicked)
                {
                    var widget=scheduleScreen.GetComponentsInChildren<ScheduleMinionWidget>(false)
                        .FirstOrDefault(w=>w.schedulable==person);
                    if (widget==null) return false;
                    widget.ChangeAssignment(s,person);clicked=true;
                }
                if (person.GetSchedule()!=s) return false;
                stage++;
            }
            if (stage==4)
            {
                if (!Picture("schedule",round,host)) return false;
                log($"DUAL_{(host?"HOST":"GUEST")}_SCHEDULE round={round} added=True renamed=True paintedSlot=9 group={groupId} assigned={id}");
                manager.CloseAll();Next();
            }
            if (stage==5)
            {
                if (!opened) { manager.TogglePriorities();opened=true;return false; }
                var jobs=manager.jobsScreen;
                if (!jobs.gameObject.activeInHierarchy) return false;
                var group=Db.Get().ChoreGroups.resources.First(g=>g.Id==jobId);
                if (!host && round==1 && !clicked)
                {
                    var row=jobs.rows.FirstOrDefault(r=>r.GetIdentity()==identity);
                    var cols=Field<Dictionary<string,TableColumn>>(jobs,"columns");
                    if (row==null || !cols.TryGetValue(jobId,out var col)) return false;
                    var button=row.GetWidget(col);
                    if (button==null || !button.activeInHierarchy) return false;
                    button.GetComponent<OptionSelector>().OnChangePriority(button,1);clicked=true;
                }
                int target=(prior+1)%6;
                if (consumer.GetPersonalPriority(group)!=target) return false;
                if (!Picture("priorities",round,host)) return false;
                log($"DUAL_{(host?"HOST":"GUEST")}_PRIORITY round={round} id={id} group={jobId} old={prior} confirmed={target} nativeTable=True afterBaselinePerson={newcomer}");
                if (!host && newcomer && round==1)
                {
                    VerifyLiveIds(baselineLiveIds);
                    if (stored==null || !stored.isSpawned || stored.GetComponent<Brain>()!=null)
                        throw new InvalidDataException("Newcomer priorities did not use a spawned display-only card");
                    log("DUAL_WORK_LIVE_AFTER round=1 liveIds="+baselineLiveIds+" cardHostId="+id+" nativeBrain=False");
                }
                manager.CloseAll();doneRound=round;stage=0;clicked=opened=false;return true;
            }
            return false;
        }
        static void VerifyLiveIds(string expected)
        {
            var actual=string.Join(",",Components.LiveMinionIdentities.Items.Where(m=>m!=null &&
                ClusterUtil.GetMyWorldId(m)==ClusterManager.Instance.activeWorldId)
                .Select(m=>m.GetComponent<KPrefabID>().InstanceID).OrderBy(i=>i));
            if(actual!=expected) throw new InvalidDataException("Management table changed the guest simulation roster: expected="+expected+" observed="+actual);
        }
        static bool ReadyKey(Schedule s)=>AccessTools.DeclaredMethod(typeof(Queyang.Game.NativeManagement),"LocalScheduleKey")
            .Invoke(null,new object[]{s})!=null;
    }
}
