using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    public static partial class HostWorld
    {
        static void ReadUtilityValues(ref ColonyCell c, int cell, Dictionary<ushort, ColonyCell> circuits)
        {
            var game = global::Game.Instance;
            if ((c.Utilities & 2) != 0) c.GasPipe = PipeContents(game.gasConduitFlow, cell);
            if ((c.Utilities & 4) != 0) c.LiquidPipe = PipeContents(game.liquidConduitFlow, cell);
            if ((c.Utilities & 8) != 0 && game.logicCircuitSystem.GetNetworkForCell(cell) is LogicCircuitNetwork logic)
                c.LogicValue = logic.OutputValue;
            if ((c.Utilities & 1) == 0) return;
            var manager = game.circuitManager;
            ushort id = manager.GetCircuitID(cell);
            if (id == CircuitManager.INVALID_ID) return;
            if (!circuits.TryGetValue(id, out var reading))
            {
                reading.WattsUsed = Mathf.Max(0, manager.GetWattsUsedByCircuit(id));
                reading.WattsGenerated = Mathf.Max(0, manager.GetWattsGeneratedByCircuit(id));
                reading.WattsSafe = Mathf.Max(0, manager.GetMaxSafeWattageForCircuit(id));
                reading.WattsNeeded = Mathf.Max(0, manager.GetWattsNeededWhenActive(id));
                circuits.Add(id, reading);
            }
            c.WattsUsed = reading.WattsUsed; c.WattsGenerated = reading.WattsGenerated;
            c.WattsSafe = reading.WattsSafe; c.WattsNeeded = reading.WattsNeeded;
        }

        static MaterialSample PipeContents(ConduitFlow flow, int cell)
        {
            if (!flow.HasConduit(cell)) return default;
            var contents = flow.GetContents(cell);
            return contents.mass <= 0 ? default : new MaterialSample {
                Element = (int)contents.element, Mass = contents.mass, Temperature = contents.temperature,
                GermType = contents.diseaseIdx, Germs = contents.diseaseCount };
        }
    }

    internal static class NativeUtilities
    {
        static readonly Dictionary<ushort, ColonyCell> circuits = new Dictionary<ushort, ColonyCell>();
        static readonly Dictionary<LogicCircuitNetwork, int> signals = new Dictionary<LogicCircuitNetwork, int>();
        static readonly AccessTools.FieldRef<GameClock, int> cycle = AccessTools.FieldRefAccess<GameClock, int>("cycle");
        static readonly AccessTools.FieldRef<GameClock, float> seconds =
            AccessTools.FieldRefAccess<GameClock, float>("timeSinceStartOfCycle");

        internal static void Clock(ColonyView view)
        {
            var clock = GameClock.Instance;
            if (clock == null || view.Cycle < 1) return;
            // SetTime calls AddTime, which executes day-transition callbacks. Only change the
            // two display fields consumed by ONI's clock getters on the frozen replica.
            cycle(clock) = view.Cycle - 1;
            seconds(clock) = view.CycleFraction * 600f;
            var screen = SpeedControlScreen.Instance;
            if (screen == null) return;
            SetToggle(screen.pauseButtonWidget, view.Paused);
            SetToggle(screen.speedButtonWidget_slow, view.Speed == Mathf.RoundToInt(screen.normalSpeed));
            SetToggle(screen.speedButtonWidget_medium, view.Speed == Mathf.RoundToInt(screen.fastSpeed));
            SetToggle(screen.speedButtonWidget_fast, view.Speed == Mathf.RoundToInt(screen.ultraSpeed));
        }

        static void SetToggle(GameObject widget, bool value)
        {
            var toggle = widget == null ? null : widget.GetComponent<KToggle>();
            if (toggle == null) return;
            if (toggle.isOn != value) toggle.SetIsOnWithoutNotify(value);
            // Selecting 1x can turn off 3x through Unity's ToggleGroup before
            // we visit it. Its value is already false, while Klei still shows
            // the old active image and flourish until they are refreshed.
            foreach (var image in widget.GetComponents<ImageToggleState>()) image.SetActiveState(value);
            toggle.ActivateFlourish(value);
        }

        internal static void Apply(int cell, ColonyCell data, bool hasEnvironment)
        {
            var game = global::Game.Instance;
            // Klei's per-cell grid can hold contents before a conduit is registered.
            // A pipe built after the join save has no simulation graph on the guest.
            // Clear the same grid slot when a later sample removes the pipe.
            SetContents(game.gasConduitFlow, cell, data.GasPipe, ConduitFlow.MAX_GAS_MASS, hasEnvironment);
            SetContents(game.liquidConduitFlow, cell, data.LiquidPipe, ConduitFlow.MAX_LIQUID_MASS, hasEnvironment);
            ushort circuit = game.circuitManager.GetCircuitID(cell);
            if (circuit != CircuitManager.INVALID_ID && (data.Utilities & 1) != 0) circuits[circuit] = data;
            if (game.logicCircuitSystem.GetNetworkForCell(cell) is LogicCircuitNetwork logic)
                signals[logic] = data.LogicValue;
        }

        static void SetContents(ConduitFlow flow, int cell, MaterialSample sample, float maxMass, bool hasEnvironment)
        {
            var old = flow.GetContents(cell);
            if (sample.Mass == 0)
            {
                if (old.mass > 0) flow.SetContents(cell, ConduitFlow.ConduitContents.Empty);
                return;
            }
            int germs = hasEnvironment ? sample.Germs : old.diseaseCount;
            byte strain = germs == 0 ? byte.MaxValue : hasEnvironment ? sample.GermType : old.diseaseIdx;
            if (old.mass == sample.Mass && old.temperature == sample.Temperature && (int)old.element == sample.Element &&
                old.diseaseCount == germs && (germs == 0 || old.diseaseIdx == strain))
                return;
            var element = ElementLoader.FindElementByHash((SimHashes)sample.Element);
            if (element != null)
                flow.SetContents(cell, new ConduitFlow.ConduitContents(element.id, Mathf.Clamp(sample.Mass, 0, maxMass),
                    Mathf.Clamp(sample.Temperature, 0, Sim.MaxTemperature), strain, germs));
        }

        internal static void Reset() { circuits.Clear(); signals.Clear(); }

        [HarmonyPatch(typeof(LogicCircuitNetwork), "get_OutputValue")]
        static class Signal
        {
            static void Postfix(LogicCircuitNetwork __instance, ref int __result)
            {
                if (CoopSession.IsNativeClient && signals.TryGetValue(__instance, out var value)) __result = value;
            }
        }

        [HarmonyPatch(typeof(CircuitManager), "GetWattsUsedByCircuit")]
        static class Load
        {
            static void Postfix(ushort __0, ref float __result)
            { if (CoopSession.IsNativeClient && circuits.TryGetValue(__0, out var c)) __result = c.WattsUsed; }
        }
        [HarmonyPatch(typeof(CircuitManager), "GetWattsGeneratedByCircuit")]
        static class Generation
        {
            static void Postfix(ushort __0, ref float __result)
            { if (CoopSession.IsNativeClient && circuits.TryGetValue(__0, out var c)) __result = c.WattsGenerated; }
        }
        [HarmonyPatch(typeof(CircuitManager), "GetMaxSafeWattageForCircuit")]
        static class Rating
        {
            static void Postfix(ushort __0, ref float __result)
            { if (CoopSession.IsNativeClient && circuits.TryGetValue(__0, out var c)) __result = c.WattsSafe; }
        }
        [HarmonyPatch(typeof(CircuitManager), "GetWattsNeededWhenActive")]
        static class Potential
        {
            static void Postfix(ushort __0, ref float __result)
            { if (CoopSession.IsNativeClient && circuits.TryGetValue(__0, out var c)) __result = c.WattsNeeded; }
        }
    }
}
