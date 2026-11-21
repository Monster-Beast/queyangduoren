using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Queyang.Game
{
    public static partial class HostWorld
    {
        private static void ReadConnections(ref ColonyCell result, int cell)
        {
            var game = global::Game.Instance;
            ReadLayer(ref result, cell, ObjectLayer.WireTile, ObjectLayer.Wire, 0, game.electricalConduitSystem);
            ReadLayer(ref result, cell, ObjectLayer.GasConduitTile, ObjectLayer.GasConduit, 1, game.gasConduitSystem);
            ReadLayer(ref result, cell, ObjectLayer.LiquidConduitTile, ObjectLayer.LiquidConduit, 2, game.liquidConduitSystem);
            ReadLayer(ref result, cell, ObjectLayer.LogicWireTile, ObjectLayer.LogicWire, 3, game.logicCircuitSystem);
            if ((result.Utilities & 8) != 0 &&
                game.logicCircuitSystem.GetNetworkForCell(cell) is LogicCircuitNetwork signal &&
                signal.OutputValue != 0) result.Utilities |= 16;
        }

        private static void ReadLayer(ref ColonyCell result, int cell, ObjectLayer tile,
            ObjectLayer layer, int index, IUtilityNetworkMgr manager)
        {
            if (Grid.Objects[cell, (int)tile] == null && Grid.Objects[cell, (int)layer] == null) return;
            result.Utilities |= (byte)(1 << index);
            result.Connections |= (ushort)(((int)manager.GetDisplayConnections(cell) & 15) << (index * 4));
        }

        private static string BuildUtilityLine(string action, int x, int y, WorldContainer world)
        {
            int at = action.IndexOf('@');
            var start = action.Substring(at + 1).Split(',');
            int startX, startY, startCell;
            if (start.Length != 2 ||
                !int.TryParse(start[0], NumberStyles.None, CultureInfo.InvariantCulture, out startX) ||
                !int.TryParse(start[1], NumberStyles.None, CultureInfo.InvariantCulture, out startY) ||
                !VisibleCell(world, startX, startY, out startCell) || (x != startX && y != startY))
                return "ERROR Select two cells on the same row or column";
            int dx = x - startX, dy = y - startY;
            int count = 1 + Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (count > 24) return "ERROR Maximum line length is 24";
            string prefab;
            IUtilityNetworkMgr manager;
            var game = global::Game.Instance;
            switch (action.Substring(0, at))
            {
                case "wire": prefab = WireConfig.ID; manager = game.electricalConduitSystem; break;
                case "gaspipe": prefab = GasConduitConfig.ID; manager = game.gasConduitSystem; break;
                case "liquidpipe": prefab = LiquidConduitConfig.ID; manager = game.liquidConduitSystem; break;
                default: return "ERROR Unsupported network";
            }
            var def = Assets.GetBuildingDef(prefab);
            if (def == null || manager == null) return "ERROR Network unavailable";
            var cells = new List<int>(count);
            // Check the entire route's visibility first. An illegal path must not reveal the hidden section.
            for (int i = 0; i < count; i++)
            {
                int cell;
                if (!VisibleCell(world, startX + i * Math.Sign(dx), startY + i * Math.Sign(dy), out cell) ||
                    !Grid.IsValidBuildingCell(cell)) return "ERROR Route unavailable";
                cells.Add(cell);
            }
            KAnimGraphTileVisualizer previous = null;
            for (int i = 0; i < count; i++)
            {
                int cell = cells[i];
                var obj = Grid.Objects[cell, (int)def.TileLayer];
                if (obj == null)
                {
                    string result = Place(prefab, cell, world, out obj);
                    if (obj == null) return PartialLine(i, count, result);
                }
                var item = obj.GetComponent<KAnimGraphTileVisualizer>();
                if (item == null || item.ConnectionManager != manager)
                    return PartialLine(i, count, "different utility at cell");
                if (previous != null)
                {
                    int prev = cells[i - 1];
                    var forward = UtilityConnectionsExtensions.DirectionFromToCell(prev, cell);
                    var reverse = UtilityConnectionsExtensions.InverseDirection(forward);
                    string reason;
                    if (!manager.CanAddConnection(forward, prev, false, out reason) ||
                        !manager.CanAddConnection(reverse, cell, false, out reason))
                        return PartialLine(i, count, "connection refused");
                    Connect(manager, previous, prev, forward, def);
                    Connect(manager, item, cell, reverse, def);
                }
                previous = item;
            }
            return "OK Utility route accepted (" + count + " cells)";
        }

        private static string PartialLine(int accepted, int length, string reason) =>
            (accepted == 0 ? "ERROR " : "PARTIAL ") + accepted + "/" + length + " connected: " + reason;

        private static void Connect(IUtilityNetworkMgr manager, KAnimGraphTileVisualizer item,
            int cell, UtilityConnections dir, BuildingDef def)
        {
            manager.AddConnection(dir, cell, false);
            var desired = item.Connections | dir;
            if (item.GetComponent<BuildingComplete>() != null) item.UpdateConnections(desired);
            item.Connections = desired;
            TileVisualizer.RefreshCell(cell, def.TileLayer, def.ReplacementLayer);
        }

        private static string Inspect(int cell)
        {
            var report = new StringBuilder("OK ");
            var game = global::Game.Instance;
            report.AppendFormat(CultureInfo.InvariantCulture, "{0} {1:0.##} kg / {2:0.#} K", Grid.Element[cell].id,
                Grid.Mass[cell], Grid.Temperature[cell]);
            AppendPipe(report, "Gas", cell, game.gasConduitFlow, game.gasConduitSystem);
            AppendPipe(report, "Liquid", cell, game.liquidConduitFlow, game.liquidConduitSystem);
            if (Grid.Objects[cell, (int)ObjectLayer.Wire] != null ||
                Grid.Objects[cell, (int)ObjectLayer.WireTile] != null)
            {
                var id = game.circuitManager.GetCircuitID(cell);
                report.Append(" | Wire ").Append(game.electricalConduitSystem.GetDisplayConnections(cell));
                if (id != CircuitManager.INVALID_ID)
                    report.AppendFormat(CultureInfo.InvariantCulture, " {0:0} W load / {1:0} W generation",
                        game.circuitManager.GetWattsUsedByCircuit(id), game.circuitManager.GetWattsGeneratedByCircuit(id));
            }
            var signal = game.logicCircuitSystem.GetNetworkForCell(cell) as LogicCircuitNetwork;
            if (signal != null) report.Append(" | Logic ").Append(signal.OutputValue);
            var target = GetTarget(cell);
            if (target != null)
            {
                var priority = target.GetComponent<Prioritizable>();
                if (priority != null && priority.IsPrioritizable())
                    report.Append(" | Priority ").Append(priority.GetMasterPriority().priority_value);
                var op = target.GetComponent<Operational>();
                if (op != null) report.Append(op.IsActive ? " | Active" : op.IsOperational ? " | Idle" : " | Inoperable");
            }
            return report.ToString();
        }

        private static void AppendPipe(StringBuilder report, string label, int cell, ConduitFlow flow, IUtilityNetworkMgr manager)
        {
            if (!flow.HasConduit(cell)) return;
            var contents = flow.GetContents(cell);
            report.Append(" | ").Append(label).Append(" pipe ").Append(manager.GetDisplayConnections(cell));
            if (contents.mass > 0)
                report.AppendFormat(CultureInfo.InvariantCulture, " {0} {1:0.###} kg / {2:0.#} K",
                    contents.element, contents.mass, contents.temperature);
            else report.Append(" empty");
        }

        private static string Overview(WorldContainer world)
        {
            var report = new StringBuilder("OK Cycle ").Append(GameClock.Instance.GetCycle() + 1)
                .Append(SpeedControlScreen.Instance.IsPaused ? " (paused)" : " (running)")
                .Append(" | Dupes ").Append(Components.LiveMinionIdentities.Count);
            var research = Research.Instance.GetActiveResearch();
            report.Append(" | Research ").Append(research == null ? "-" : research.tech.Id);
            var inventory = world.worldInventory.GetAccessibleAmounts();
            foreach (var item in inventory.Where(entry => entry.Value > 0)
                         .OrderByDescending(entry => entry.Value).Take(6))
                report.AppendFormat(CultureInfo.InvariantCulture, " | {0} {1:0.#}", item.Key, item.Value);
            return report.ToString();
        }
    }
}
