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
    }

    internal static partial class NativeManagement
    {
        static int inspectedId, inspectedCell;
        static string buildingReadout;
        static CollapsibleDetailContentPanel visibleInfoPanel;

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
                inspectedId = id; inspectedCell = cell; buildingReadout = null;
            }
            if (id <= 0 || !Grid.IsValidCell(cell)) return;
            Grid.CellToXY(cell, out int x, out int y);
            CoopSession.Command("nm/readings/" + id, x, y);
        }

        internal static string ReadBuilding(GameObject target, int id)
        {
            var lines = new List<string>();
            var assignment = target.GetComponent<Assignable>();
            if (assignment != null)
                lines.Add("使用者 " + (assignment.assignee?.GetProperName() ?? "未分配"));
            var priority = target.GetComponent<Prioritizable>();
            if (priority != null && priority.IsPrioritizable())
            {
                var setting = priority.GetMasterPriority();
                lines.Add(setting.priority_class == PriorityScreen.PriorityClass.basic
                    ? $"优先级 {setting.priority_value}" : $"优先级 {setting.priority_class}");
            }
            var op = target.GetComponent<Operational>();
            if (op != null) lines.Add(op.IsActive ? "运行中" : op.IsOperational ? "待机" : "停止运行");
            var selectable = target.GetComponent<KSelectable>();
            var statuses = selectable == null ? null : selectable.GetStatusItemGroup();
            if (statuses != null)
                foreach (var entry in statuses)
                {
                    string name = entry.GetName();
                    if (!string.IsNullOrWhiteSpace(name)) lines.Add(name);
                    if (lines.Count >= 12) break;
                }
            var hp = target.GetComponent<BuildingHP>();
            if (hp != null) lines.Add($"耐久 {hp.HitPoints}/{hp.MaxHitPoints}");
            var battery = target.GetComponent<Battery>();
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
                Convert.ToBase64String(Encoding.UTF8.GetBytes(text.ToString())).Replace('/', '_');
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
            if (fields.Length != 4 || Number(fields[2]) != inspectedId || inspectedId <= 0) return;
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
