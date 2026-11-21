using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class NativePipePorts
    {
        Building bridge, valve;
        int[] ports;
        int stage;
        float since;

        static Building Build(BuildingDef def, int cell, SimHashes material)
        {
            var obj = def.Build(cell, Orientation.Neutral, null,
                new[] { ElementLoader.FindElementByHash(material).tag }, 295f, false, -1f);
            if (obj == null) throw new InvalidOperationException("Could not install " + def.PrefabID);
            return obj.GetComponent<Building>();
        }

        void Create()
        {
            var view = ColonyView.Decode(HostWorld.Capture(-1, -1));
            for (int y = 1; y < ColonyView.Height-3; y++)
            for (int x = 1; x < ColonyView.Width-5; x++)
            {
                int Cell(int dx, int dy) => Grid.XYToCell(view.X + x + dx,view.Y + y + dy);
                bool free = true;
                for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 4; dx++)
                {
                    int c = Cell(dx,dy);
                    if (Grid.Visible[c] == 0 || Grid.Solid[c] || HostWorld.FindBuilding(c) != null ||
                        Grid.Objects[c,(int)ObjectLayer.LiquidConduit] != null) free = false;
                }
                if (!free) continue;
                bridge = Build(Assets.GetBuildingDef(LiquidConduitBridgeConfig.ID),Cell(1,0),SimHashes.IgneousRock);
                valve = Build(Assets.GetBuildingDef(LiquidValveConfig.ID),Cell(4,0),SimHashes.Cuprite);
                ports = new[] { bridge.GetUtilityInputCell(), bridge.GetUtilityOutputCell(),
                    valve.GetUtilityInputCell(), valve.GetUtilityOutputCell() };
                if (ports.Distinct().Count() != ports.Length) throw new InvalidDataException("Overlapping test ports");
                foreach (int cell in ports) Build(Assets.GetBuildingDef(LiquidConduitConfig.ID),cell,SimHashes.IgneousRock);
                return;
            }
            throw new InvalidDataException("Missing clear port fixture area");
        }

        void Snapshot(string folder, string name)
        {
            Grid.CellToXY(ports[0],out int x,out int y);
            var data = HostWorld.Capture(x-2, y-2);
            var view = ColonyView.Decode(data);
            foreach (int cell in ports)
            {
                Grid.CellToXY(cell,out int px,out int py);
                var sample = view.Cells[(py-view.Y)*ColonyView.Width+px-view.X].LiquidPipe;
                var actual = global::Game.Instance.liquidConduitFlow.GetContents(cell);
                if (Math.Abs(sample.Mass-actual.mass) > .0001f || (sample.Mass > 0 && sample.Element != (int)actual.element))
                    throw new InvalidDataException("Host port reading disagrees with the native flow");
            }
            File.WriteAllBytes(Path.Combine(folder,"ports-"+name+".view"),data);
        }

        internal bool Tick(string folder, Action<string> log)
        {
            if (string.IsNullOrEmpty(folder) || stage == 4) return true;
            if (stage == 0) { Create(); stage++; return false; }
            var flow = global::Game.Instance.liquidConduitFlow;
            if (ports.Any(cell => !flow.HasConduit(cell))) return false;
            var regulator = valve.GetComponent<ValveBase>();
            float Mass(int i) => flow.GetContents(ports[i]).mass;
            void Set(int i, float kg) => flow.SetContents(ports[i],
                kg > 0 ? new ConduitFlow.ConduitContents(SimHashes.Water,kg,295f,255,0) : ConduitFlow.ConduitContents.Empty);
            if (stage == 1)
            {
                regulator.CurrentFlow = 0;
                Set(0,4); Set(1,ConduitFlow.MAX_LIQUID_MASS); Set(2,3); Set(3,0);
                since = GameClock.Instance.GetTime(); stage++; return false;
            }
            if (GameClock.Instance.GetTime()-since < 1f) return false;
            if (stage == 2)
            {
                if (Mass(0) != 4 || Mass(1) != ConduitFlow.MAX_LIQUID_MASS || Mass(2) != 3 || Mass(3) != 0)
                    throw new InvalidDataException($"Unexpected flow with full bridge outlet or closed valve: {Mass(0)}/{Mass(1)} {Mass(2)}/{Mass(3)}");
                Snapshot(folder,"blocked");
                Set(1,0); regulator.CurrentFlow = regulator.MaxFlow;
                since = GameClock.Instance.GetTime(); stage++; return false;
            }
            if (Mass(1) != 4 || Mass(3) != 3)
            {
                if (GameClock.Instance.GetTime()-since > 6) throw new InvalidDataException($"No native flow after opening: {Mass(0)}/{Mass(1)} {Mass(2)}/{Mass(3)}");
                return false;
            }
            if (Mass(0) != 0 || Mass(2) != 0) throw new InvalidDataException("Native port transfer duplicated water");
            Snapshot(folder,"open");
            using (var w = new BinaryWriter(File.Create(Path.Combine(folder,"ports.bin"))))
                foreach (int cell in ports) w.Write(cell);
            log("NATIVE_PORTS_HOST fullOutletBlocked=true closedValveBlocked=true bridgeTransferredKg=4 valveTransferredKg=3");
            stage++; return true;
        }

        internal static void Replay(string folder, Action<string> log)
        {
            string path = Path.Combine(folder,"ports.bin");
            if (!File.Exists(path)) return;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                int[] cells = { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
                void Compare(string name, params float[] expected)
                {
                    NativeWorldMirror.Apply(ColonyView.Decode(File.ReadAllBytes(Path.Combine(folder,"ports-"+name+".view"))));
                    for (int i=0; i<cells.Length; i++)
                        if (global::Game.Instance.liquidConduitFlow.GetContents(cells[i]).mass != expected[i])
                            throw new InvalidDataException("Guest "+ name+" port "+i+" mass differs from host");
                }
                Compare("blocked",4,ConduitFlow.MAX_LIQUID_MASS,3,0);
                Compare("open",0,4,0,3);
                log("NATIVE_PORTS_GUEST blockedAndOpen=true bridgeKg=4 valveKg=3");
            }
        }
    }
}
