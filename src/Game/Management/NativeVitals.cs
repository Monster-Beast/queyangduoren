using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;
using Klei.AI;
using Packet = Queyang.Core.Wire;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        internal sealed class Errand
        {
            internal string Name, Target;
            internal int X, Y, Priority;
        }
        internal sealed class Vital
        {
            internal float Value, Min, Max;
            internal string Formatted;
        }
        internal sealed class DupeDetails
        {
            internal string Schedule, Block;
            internal Errand Current;
            internal readonly List<Errand> Candidates = new List<Errand>();
            internal readonly Dictionary<int, Vital> Values = new Dictionary<int, Vital>();
        }

        const int MaxCandidates = 5;
        static readonly AccessTools.FieldRef<ChoreDriver, Chore.Precondition.Context> driverContext =
            AccessTools.FieldRefAccess<ChoreDriver, Chore.Precondition.Context>("context");
        static GameObject selectedIdentity;
        static DupeDetails details;
        static string detailsDigest;
        static readonly Dictionary<AmountInstance, Vital> selectedValues = new Dictionary<AmountInstance, Vital>();
        static int SaveId(GameObject target)
        {
            if (target == null) return -1;
            var marker = target.GetComponent<NativeDupeIdentityMarker>();
            return marker != null ? marker.HostId : target.GetComponent<MinionIdentity>() != null ? target.GetComponent<KPrefabID>().InstanceID : -1;
        }
        static GameObject SelectedDupe()
        {
            var screen = DetailsScreen.Instance;
            return screen != null && screen.IsActive() && SaveId(screen.target) > 0 ? screen.target : null;
        }
        static void ResetVitals()
        {
            selectedIdentity = null;
            selectedValues.Clear();
            details = null;
            detailsDigest = null;
        }
        static void PollVitals()
        {
            var selected = SelectedDupe();
            if (!ReferenceEquals(selected,selectedIdentity)) { ResetVitals(); selectedIdentity = selected; }
            if (selected != null)
            {
                int id=SaveId(selected);
                selected.AddOrGet<NativePersonMenu>().Poll(id);
                Send("vitals/" + id);
            }
        }
        static Amount[] Vitals()
        {
            var a = Db.Get().Amounts;
            return new[] { a.Stamina, a.Calories, a.Breath, a.Stress, a.Bladder, a.Temperature,
                a.HitPoints, a.ImmuneLevel, a.Decor, a.Toxicity, a.RadiationBalance };
        }
        static string DisplayText(string value)
        {
            string text = STRINGS.UI.StripLinkFormatting(value ?? "").Replace('<', '〈').Replace('>', '〉').Replace('\n',' ').Replace('\r',' ');
            if (text.Length <= 72) return text;
            return text.Substring(0, char.IsHighSurrogate(text[71]) ? 71 : 72);
        }
        static Errand CaptureErrand(Chore chore, object data)
        {
            if (chore?.choreType == null || chore.target == null || chore.target is UnityEngine.Object obj && obj == null) return null;
            var target = chore.target.gameObject;
            if (target == null || chore.gameObject == null) return null;
            var cell = Grid.PosToCell(target);
            var e = new Errand { Name = DisplayText(GameUtil.GetChoreName(chore, data)), X = -1, Y = -1, Target = "",
                Priority = chore.masterPriority.priority_class == PriorityScreen.PriorityClass.basic ? chore.masterPriority.priority_value : 0 };
            if (string.IsNullOrEmpty(e.Name)) e.Name = DisplayText(chore.choreType.Name);
            if (Grid.IsValidCell(cell) && Grid.WorldIdx[cell] == ClusterManager.Instance.activeWorldId && Grid.IsVisible(cell))
            {
                e.Target = DisplayText(target.GetProperName());
                Grid.CellToXY(cell, out e.X, out e.Y);
            }
            return e;
        }
        static string ReadHostVitals(int id)
        {
            string header = Prefix + "v/" + id.ToString(CultureInfo.InvariantCulture) + "/";
            var dupe = FindDupe(id);
            if (dupe == null || ClusterUtil.GetMyWorldId(dupe) != ClusterManager.Instance.activeWorldId) return header + "-";
            var s = dupe.GetComponent<Schedulable>()?.GetSchedule();
            var data = new DupeDetails { Schedule = DisplayText(s?.name), Block = DisplayText(s?.GetCurrentScheduleBlock()?.name) };
            var consumer = dupe.GetComponent<ChoreConsumer>();
            var driver = dupe.GetComponent<ChoreDriver>();
            var chore = driver?.GetCurrentChore();
            if (chore != null) data.Current = CaptureErrand(chore, driverContext(driver).data);
            // Use the host's last successful precondition evaluations. Looking at a
            // duplicant must never run its chore selector or change the order it chose.
            var snapshot = consumer?.GetLastPreconditionSnapshot();
            if (snapshot != null)
            {
                foreach (var ctx in snapshot.succeededContexts)
                {
                    if (ctx.chore == chore) continue;
                    var option = CaptureErrand(ctx.chore, ctx.data);
                    if (option != null) data.Candidates.Add(option);
                    if (data.Candidates.Count == MaxCandidates) break;
                }
            }
            var amounts = Vitals();
            for (int i = 0; i < amounts.Length; i++)
            {
                var a = amounts[i].Lookup(dupe);
                if (a == null) continue;
                var v = new Vital { Value = a.value, Min = a.GetMin(), Max = a.GetMax(), Formatted = DisplayText(a.GetValueString()) };
                if (v.Formatted.Length > 40) v.Formatted = v.Formatted.Substring(0, char.IsHighSurrogate(v.Formatted[39]) ? 39 : 40);
                if (Finite(v.Value) && Finite(v.Min) && Finite(v.Max)) data.Values.Add(i,v);
            }
            using (var buffer = new MemoryStream())
            using (var w = new BinaryWriter(buffer))
            {
                w.Write((byte)1);
                Packet.Text(w, data.Schedule, 256); Packet.Text(w, data.Block, 256);
                w.Write(data.Current != null);
                if (data.Current != null) WriteErrand(w, data.Current);
                w.Write((byte)data.Candidates.Count);
                foreach (var candidate in data.Candidates) WriteErrand(w, candidate);
                w.Write((byte)data.Values.Count);
                foreach (var pair in data.Values)
                {
                    w.Write((byte)pair.Key); w.Write(pair.Value.Value); w.Write(pair.Value.Min); w.Write(pair.Value.Max);
                    Packet.Text(w,pair.Value.Formatted,128);
                }
                // At most 14 bounded strings and 11 metric tuples; less than 8 KiB on wire even in UTF-8.
                return header + Convert.ToBase64String(buffer.ToArray()).Replace('/','_');
            }
        }
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static void WriteErrand(BinaryWriter w, Errand e)
        {
            Packet.Text(w, e.Name, 256); Packet.Text(w, e.Target, 256);
            w.Write(e.X); w.Write(e.Y); w.Write(e.Priority);
        }
        static Errand ReadErrand(BinaryReader r)
        {
            var e = new Errand { Name = Packet.Text(r,256), Target = Packet.Text(r,256), X=r.ReadInt32(), Y=r.ReadInt32(), Priority=r.ReadInt32() };
            if ((e.X == -1) != (e.Y == -1) || e.X < -1 || e.Y < -1 || e.X >= Grid.WidthInCells || e.Y >= Grid.HeightInCells ||
                e.Priority < 0 || e.Priority > 9) throw new InvalidDataException("Bad duplicant errand");
            return e;
        }
        static void ApplyVitals(int id, string payload)
        {
            var selected = SelectedDupe();
            if (selected == null || selected != selectedIdentity || SaveId(selected) != id) return;
            if (payload == "-") { ResetVitals(); return; }
            if (payload.Length > 7900) throw new InvalidDataException("Oversized duplicant details");
            var next = new DupeDetails();
            var amounts = Vitals();
            using (var r = new BinaryReader(new MemoryStream(Convert.FromBase64String(payload.Replace('_','/')))))
            {
                if (r.ReadByte() != 1) throw new InvalidDataException("Unknown duplicant details format");
                next.Schedule = Packet.Text(r,256); next.Block = Packet.Text(r,256);
                if (r.ReadBoolean()) next.Current = ReadErrand(r);
                int count = r.ReadByte();
                if (count > MaxCandidates) throw new InvalidDataException("Too many candidate errands");
                for (int i=0; i<count; i++) next.Candidates.Add(ReadErrand(r));
                count = r.ReadByte();
                if (count > amounts.Length) throw new InvalidDataException("Too many duplicant amounts");
                for (int i=0; i<count; i++)
                {
                    int key = r.ReadByte();
                    var v = new Vital { Value=r.ReadSingle(), Min=r.ReadSingle(), Max=r.ReadSingle(), Formatted=Packet.Text(r,128) };
                    if (key >= amounts.Length || next.Values.ContainsKey(key) || !Finite(v.Value) || !Finite(v.Min) || !Finite(v.Max))
                        throw new InvalidDataException("Bad duplicant amount");
                    next.Values.Add(key,v);
                }
                Packet.End(r);
            }
            selectedValues.Clear();
            foreach (var p in next.Values)
            {
                if (selected.GetComponent<NativeDupeIdentityMarker>() != null) break;
                var amount = amounts[p.Key].Lookup(selected);
                if (amount == null) continue;
                // SetValue invokes threshold callbacks. The stock vitals panel reads
                // this frozen scene's raw value and the two getters patched below.
                amount.value = p.Value.Value;
                selectedValues.Add(amount,p.Value);
            }
            details = next;
            detailsDigest = Digest(payload);
            foreach (var panel in DetailsScreen.Instance.GetComponentsInChildren<MinionTodoSideScreen>()) RenderErrands(panel);
        }

        [HarmonyPatch(typeof(AmountInstance), "GetMin")]
        static class SelectedMin
        {
            static bool Prefix(AmountInstance __instance, ref float __result)
            {
                if (!NativeGuestScene.Ready || !selectedValues.TryGetValue(__instance,out var v)) return true;
                __result = v.Min; return false;
            }
        }
        [HarmonyPatch(typeof(AmountInstance), "GetMax")]
        static class SelectedMax
        {
            static bool Prefix(AmountInstance __instance, ref float __result)
            {
                if (!NativeGuestScene.Ready || !selectedValues.TryGetValue(__instance,out var v)) return true;
                __result = v.Max; return false;
            }
        }
        [HarmonyPatch(typeof(MinionTodoSideScreen), "PopulateElements")]
        static class ErrandPanel
        {
            static bool Prefix(MinionTodoSideScreen __instance)
            {
                if (!NativeGuestScene.Ready) return true;
                RenderErrands(__instance);
                return false;
            }
        }
        [HarmonyPatch(typeof(MinionTodoSideScreen), "IsValidForTarget")]
        static class PrintedDupeErrands
        {
            static void Postfix(GameObject __0, ref bool __result)
            {
                if (NativeGuestScene.Ready && __0 != null && __0.GetComponent<NativeDupeIdentityMarker>() != null) __result = true;
            }
        }
        [HarmonyPatch(typeof(DetailsScreen), "OnSelectObject")]
        static class PrintedDupeTab
        {
            static void Postfix(DetailsScreen __instance, object __0)
            {
                if (NativeGuestScene.Ready && __0 is GameObject obj && obj != null && obj.GetComponent<NativeDupeIdentityMarker>() != null)
                    __instance.SelectSideScreenTab(DetailsScreen.SidescreenTabTypes.Errands);
            }
        }
        [HarmonyPatch(typeof(MinionVitalsPanel), "Refresh")]
        static class PrintedDupeVitals
        {
            static bool Prefix(MinionVitalsPanel __instance, GameObject __0)
            {
                if (__instance == profilePanel && (profilePanelTarget != __0 || profileTarget != __0 || currentProfile == null))
                {
                    __instance.Commit();
                    profilePanel = null; profilePanelTarget = null;
                }
                var display = __instance.GetComponent<GuestVitalsDisplay>();
                if (!NativeGuestScene.Ready || __0 == null || __0.GetComponent<NativeDupeIdentityMarker>() == null)
                {
                    if (display != null) display.Restore(__instance);
                    return true;
                }
                if (display == null) display = __instance.gameObject.AddComponent<GuestVitalsDisplay>();
                display.Render(__instance, __0, __0 == selectedIdentity ? details : null, __0 == profileTarget ? currentProfile : null, Vitals());
                return false;
            }
            static void Postfix(MinionVitalsPanel __instance, GameObject __0)
            {
                if (!NativeGuestScene.Ready || __0 == null || __0.GetComponent<MinionIdentity>() == null ||
                    __0 != profileTarget || currentProfile == null) return;
                DrawProfile(__instance, __0);
                __instance.Commit();
                profilePanel = __instance; profilePanelTarget = __0;
            }
        }
        static void RenderErrands(MinionTodoSideScreen screen)
        {
            if (screen.currentTask == null || screen.taskEntryContainer == null) return;
            var display = screen.GetComponent<GuestErrandsDisplay>();
            if (display == null) display = screen.gameObject.AddComponent<GuestErrandsDisplay>();
            display.Render(screen, selectedIdentity != null && selectedIdentity == SelectedDupe() ? details : null);
        }
        internal static bool SelectionDetailsCleared => ReferenceEquals(selectedIdentity,null) && details==null &&
            selectedValues.Count==0 && ReferenceEquals(profileTarget,null) && currentProfile==null;
        internal static bool DisplayedVitals(int id, string expectedDigest, out string summary)
        {
            summary = null;
            if (details == null || selectedIdentity == null || SaveId(selectedIdentity) != id ||
                expectedDigest != null && detailsDigest != expectedDigest || !details.Values.TryGetValue(0,out var stamina)) return false;
            bool projected = selectedIdentity.GetComponent<NativeDupeIdentityMarker>() != null;
            if (projected)
            {
                var display = DetailsScreen.Instance.GetComponentInChildren<GuestVitalsDisplay>();
                if (display == null || !display.Displays(details,stamina.Formatted)) return false;
            }
            else
            {
                var stock = Db.Get().Amounts.Stamina.Lookup(selectedIdentity);
                if (stock == null || stock.value != stamina.Value) return false;
            }
            var panel = DetailsScreen.Instance.GetComponentInChildren<MinionTodoSideScreen>();
            if (panel == null || panel.currentTask.label.text != GuestErrandsDisplay.Task(details.Current) ||
                panel.currentTask.subLabel.text != GuestErrandsDisplay.Target(details.Current)) return false;
            summary = "id=" + id + " task=" + panel.currentTask.label.text + " target=" + panel.currentTask.subLabel.text +
                " candidates=" + details.Candidates.Count + " vitals=" + details.Values.Count + " stamina=" + stamina.Value.ToString("R",CultureInfo.InvariantCulture);
            return true;
        }
    }

    internal sealed class NativeDupeIdentityMarker : MonoBehaviour
    {
        internal int HostId;
    }

    internal sealed class GuestVitalsDisplay : MonoBehaviour
    {
        readonly List<GameObject> original = new List<GameObject>();
        bool visible;
        NativeManagement.DupeDetails last;
        NativeManagement.Profile lastProfile;
        internal bool Displays(NativeManagement.DupeDetails expected, string stamina)
        {
            if (!visible || !ReferenceEquals(last,expected)) return false;
            string text = Db.Get().Amounts.Stamina.Name + ": " + stamina;
            foreach (var label in GetComponentsInChildren<DetailLabel>())
                if (label.label != null && label.label.text == text) return true;
            return false;
        }
        internal bool DisplaysProfile(NativeManagement.Profile expected, out string observation)
        {
            observation="visible="+visible+" profileSet="+(lastProfile!=null);
            if (!visible || !ReferenceEquals(lastProfile,expected)) return false;
            bool skills=false, effects=false, diet=false;
            foreach (var label in GetComponentsInChildren<DetailLabel>())
            {
                string value=Plain(label.label == null ? "" : label.label.text);
                if (label.gameObject.name.StartsWith("Coop") && !label.gameObject.name.StartsWith("CoopVital"))
                    observation+=" "+label.gameObject.name+"="+value;
                if (value == "技能 · " + Plain(expected.SkillLabels.Length>0 ? expected.SkillLabels : "无")) skills=true;
                if (value == "状态效果 · " + Plain(expected.EffectLabels.Length>0 ? expected.EffectLabels : "无")) effects=true;
                if (value == "禁用食物与药物 · " + Plain(expected.RestrictionLabels.Length>0 ? expected.RestrictionLabels : "无")) diet=true;
            }
            observation+=" matches="+skills+"/"+effects+"/"+diet;
            return skills && effects && diet;
        }
        // LocText expands link markup into nested KLink style/bold tags.
        static string Plain(string rich) => Regex.Replace(rich,"<[^>]*>","");
        internal void Restore(MinionVitalsPanel panel)
        {
            if (!visible) return;
            panel.Commit();
            foreach (var child in original) if (child != null) child.SetActive(true);
            original.Clear();
            visible = false;
            last = null;
            lastProfile = null;
        }
        internal void Render(MinionVitalsPanel panel, GameObject target, NativeManagement.DupeDetails data, NativeManagement.Profile profile, Amount[] amounts)
        {
            if (visible && ReferenceEquals(last,data) && ReferenceEquals(lastProfile,profile)) return;
            if (!visible)
            {
                foreach (Transform child in panel.Content)
                    if (child.gameObject.activeSelf) { original.Add(child.gameObject); child.gameObject.SetActive(false); }
            }
            visible = true;
            last = data;
            lastProfile = profile;
            panel.SetActive(true);
            panel.SetTitle(STRINGS.UI.DETAILTABS.SIMPLEINFO.GROUPNAME_CONDITION);
            if (data == null) panel.SetLabel("CoopWaiting","正在读取房主生理状态…","");
            else foreach (var v in data.Values)
                panel.SetLabel("CoopVital" + v.Key, amounts[v.Key].Name + ": " + v.Value.Formatted,"");
            NativeManagement.DrawProfile(panel,target);
            panel.Commit();
        }
    }

    internal sealed class GuestErrandsDisplay : MonoBehaviour
    {
        readonly List<MinionTodoChoreEntry> candidates = new List<MinionTodoChoreEntry>();
        internal static string Task(NativeManagement.Errand e) => e == null ? "暂无当前差事" : e.Name;
        internal static string Target(NativeManagement.Errand e) => e == null || e.X < 0 ? "" : e.Target + " (" + e.X + ", " + e.Y + ")";
        bool rendered;
        NativeManagement.DupeDetails last;
        internal void Render(MinionTodoSideScreen screen, NativeManagement.DupeDetails data)
        {
            if (rendered && ReferenceEquals(last,data)) return;
            if (!rendered)
                foreach (Transform child in screen.taskEntryContainer.transform)
                    if (!screen.currentTask.transform.IsChildOf(child) && child.GetComponent<HierarchyReferences>() != null)
                        child.gameObject.SetActive(false);
            rendered = true;
            last = data;
            if (screen.currentShiftLabel != null) screen.currentShiftLabel.SetText(data == null ? "正在读取房主日程…" : data.Schedule);
            if (screen.currentScheduleBlockLabel != null) screen.currentScheduleBlockLabel.SetText(data?.Block ?? "");
            Draw(screen.currentTask, data?.Current, data == null ? "正在读取房主差事…" : Task(data.Current));
            int count = data?.Candidates.Count ?? 0;
            while (candidates.Count < count)
                candidates.Add(Instantiate(screen.taskEntryPrefab, screen.taskEntryContainer.transform, false));
            for (int i=0; i<candidates.Count; i++)
            {
                var row = candidates[i];
                row.gameObject.SetActive(i < count);
                if (i < count) Draw(row, data.Candidates[i], Task(data.Candidates[i]));
            }
        }
        static void Draw(MinionTodoChoreEntry row, NativeManagement.Errand e, string name)
        {
            row.label.SetText(name);
            row.subLabel.SetText(Target(e));
            if (row.priorityLabel != null) row.priorityLabel.SetText(e != null && e.Priority > 0 ? e.Priority.ToString() : "");
            if (row.moreLabel != null) row.moreLabel.SetText("");
            if (row.icon != null) row.icon.enabled = false;
            if (row.priorityIcon != null) row.priorityIcon.enabled = false;
            var button = row.GetComponent<KButton>();
            if (button != null) button.isInteractable = false;
        }
    }
}
