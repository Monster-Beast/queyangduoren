using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Queyang.Integration
{
    internal sealed class SteamDualSkills
    {
        static bool BaselineOnly => Environment.GetEnvironmentVariable("QY_BASELINE_ONLY")=="1";
        MinionIdentity who;
        string skill,hat;
        int initialPoints, finishedRound;
        bool opened,learnClicked,hatClicked,reloaded;
        FixtureFrame frame;
        static readonly AccessTools.FieldRef<ManagementMenu,SkillsScreen> panel=
            AccessTools.FieldRefAccess<ManagementMenu,SkillsScreen>("skillsScreen");
        static readonly AccessTools.FieldRef<SkillsScreen,DropDown> dropdown=
            AccessTools.FieldRefAccess<SkillsScreen,DropDown>("hatDropDown");
        static readonly AccessTools.FieldRef<DropDown,Dictionary<IListableOption,GameObject>> rows=
            AccessTools.FieldRefAccess<DropDown,Dictionary<IListableOption,GameObject>>("rowLookup");
        static void RecordPortrait(SkillsScreen screen,Action<string> log)
        {
            var widget=screen.GetComponentInChildren<FullBodyUIMinionWidget>();
            var anim=widget?.animController;
            var batch=anim?.GetBatch();
            if (batch==null) { log("DUAL_SKILL_PORTRAIT batch=missing"); return; }
            var canvas=anim.GetComponent<KBatchedAnimCanvasRenderer>();
            var mat=canvas?.GetComponent<CanvasRenderer>()?.GetMaterial();
            var controller=anim.GetComponent<SymbolOverrideController>();
            var symbols=controller.GetSymbolOverrides.Where(e=>e.sourceSymbol!=null)
                .GroupBy(e=>e.targetSymbol).Select(g=>g.OrderByDescending(e=>e.priority).First())
                .OrderBy(e=>e.targetSymbol.HashValue)
                .Select(e=>e.targetSymbol+"="+e.sourceSymbol.hash+"@"+e.sourceSymbol.build.fileHash+":"+e.priority);
            var textures=mat==null ? "-" : string.Join(",",mat.GetTexturePropertyNames().Select(n=>
                n+"="+(mat.GetTexture(n)?.name??"-")+"/"+(batch.matProperties.GetTexture(n)?.name??"-")));
            log("DUAL_SKILL_PORTRAIT frame="+anim.GetCurrentFrameIndex()+" batch="+batch.id+" group="+batch.batchGroup+
                " material="+mat?.name+" shader="+mat?.shader?.name+" symbols="+string.Join(",",symbols)+" textures="+textures);
        }
        static string Data(CSteamID lobby,string key) => SteamMatchmaking.GetLobbyData(lobby,"qy_skill_"+key);
        static void Set(CSteamID lobby,string key,string value)
        {
            if (!SteamMatchmaking.SetLobbyData(lobby,"qy_skill_"+key,value)) throw new IOException("Steam rejected skill fixture metadata");
        }

        internal void Prepare(CSteamID lobby,Action<string> log)
        {
            if (BaselineOnly) return;
            var people=Components.LiveMinionIdentities.Items.Where(m=>m!=null && ClusterUtil.GetMyWorldId(m)==ClusterManager.Instance.activeWorldId)
                .OrderByDescending(m=>m.GetComponent<MinionResume>().AvailableSkillpoints).ToList();
            foreach (var person in people)
            {
                var r=person.GetComponent<MinionResume>();
                var candidate=Db.Get().Skills.resources.FirstOrDefault(s=>!s.deprecated && global::Game.IsCorrectDlcActiveForCurrentSave(s) &&
                    (s.requiredDuplicantModel==null || (Tag)s.requiredDuplicantModel==Db.Get().Personalities.Get(person.personalityResourceId).model) &&
                    !string.IsNullOrEmpty(s.hat) && s.hat!=r.TargetHat && !r.HasMasteredSkill(s.Id) && !r.HasBeenGrantedSkill(s) &&
                    r.IsAbleToLearnSkill(s.Id) && r.HasMasteredDirectlyRequiredSkillsForSkill(s));
                if (candidate==null) continue;
                who=person; skill=candidate.Id; hat=candidate.hat;
                bool seeded=r.AvailableSkillpoints==0;
                if (seeded) r.AddExperience(MinionResume.CalculateNextExperienceBar(r.TotalSkillPointsGained)-r.TotalExperienceGained+1);
                if (!r.CanMasterSkill(r.GetSkillMasteryConditions(skill))) throw new InvalidDataException("Fixture selected an ineligible skill");
                initialPoints=r.AvailableSkillpoints;
                Set(lobby,"id",who.GetComponent<KPrefabID>().InstanceID.ToString()); Set(lobby,"skill",skill); Set(lobby,"hat",hat); Set(lobby,"points",initialPoints.ToString());
                log("DUAL_SKILL_SETUP id="+who.GetComponent<KPrefabID>().InstanceID+" skill="+skill+" points="+initialPoints+" seededXP="+seeded);
                return;
            }
            throw new InvalidDataException("No valid base-game skill for any live duplicant");
        }

        bool Resolve(CSteamID lobby)
        {
            if (!int.TryParse(Data(lobby,"id"),out int id) || !int.TryParse(Data(lobby,"points"),out initialPoints)) return false;
            skill=Data(lobby,"skill"); hat=Data(lobby,"hat");
            who=Components.LiveMinionIdentities.Items.FirstOrDefault(d=>d!=null && d.GetComponent<KPrefabID>().InstanceID==id);
            return who!=null && !string.IsNullOrEmpty(skill) && !string.IsNullOrEmpty(hat);
        }

        internal void Baseline(CSteamID lobby,Action<string> log)
        {
            if (BaselineOnly) return;
            if (!Resolve(lobby)) throw new InvalidDataException("Missing selected skill target in the fresh save");
            var r=who.GetComponent<MinionResume>();
            if (!r.HasMasteredSkill(skill) || (r.TargetHat??r.CurrentHat)!=hat) throw new InvalidDataException("Rejoined guest did not inherit mastered skill and hat in the save");
            reloaded=true;
            log("DUAL_GUEST_SKILL_BASELINE mastered=True targetHat=True beforeFirstViewport=True");
        }

        internal bool Verify(CSteamID lobby,int round,bool host,Action<string> log)
        {
            if (BaselineOnly || finishedRound==round) return true;
            if (!Resolve(lobby) || ManagementMenu.Instance==null) return false;
            var resume=who.GetComponent<MinionResume>();
            if (host && (!resume.HasMasteredSkill(skill) || (resume.TargetHat??resume.CurrentHat)!=hat)) return false;
            if (round==2 && !host && !reloaded) throw new InvalidDataException("Missing pre-viewport skill readback");
            if (!opened)
            {
                if (!ManagementMenu.Instance.OpenSkills(who)) return false;
                opened=true;
                return false;
            }
            var screen=panel(ManagementMenu.Instance);
            if (screen==null || !screen.IsScreenActive() || !screen.isActiveAndEnabled) return false;
            if (!host && !NativeManagement.DisplayedProfile(who.GetComponent<KPrefabID>().InstanceID,null,out _)) return false;
            var widget=screen.GetSkillWidget(skill);
            if (widget==null || !widget.gameObject.activeInHierarchy) throw new InvalidDataException("The playable skill widget is absent from native UI");
            if (!host && round==1)
            {
                if (!learnClicked)
                {
                    if (resume.HasMasteredSkill(skill) || resume.AvailableSkillpoints!=initialPoints)
                        throw new InvalidDataException("Unexpected initial skill state");
                    var mouse=new PointerEventData(UnityEngine.EventSystems.EventSystem.current){button=PointerEventData.InputButton.Left};
                    widget.OnPointerClick(mouse);
                    widget.OnPointerClick(mouse);
                    if (resume.HasMasteredSkill(skill)) throw new InvalidDataException("A native skill click applied local mastery speculatively");
                    learnClicked=true;
                    log("DUAL_GUEST_SKILL_CLICK nativeSkillWidget=True localMasteryUnchanged=True repeatedClicks=2");
                    return false;
                }
                if (!resume.HasMasteredSkill(skill)) return false;
                if (!hatClicked)
                {
                    var drop=dropdown(screen);
                    if (!drop.open) { drop.openButton.SignalClick(KKeyCode.Mouse0); return false; }
                    var item=rows(drop).FirstOrDefault(p=>p.Key is HatListable h && h.hat==hat);
                    var entry=item.Value?.GetComponent<DropDownEntry>();
                    if (entry==null || !entry.gameObject.activeInHierarchy) throw new InvalidDataException("Native hat dropdown does not contain the learned profession");
                    entry.button.SignalClick(KKeyCode.Mouse0);
                    if (resume.TargetHat==hat) throw new InvalidDataException("Occupation hat was changed locally before the host reply");
                    hatClicked=true;
                    return false;
                }
            }
            if (!resume.HasMasteredSkill(skill) || (resume.TargetHat??resume.CurrentHat)!=hat) return false;
            if (round==1 && resume.AvailableSkillpoints!=initialPoints-1) throw new InvalidDataException("Learning spent an incorrect number of points");
            if (round==1)
            {
                if (frame==null) frame=new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),host?"dual-host-skills.png":"dual-guest-skills.png"));
                if (!frame.Requested) { RecordPortrait(screen,log); frame.Capture(); return false; }
                if (!frame.Complete) return false;
            }
            log((host?"DUAL_HOST_SKILL":"DUAL_GUEST_SKILL")+" round="+round+" skill="+skill+" mastered=True targetHat="+hat+" points="+resume.AvailableSkillpoints+" nativeScreen=True");
            ManagementMenu.Instance.CloseAll();
            finishedRound=round; opened=false;
            return true;
        }
    }
}
