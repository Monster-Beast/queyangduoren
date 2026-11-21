using System;
using System.IO;
using System.Linq;
using Queyang.Game;

namespace Queyang.Integration
{
    // Uses isolated real conduits during recording. Both snapshots precede joining so
    // replay can exercise a change in a pipe that already exists in the loaded save.
    internal sealed class NativeInfrastructure
    {
        int cell = Grid.InvalidCell;
        bool finished;

        internal bool Record(string folder, Action<string> log)
        {
            if (finished || string.IsNullOrEmpty(folder)) return true;
            var game = global::Game.Instance;
            if (cell == Grid.InvalidCell)
            {
                var center = ColonyView.Decode(HostWorld.Capture(-1, -1));
                var defs = new[] { GasConduitConfig.ID, LiquidConduitConfig.ID, SolidConduitConfig.ID, LogicWireConfig.ID, WireConfig.ID }
                    .Select(Assets.GetBuildingDef).ToArray();
                for (int y = 1; y < ColonyView.Height - 1 && cell == Grid.InvalidCell; ++y)
                    for (int x = 1; x < ColonyView.Width - 1; ++x)
                    {
                        int candidate = Grid.XYToCell(center.X + x, center.Y + y);
                        if (Grid.Visible[candidate] == 0 || Grid.Solid[candidate] || HostWorld.FindBuilding(candidate) != null ||
                            Array.Exists(defs, def => Grid.Objects[candidate,(int)def.TileLayer] != null)) continue;
                        cell = candidate; break;
                    }
                if (cell == Grid.InvalidCell) throw new InvalidOperationException("No isolated test conduit location");
                for (int i = 0; i < defs.Length; i++)
                {
                    var material = i < 2 ? SimHashes.IgneousRock : i == 2 || i == 4 ? SimHashes.Cuprite : SimHashes.Copper;
                    var built = defs[i].Build(cell, Orientation.Neutral, null, new[] { ElementLoader.FindElementByHash(material).tag }, 295f, false, -1f);
                    if (built == null) throw new InvalidOperationException("Could not build " + defs[i].PrefabID);
                }
                SpeedControlScreen.Instance.Unpause(false);
                return false;
            }
            if (!game.gasConduitFlow.HasConduit(cell) || !game.liquidConduitFlow.HasConduit(cell) || !game.solidConduitFlow.HasConduit(cell)) return false;
            Grid.CellToXY(cell, out int cx, out int cy);
            byte[] Capture() => HostWorld.Capture(cx - 2, cy - 2);
            var gas = new ConduitFlow.ConduitContents(SimHashes.Oxygen, .65f, 298f, 0, 137);
            game.gasConduitFlow.SetContents(cell, gas);
            game.liquidConduitFlow.SetContents(cell, new ConduitFlow.ConduitContents(SimHashes.Water, 4.5f, 296f, 1, 256));
            var iron = ElementLoader.FindElementByHash(SimHashes.Iron);
            var cargo = iron.substance.SpawnResource(Grid.CellToPosCBC(cell, Grid.SceneLayer.Ore), 7f, 294f, 255, 0);
            game.solidConduitFlow.AddPickupable(cell, cargo.GetComponent<Pickupable>());
            var full = Capture();
            var first = ColonyView.Decode(full);
            int index = (cy - first.Y) * ColonyView.Width + cx - first.X;
            var c = first.Cells[index];
            if (!first.CoversEnvironment(index) || (c.Utilities & 15) != 15 || !c.RailPresent ||
                c.RailItem != iron.tag.GetHash() || c.RailMass != 7f || c.GasPipe.Germs != 137 || c.LiquidPipe.Germs != 256)
                throw new InvalidOperationException($"Infrastructure capture mismatch: layers={c.Utilities} rail={c.RailMass} germs={c.GasPipe.Germs}/{c.LiquidPipe.Germs}");
            game.gasConduitFlow.SetContents(cell, ConduitFlow.ConduitContents.Empty);
            game.solidConduitFlow.RemovePickupable(cell);
            var empty = Capture();
            File.WriteAllBytes(Path.Combine(folder,"utilities-full.view"), full);
            File.WriteAllBytes(Path.Combine(folder,"utilities-empty.view"), empty);
            File.WriteAllText(Path.Combine(folder,"utilities.cell"), cell.ToString(System.Globalization.CultureInfo.InvariantCulture));
            finished = true;
            log($"NATIVE_HOST_UTILITIES cell={cell} gas=0.65 liquid=4.5 germs=137/256 rail=7");
            return true;
        }

        internal static void Replay(string folder, Action<string> log)
        {
            if (string.IsNullOrEmpty(folder)) return;
            int cell = int.Parse(File.ReadAllText(Path.Combine(folder,"utilities.cell")), System.Globalization.CultureInfo.InvariantCulture);
            var full = ColonyView.Decode(File.ReadAllBytes(Path.Combine(folder,"utilities-full.view")));
            var overlay = OverlayScreen.Instance;
            var previous = overlay.GetMode();
            overlay.ToggleOverlay(OverlayModes.SolidConveyor.ID, true);
            NativeWorldMirror.Apply(full);
            var game = global::Game.Instance;
            var gas = game.gasConduitFlow.GetContents(cell);
            var liquid = game.liquidConduitFlow.GetContents(cell);
            Grid.CellToXY(cell, out int x, out int y);
            var c = full.Cells[(y-full.Y)*ColonyView.Width+x-full.X];
            if (gas.mass != .65f || gas.diseaseCount != 137 || liquid.mass != 4.5f || liquid.diseaseCount != 256 ||
                Grid.LightIntensity[cell] != c.Lux || Grid.Decor[cell] != c.Decor ||
                Grid.DiseaseCount[cell] != c.Germs) throw new InvalidDataException("Guest utility/overlay readback mismatch");
            string cargo = NativeUtilityDisplay.GetOverlaySelectable(cell)?.GetName();
            if (cargo == null || !cargo.Contains("7 kg")) throw new InvalidDataException("Rail tooltip did not show cargo: " + cargo);
            NativeWorldMirror.Apply(ColonyView.Decode(File.ReadAllBytes(Path.Combine(folder,"utilities-empty.view"))));
            if (game.gasConduitFlow.GetContents(cell).mass != 0) throw new InvalidDataException("Guest retained drained pipe");
            string drained = NativeUtilityDisplay.GetOverlaySelectable(cell)?.GetName();
            if (drained == null || !drained.Contains("空")) throw new InvalidDataException("Rail tooltip retained cargo: " + drained);
            overlay.ToggleOverlay(previous, true);
            log("NATIVE_GUEST_UTILITIES gas=0.65->0 liquid=4.5 germs=137/256 railTooltip=7kg->empty gridReadback=True");
        }
    }
}
