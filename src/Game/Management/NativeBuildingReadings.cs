using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal sealed class NativeBuildingIdentity : MonoBehaviour
    {
        internal int Cell, SaveId;
        internal bool Complete;
    }

    internal static partial class NativeManagement
    {
        static int inspectedId, inspectedCell;
        static string buildingReadout;
        static CollapsibleDetailContentPanel visibleInfoPanel;
        static CollapsibleDetailContentPanel statusPanel;
        static GameObject statusTarget;
        static string statusReadout;

        static bool MatchesStatusTarget() => statusTarget!=null && inspectedId>0 &&
            NativeWorldMirror.GetBuildingSelectable(inspectedCell,inspectedId)?.gameObject==statusTarget;

        static void DrawStatuses(CollapsibleDetailContentPanel panel)
        {
            int count=0;
            if(!string.IsNullOrEmpty(statusReadout))
                foreach(string line in statusReadout.Split('\n'))
                    panel.SetLabel("CoopStatus"+count++,line,"");
            panel.Commit();
            panel.gameObject.SetActive(count>0);
        }

        [HarmonyPatch(typeof(SimpleInfoScreen),"OnSelectTarget")]
        static class SelectStatusTarget
        {
            static void Prefix(GameObject __0, CollapsibleDetailContentPanel ___statusItemPanel)
            {
                if(!NativeGuestScene.Ready || !CoopSession.IsNativeClient || __0==null ||
                    (__0.GetComponent<Building>()==null && __0.GetComponent<NativeBuildingIdentity>()==null)) return;
                statusTarget=__0; statusPanel=___statusItemPanel;
            }
        }

        [HarmonyPatch(typeof(SimpleInfoScreen),"DoAddStatusItem")]
        static class AddOriginalStatus
        {
            static bool Prefix() => !NativeGuestScene.Ready || !CoopSession.IsNativeClient || statusTarget==null;
        }

        [HarmonyPatch(typeof(SimpleInfoScreen),"OnDeselectTarget")]
        static class DeselectStatusTarget
        {
            static void Prefix()
            {
                if(statusPanel!=null) statusPanel.Commit();
                statusTarget=null; statusPanel=null;
            }
        }

        [HarmonyPatch(typeof(SimpleInfoScreen),"Refresh")]
        static class RefreshStatusTarget
        {
            static void Postfix()
            {
                if(statusPanel!=null && MatchesStatusTarget()) DrawStatuses(statusPanel);
            }
        }

        static void PollBuildingReadings(KSelectable selected)
        {
            int id = 0, cell = Grid.InvalidCell;
            if (selected != null)
            {
                var original = selected.GetComponent<Building>();
                var replica = selected.GetComponent<NativeBuildingIdentity>();
                if (replica != null) { id = replica.SaveId; cell = replica.Cell; }
                else if (original != null) { id = Id(original); cell = original.GetCell(); }
            }
            if (id != inspectedId || cell != inspectedCell)
            {
                inspectedId = id; inspectedCell = cell; buildingReadout = null; ResetStoragePanel();
            }
            if (id <= 0 || !Grid.IsValidCell(cell)) return;
            Grid.CellToXY(cell, out int x, out int y);
            CoopSession.Command("nm/readings/" + id + (nextStoragePage==0?"":"/"+nextStoragePage), x, y);
        }

        internal static string ReadBuilding(GameObject target, int id,int page=0)
        {
            var lines = new List<string>();
            NativeBuildingWork.AddSelectedReadings(target,lines);
            var assignment = target.GetComponent<Assignable>();
            if (assignment != null)
                lines.Add("使用者 " + (assignment.assignee?.GetProperName() ?? "未分配"));
            var priority = target.GetComponent<Prioritizable>();
            var research = target.GetComponent<ResearchCenter>();
            if (research!=null) lines.Add(ResearchBuildingReading(research));
            if (priority != null && priority.IsPrioritizable())
            {
                var setting = priority.GetMasterPriority();
                lines.Add(setting.priority_class == PriorityScreen.PriorityClass.basic
                    ? $"优先级 {setting.priority_value}" : $"优先级 {setting.priority_class}");
            }
            var op = target.GetComponent<Operational>();
            if (op != null) lines.Add(op.IsActive ? "运行中" : op.IsOperational ? "待机" : "停止运行");
            var hp = target.GetComponent<BuildingHP>();
            if (hp != null) lines.Add($"耐久 {hp.HitPoints}/{hp.MaxHitPoints}");
            var battery = target.GetComponent<Battery>();
            var store=target.GetComponent<Storage>();
            if(store!=null && store.allowSettingOnlyFetchMarkedItems)
                lines.Add(store.GetOnlyFetchMarkedItems()?"仅限清扫：开启":"仅限清扫：关闭");
            if (battery != null) lines.Add(FormattableString.Invariant($"储能 {battery.JoulesAvailable:0}/{battery.Capacity:0} J"));
            var fabricator = target.GetComponent<ComplexFabricator>();
            if (fabricator != null && fabricator.CurrentWorkingOrder != null)
                lines.Add(FormattableString.Invariant($"配方 {fabricator.CurrentWorkingOrder.id} · {fabricator.OrderProgress:P0}"));
            var door = target.GetComponent<Door>();
            if (door != null) lines.Add($"门 {door.CurrentState} → {door.RequestedState}");
            var valve = target.GetComponent<Valve>();
            if (valve != null) lines.Add(FormattableString.Invariant($"阀门 {valve.GetComponent<ValveBase>().CurrentFlow:0.###} kg/s · 目标 {valve.DesiredFlow:0.###} · 上限 {valve.MaxFlow:0.###}"));
            var building = target.GetComponent<Building>();
            if (building != null)
            {
                AddPort(lines, "输入", building.Def.InputConduitType, building.GetUtilityInputCell());
                AddPort(lines, "输出", building.Def.OutputConduitType, building.GetUtilityOutputCell());
            }
            var sensor = target.GetComponent<IThresholdSwitch>();
            if (sensor != null) lines.Add($"传感器 {sensor.Format(sensor.CurrentValue, true)} · {(sensor.ActivateAboveThreshold ? "高于" : "低于")} {sensor.Format(sensor.Threshold, true)}");
            var room = global::Game.Instance.roomProber?.GetRoomOfGameObject(target);
            if (room?.cavity != null) lines.Add($"房间 {room.GetProperName()} · {room.cavity.NumCells} 格");
            var text = new StringBuilder();
            foreach (string line in lines)
            {
                if (text.Length + line.Length > 900) break;
                if (text.Length != 0) text.Append('\n');
                text.Append(line);
            }
            return Prefix + "g/" + id.ToString(CultureInfo.InvariantCulture) + "/" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(text.ToString())).Replace('/', '_') +
                "/" + (NativeFacilityStorage.Supported(target)?NativeFacilityStorage.Encode(target,page):"-") +
                "/" + EncodeStatuses(target);
        }

        internal static string ResearchBuildingReading(ResearchCenter station)
        {
            var research=Research.Instance;
            var active=research?.GetActiveResearch();
            if(active==null) return STRINGS.RESEARCH.MESSAGING.NORESEARCHSELECTED;
            string type=station.GetResearchType();
            var points=active.progressInventory.PointsByTypeID;
            var text=new StringBuilder(active.tech.Name);
            if(!active.tech.costsByResearchTypeID.TryGetValue(type,out float cost) || cost<=0)
                return text.Append("\n此科技不需要本研究站的点数").ToString();
            points.TryGetValue(type,out float earned);
            text.Append("\n").Append(research.GetResearchType(type).name).Append(" ")
                .Append(earned.ToString("0.##",CultureInfo.InvariantCulture)).Append("/")
                .Append(cost.ToString("0.##",CultureInfo.InvariantCulture));
            return text.ToString();
        }

        static string EncodeStatuses(GameObject target)
        {
            var names=new StringBuilder();
            int used=0;
            var group=target.GetComponent<KSelectable>()?.GetStatusItemGroup();
            if(group!=null)
                foreach(var entry in group)
                {
                    if(entry.item.showInHoverCardOnly) continue;
                    string name=entry.GetName();
                    if(string.IsNullOrWhiteSpace(name)) continue;
                    int bytes=Encoding.UTF8.GetByteCount(name)+1;
                    if(used+bytes>1400) continue;
                    if(names.Length>0) names.Append('\n');
                    names.Append(name); used+=bytes;
                }
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(names.ToString())).Replace('/','_');
        }

        static void AddPort(List<string> lines, string label, ConduitType type, int cell)
        {
            if ((type != ConduitType.Liquid && type != ConduitType.Gas) || !Grid.IsValidCell(cell)) return;
            Grid.CellToXY(cell, out int x, out int y);
            var flow = Conduit.GetFlowManager(type);
            if (!flow.HasConduit(cell)) { lines.Add($"{label} ({x},{y}) · 未接管"); return; }
            var data = flow.GetContents(cell);
            string material = data.mass > 0 ? ElementLoader.FindElementByHash(data.element)?.name : "空";
            lines.Add(FormattableString.Invariant($"{label} ({x},{y}) · {material} {data.mass:0.###} kg"));
        }

        static void ReceiveReadings(string[] fields)
        {
            if (fields.Length<4 || fields.Length>6 || Number(fields[2])!=inspectedId || inspectedId<=0) return;
            if(fields.Length>=5 && fields[4]!="-") ReceiveStoragePanel(fields[4]);
            if(fields.Length==6)
            {
                statusReadout=Encoding.UTF8.GetString(Convert.FromBase64String(fields[5].Replace('_','/')));
                if(statusPanel!=null && MatchesStatusTarget()) DrawStatuses(statusPanel);
            }
            string text = Encoding.UTF8.GetString(Convert.FromBase64String(fields[3].Replace('_', '/')));
            if (text == buildingReadout) return;
            buildingReadout = text;
            var selected = NativeWorldMirror.GetBuildingSelectable(inspectedCell, inspectedId);
            if (visibleInfoPanel != null && selected != null && DetailsScreen.Instance?.target == selected.gameObject)
                RefreshInfo(visibleInfoPanel, selected.gameObject);
        }

        static readonly Action<CollapsibleDetailContentPanel, GameObject> RefreshInfo =
            AccessTools.MethodDelegate<Action<CollapsibleDetailContentPanel, GameObject>>(
                AccessTools.DeclaredMethod(typeof(SimpleInfoScreen), "RefreshInfoPanel"));

        [HarmonyPatch(typeof(SimpleInfoScreen), "RefreshInfoPanel")]
        static class BuildingInfo
        {
            // Preserve the normal building description alongside the separate live reading.
            static void Prefix(CollapsibleDetailContentPanel __0, GameObject __1)
            {
                if (NativeGuestScene.Ready) visibleInfoPanel = __0;
                if (!NativeGuestScene.Ready || inspectedId <= 0 || string.IsNullOrEmpty(buildingReadout)) return;
                var selected = NativeWorldMirror.GetBuildingSelectable(inspectedCell, inspectedId);
                if (selected != null && selected.gameObject == __1)
                    __0.SetLabel("QueyangBuildingReading", buildingReadout, "来自房主的建筑状态");
            }
        }
    }
}
