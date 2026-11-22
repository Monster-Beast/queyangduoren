using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        // Experience can tick while a player is reading the tree. Only changes that
        // affect a spending decision invalidate the request.
        static string SkillStamp(MinionResume resume)
        {
            return Digest(resume.AvailableSkillpoints+"|"+
                string.Join(",",resume.MasteryBySkillID.Where(p=>p.Value).Select(p=>p.Key).OrderBy(id=>id,StringComparer.Ordinal))+"|"+
                string.Join(",",resume.GrantedSkillIDs.OrderBy(id=>id,StringComparer.Ordinal)));
        }

        static string EditSkill(string[] p)
        {
            if (p.Length!=5 || p[4].Length==0 || p[4].Length>100) return "ERROR 技能请求无效";
            var who=FindDupe(Number(p[2]));
            if (who==null || ClusterUtil.GetMyWorldId(who)!=ClusterManager.Instance.activeWorldId)
                return "ERROR 复制人已不在当前星球";
            var resume=who.GetComponent<MinionResume>();
            if (resume==null) return "ERROR 复制人没有技能记录";
            if (p[1]=="learn")
            {
                var skill=Db.Get().Skills.TryGet(p[4]);
                if (skill==null || skill.Id!=p[4] || skill.deprecated || !global::Game.IsCorrectDlcActiveForCurrentSave(skill) ||
                    (skill.requiredDuplicantModel!=null && (Tag)skill.requiredDuplicantModel!=Db.Get().Personalities.Get(who.personalityResourceId).model))
                    return "ERROR 此复制人无法使用该技能";
                if (resume.HasMasteredSkill(skill.Id) || resume.HasBeenGrantedSkill(skill)) return "ERROR 已掌握该技能";
                if (SkillStamp(resume)!=p[3]) return "ERROR 技能点或已学技能发生变化，请重新选择";
                var conditions=resume.GetSkillMasteryConditions(skill.Id);
                if (!resume.CanMasterSkill(conditions))
                {
                    if (!resume.IsAbleToLearnSkill(skill.Id)) return "ERROR 复制人的特质不允许学习该技能";
                    if (!resume.HasMasteredDirectlyRequiredSkillsForSkill(skill)) return "ERROR 需要先学习前置技能";
                    if (!resume.HasSkillPointsRequiredForSkill(skill)) return "ERROR 可用技能点不足";
                    return "ERROR 未满足原版学习条件";
                }
                resume.MasterSkill(skill.Id);
                if (!resume.HasMasteredSkill(skill.Id)) return "ERROR 房主未能完成技能学习";
            }
            else
            {
                string hat=p[4]=="-" ? null : p[4];
                if (Digest(resume.TargetHat??"")!=p[3]) return "ERROR 职业帽选择已经变化";
                if (hat!=null && !resume.GetAllHats().Any(h=>h.Hat==hat)) return "ERROR 当前无法选择这顶职业帽";
                if ((resume.TargetHat??"")!=(hat??""))
                {
                    // Mirror SkillsScreen.OnHatDropEntryClick, including the delay between
                    // a newly learned profession and owning its physical hat.
                    resume.SetHats(resume.CurrentHat,hat);
                    if (hat==null) resume.ApplyTargetHat();
                    else if (resume.OwnsHat(hat)) new PutOnHatChore(resume,Db.Get().ChoreTypes.SwitchHat);
                }
            }
            OpenSkills()?.RefreshAll();
            return ReadHostProfile(Id(who));
        }

        static MinionResume SelectedSkillResume(SkillsScreen screen)
        {
            var target=screen==null ? null : IdentityObject(skillSelection(screen));
            if (target==null || profileTarget!=target || currentProfile==null)
            {
                NativeGuestFeedback.Show("正在读取当前复制人的技能，请稍后再选。");
                return null;
            }
            return target.GetComponent<MinionResume>();
        }

        [HarmonyPatch(typeof(SkillWidget),"OnPointerClick",new[]{typeof(PointerEventData)})]
        static class GuestLearnSkill
        {
            static bool Prefix(SkillWidget __instance,PointerEventData __0)
            {
                if (!NativeGuestScene.Active) return true;
                if (!NativeGuestScene.Ready || __0?.button!=PointerEventData.InputButton.Left) return false;
                var resume=SelectedSkillResume(OpenSkills());
                if (resume!=null) Send("learn/"+SaveId(resume.gameObject)+"/"+SkillStamp(resume)+"/"+__instance.skillID);
                return false;
            }
        }

        [HarmonyPatch(typeof(SkillsScreen),"OnHatDropEntryClick",new[]{typeof(IListableOption),typeof(object)})]
        static class GuestSelectHat
        {
            static bool Prefix(SkillsScreen __instance,IListableOption __0)
            {
                if (!NativeGuestScene.Active) return true;
                if (!NativeGuestScene.Ready || (__0!=null && !(__0 is HatListable))) return false;
                var resume=SelectedSkillResume(__instance);
                if (resume!=null) Send("hat/"+SaveId(resume.gameObject)+"/"+Digest(resume.TargetHat??"")+"/"+((__0 as HatListable)?.hat??"-"));
                return false;
            }
        }
    }
}
