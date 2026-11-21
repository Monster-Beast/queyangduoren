using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using Klei.AI;
using UnityEngine;
using Packet = Queyang.Core.Wire;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static readonly AccessTools.FieldRef<ManagementMenu, SkillsScreen> skillsPanel =
            AccessTools.FieldRefAccess<ManagementMenu, SkillsScreen>("skillsScreen");
        static readonly AccessTools.FieldRef<SkillsScreen, IAssignableIdentity> skillSelection =
            AccessTools.FieldRefAccess<SkillsScreen, IAssignableIdentity>("currentlySelectedMinion");
        static readonly AccessTools.FieldRef<MinionResume,float> experience =
            AccessTools.FieldRefAccess<MinionResume,float>("totalExperienceGained");
        static readonly AccessTools.FieldRef<MinionResume,string> currentHat = AccessTools.FieldRefAccess<MinionResume,string>("currentHat");
        static readonly AccessTools.FieldRef<MinionResume,string> targetHat = AccessTools.FieldRefAccess<MinionResume,string>("targetHat");
        static readonly AccessTools.FieldRef<Effects,List<EffectInstance>> effectList = AccessTools.FieldRefAccess<Effects,List<EffectInstance>>("effects");
        static GameObject profileTarget, hoveredDupe;
        static MinionVitalsPanel profilePanel;
        static GameObject profilePanelTarget;
        static float hoverExpires, nextProfile;
        static string profileDigest;
        static Profile currentProfile;
        internal sealed class Profile
        {
            internal float XP;
            internal string Hat, NextHat;
            internal string SkillLabels, EffectLabels, RestrictionLabels;
            internal int[] Learned, Granted, Forbidden, Diet;
            internal readonly Dictionary<int,float> Effects = new Dictionary<int,float>();
        }

        static GameObject IdentityObject(IAssignableIdentity identity)
        {
            if (identity is MinionAssignablesProxy proxy && proxy != null) return proxy.GetTargetGameObject();
            if (identity is MinionIdentity dupe && dupe != null) return dupe.gameObject;
            return null;
        }
        static SkillsScreen OpenSkills()
        {
            if (ManagementMenu.Instance == null) return null;
            var menu = skillsPanel(ManagementMenu.Instance);
            return menu != null && menu.isActiveAndEnabled && menu.IsScreenActive() ? menu : null;
        }
        static GameObject ProfileSelection()
        {
            var skills = OpenSkills();
            if (skills != null) return IdentityObject(skillSelection(skills));
            if (hoveredDupe != null && Time.unscaledTime < hoverExpires) return hoveredDupe;
            return SelectedDupe();
        }
        static void PollProfile()
        {
            var who = ProfileSelection();
            if (!ReferenceEquals(profileTarget,who)) { profileDigest = null; currentProfile = null; nextProfile = 0f; profileTarget = who; }
            if (who == null || Time.unscaledTime < nextProfile) return;
            nextProfile = Time.unscaledTime + 1.4f;
            int id = SaveId(who);
            if (id > 0) Send("profile/" + id);
        }
        static void ResetProfile()
        {
            profileTarget = hoveredDupe = null;
            hoverExpires = nextProfile = 0;
            currentProfile = null; profileDigest = null;
            profilePanel = null; profilePanelTarget = null;
        }
        static void WriteIds(BinaryWriter w, IEnumerable<int> values, int max)
        {
            int[] ids = values.Distinct().OrderBy(i=>i).Take(max+1).ToArray();
            if (ids.Length > max) throw new InvalidDataException("Duplicant profile exceeds collection limit");
            w.Write((ushort)ids.Length);
            foreach (int id in ids) w.Write(id);
        }
        static int[] ReadIds(BinaryReader r, int max)
        {
            int n = r.ReadUInt16();
            if (n > max) throw new InvalidDataException("Invalid duplicant profile collection");
            var a = new int[n];
            for (int i=0; i<n; i++)
            {
                a[i] = r.ReadInt32();
                if (i != 0 && a[i] <= a[i-1]) throw new InvalidDataException("Duplicate or unordered profile ID");
            }
            return a;
        }
        static string ReadHostProfile(int id)
        {
            string prefix = Prefix + "pf/" + id.ToString(CultureInfo.InvariantCulture) + "/";
            var d = FindDupe(id);
            if (d == null || ClusterUtil.GetMyWorldId(d) != ClusterManager.Instance.activeWorldId) return prefix + "-";
            var resume = d.GetComponent<MinionResume>();
            var consumer = d.GetComponent<ConsumableConsumer>();
            var effects = d.GetComponent<Effects>();
            if (resume == null || consumer == null || effects == null) return prefix + "-";
            if (resume.MasteryBySkillID.Count(p=>p.Value) > 128 || resume.GrantedSkillIDs.Count > 128 ||
                consumer.forbiddenTagSet.Count > 256 || consumer.dietaryRestrictionTagSet.Count > 256) return prefix + "-";
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                w.Write((byte)2);
                w.Write(resume.TotalExperienceGained);
                Packet.Text(w,resume.CurrentHat ?? "",160); Packet.Text(w,resume.TargetHat ?? "",160);
                WriteIds(w,resume.MasteryBySkillID.Where(p=>p.Value).Select(p=>new HashedString(p.Key).HashValue),128);
                WriteIds(w,resume.GrantedSkillIDs.Select(k=>new HashedString(k).HashValue),128);
                WriteIds(w,consumer.forbiddenTagSet.Select(t=>t.GetHash()),256);
                WriteIds(w,consumer.dietaryRestrictionTagSet.Select(t=>t.GetHash()),256);
                var active = effectList(effects);
                if (active.Count > 96) return prefix + "-";
                w.Write((byte)active.Count);
                // Some runtime-created effects are absent from Db.effects. Carry the
                // actual host label while reserving room for every remaining record.
                int labelBudget = active.Count == 0 ? 0 : (5800 - (int)s.Position) / active.Count - 10;
                foreach (var e in active)
                {
                    w.Write(e.effect.IdHash.HashValue);
                    w.Write(e.effect.duration > 0 ? Mathf.Max(0,e.timeRemaining) : -1f);
                    var name = DisplayText(string.IsNullOrEmpty(e.effect.Name) ? e.effect.Id : e.effect.Name);
                    while (Encoding.UTF8.GetByteCount(name)>labelBudget && name.Length>0)
                        name=name.Substring(0,name.Length-(char.IsLowSurrogate(name[name.Length-1])?2:1));
                    Packet.Text(w,name,256);
                }
                return prefix + Convert.ToBase64String(s.ToArray()).Replace('/','_');
            }
        }
        static void ApplyProfile(int id,string payload)
        {
            var selected = ProfileSelection();
            if (profileTarget != selected || selected == null || SaveId(selected) != id) return;
            if (payload == "-") { currentProfile = null; profileDigest = null; return; }
            if (payload.Length > 7900) throw new InvalidDataException("Oversized duplicant profile");
            var hash = Digest(payload);
            if (hash == profileDigest) return;
            var p = new Profile();
            var effectLabels = new List<string>();
            using (var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(payload.Replace('_','/')))))
            {
                int version = r.ReadByte();
                if (version < 1 || version > 2) throw new InvalidDataException("Unknown duplicant profile version");
                p.XP = r.ReadSingle(); p.Hat = Packet.Text(r,160); p.NextHat = Packet.Text(r,160);
                if (!Finite(p.XP)) throw new InvalidDataException("Invalid duplicant experience");
                p.Learned=ReadIds(r,128); p.Granted=ReadIds(r,128); p.Forbidden=ReadIds(r,256); p.Diet=ReadIds(r,256);
                int count = r.ReadByte();
                if (count > 96) throw new InvalidDataException("Too many active duplicant effects");
                for (int i=0; i<count; i++)
                {
                    int key = r.ReadInt32(); float time = r.ReadSingle();
                    if (!Finite(time) || time < -1 || p.Effects.ContainsKey(key)) throw new InvalidDataException("Invalid effect state");
                    p.Effects.Add(key,time);
                    string name = version == 2 ? Packet.Text(r,256) : Db.Get().effects.TryGet(new HashedString(key))?.Name;
                    effectLabels.Add((string.IsNullOrEmpty(name) ? "#" + key.ToString("X8") : name) +
                        (time >= 0 ? " (" + Mathf.CeilToInt(time) + " s)" : ""));
                }
                Packet.End(r);
            }
            p.SkillLabels = string.Join("、",p.Learned.Concat(p.Granted).Distinct().Select(i => Db.Get().Skills.TryGet(new HashedString(i))?.Name).Where(s=>s!=null));
            p.EffectLabels = string.Join("、",effectLabels);
            p.RestrictionLabels = string.Join("、",p.Forbidden.Concat(p.Diet).Distinct().Select(i => {
                string label=new Tag(i).ProperName();
                return string.IsNullOrEmpty(label) ? "#" + i.ToString("X8") : label;
            }));
            var resume = selected.GetComponent<MinionResume>();
            if (resume != null)
            {
                // MasterSkill applies perks and signals achievements. Only the fields
                // inspected by the stock skill tree should change on the replica.
                resume.MasteryBySkillID.Clear(); resume.GrantedSkillIDs.Clear();
                foreach (var skill in Db.Get().Skills.resources)
                {
                    int key = new HashedString(skill.Id).HashValue;
                    if (Array.BinarySearch(p.Learned,key)>=0) resume.MasteryBySkillID[skill.Id]=true;
                    if (Array.BinarySearch(p.Granted,key)>=0) resume.GrantedSkillIDs.Add(skill.Id);
                }
                experience(resume) = p.XP;
                currentHat(resume) = string.IsNullOrEmpty(p.Hat) ? null : p.Hat;
                targetHat(resume) = string.IsNullOrEmpty(p.NextHat) ? null : p.NextHat;
            }
            var c = selected.GetComponent<ConsumableConsumer>();
            if (c != null)
            {
                var forbidden = p.Forbidden.Select(i=>new Tag(i)).ToArray();
                var diet = p.Diet.Select(i=>new Tag(i)).ToArray();
                if (!c.forbiddenTagSet.SetEquals(forbidden) || !c.dietaryRestrictionTagSet.SetEquals(diet))
                {
                    c.forbiddenTagSet.Clear(); c.dietaryRestrictionTagSet.Clear();
                    c.forbiddenTagSet.UnionWith(forbidden); c.dietaryRestrictionTagSet.UnionWith(diet);
                    c.consumableRulesChanged?.Invoke();
                }
            }
            bool skillsChanged = currentProfile == null || p.XP != currentProfile.XP || p.Hat != currentProfile.Hat || p.NextHat != currentProfile.NextHat ||
                !p.Learned.SequenceEqual(currentProfile.Learned) || !p.Granted.SequenceEqual(currentProfile.Granted);
            currentProfile = p; profileDigest = hash;
            if (skillsChanged) OpenSkills()?.RefreshAll();
        }

        internal static bool DisplayedProfile(int id,string expected,out string summary)
        {
            summary = null;
            if (currentProfile == null || SaveId(profileTarget) != id || expected != null && profileDigest != expected)
            { summary="profileLoaded="+(currentProfile!=null)+" target="+SaveId(profileTarget)+" digest="+profileDigest; return false; }
            var p = currentProfile;
            var resume = profileTarget.GetComponent<MinionResume>();
            var c = profileTarget.GetComponent<ConsumableConsumer>();
            if (profileTarget.GetComponent<NativeDupeIdentityMarker>() != null)
            {
                var panel=DetailsScreen.Instance.GetComponentInChildren<GuestVitalsDisplay>();
                if (panel == null || !panel.DisplaysProfile(p,out summary)) return false;
            }
            if (resume != null && (resume.TotalExperienceGained != p.XP || resume.SkillsMastered != p.Learned.Length)) return false;
            if (c != null && (c.forbiddenTagSet.Count != p.Forbidden.Length || c.dietaryRestrictionTagSet.Count != p.Diet.Length)) return false;
            summary = "skills=" + p.Learned.Length + " effects=" + p.Effects.Count + " forbidden=" + p.Forbidden.Length;
            return true;
        }
        internal static void DrawProfile(MinionVitalsPanel panel, GameObject target)
        {
            if (target != null && target == profileTarget && currentProfile != null)
            {
                var p = currentProfile;
                panel.SetLabel("CoopSkills", "技能 · " + (p.SkillLabels.Length != 0 ? p.SkillLabels : "无"), "");
                panel.SetLabel("CoopEffects","状态效果 · " + (p.EffectLabels.Length != 0 ? p.EffectLabels : "无"), "");
                panel.SetLabel("CoopDiet","禁用食物与药物 · " + (p.RestrictionLabels.Length != 0 ? p.RestrictionLabels : "无"), "");
            }
        }
        [HarmonyPatch(typeof(ConsumablesTableScreen), "on_tooltip_consumable_info")]
        static class HoverConsumable
        {
            static void Postfix(IAssignableIdentity __0)
            {
                if (!NativeGuestScene.Ready) return;
                hoveredDupe = IdentityObject(__0); hoverExpires = Time.unscaledTime + 2f;
            }
        }
        [HarmonyPatch(typeof(ConsumableConsumer), "SetPermitted")]
        static class ConsumableRules
        {
            static bool Prefix()
            {
                if (!NativeGuestScene.Ready) return true;
                NativeGuestFeedback.Show("食物和药物许可由房主设置。"); return false;
            }
        }
    }
}
