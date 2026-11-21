using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Queyang.Game
{
    // The saved network graph belongs to the baseline. For edited cells, use the same
    // connection animations and layers as the vanilla overlay without rebuilding a sim graph.
    internal static class NativeUtilityDisplay
    {
        static readonly ObjectLayer[] layers = { ObjectLayer.Wire, ObjectLayer.GasConduit, ObjectLayer.LiquidConduit, ObjectLayer.LogicWire };
        static readonly ObjectLayer[] tiles = { ObjectLayer.WireTile, ObjectLayer.GasConduitTile, ObjectLayer.LiquidConduitTile, ObjectLayer.LogicWireTile };
        static readonly string[] prefabIds = { WireConfig.ID, GasConduitConfig.ID, LiquidConduitConfig.ID, LogicWireConfig.ID };
        static readonly Dictionary<int, KBatchedAnimController> proxies = new Dictionary<int, KBatchedAnimController>();
        static readonly HashSet<KBatchedAnimController> suppressed = new HashSet<KBatchedAnimController>();
        static readonly List<int> expired = new List<int>();
        static Transform parent;
        static int active = -1;
        static IUtilityNetworkMgr manager;

        internal static void Begin(Transform root)
        {
            var overlay = OverlayScreen.Instance;
            var mode = overlay == null ? default : overlay.GetMode();
            int next = mode == OverlayModes.Power.ID ? 0 : mode == OverlayModes.GasConduits.ID ? 1 :
                mode == OverlayModes.LiquidConduits.ID ? 2 : mode == OverlayModes.Logic.ID ? 3 : -1;
            if (next != active)
            {
                Reset();
                active = next;
                parent = root;
                var game = global::Game.Instance;
                switch (next)
                {
                    case 0: manager = game.electricalConduitSystem; break;
                    case 1: manager = game.gasConduitSystem; break;
                    case 2: manager = game.liquidConduitSystem; break;
                    case 3: manager = game.logicCircuitSystem; break;
                }
            }
            if (proxies.Count == 0) return;
            var camera = CameraController.Instance?.baseCamera;
            if (camera == null) return;
            var center = camera.transform.position;
            float height = camera.orthographicSize + 2, width = camera.orthographicSize * camera.aspect + 2;
            expired.Clear();
            foreach (var entry in proxies)
            {
                var xy = Grid.CellToXY(entry.Key);
                if (Mathf.Abs(xy.x + .5f - center.x) > width || Mathf.Abs(xy.y + .5f - center.y) > height)
                    expired.Add(entry.Key);
            }
            foreach (int cell in expired) Remove(cell);
        }

        internal static void Apply(int cell, ColonyCell data)
        {
            if (active < 0 || manager == null) return;
            bool present = (data.Utilities & (1 << active)) != 0;
            var local = Grid.Objects[cell, (int)layers[active]] ?? Grid.Objects[cell, (int)tiles[active]];
            var localAnim = local == null ? null : local.GetComponent<KBatchedAnimController>();
            var connections = (UtilityConnections)((data.Connections >> (active * 4)) & 15);
            bool same = local != null && present && manager.GetDisplayConnections(cell) == connections;
            if (localAnim != null)
            {
                if (same && suppressed.Remove(localAnim)) localAnim.SetVisiblity(true);
                else if (!same && suppressed.Add(localAnim)) localAnim.SetVisiblity(false);
            }
            if (!present || same) { Remove(cell); return; }
            if (!proxies.TryGetValue(cell, out var proxy))
            {
                var def = Assets.GetBuildingDef(prefabIds[active]);
                var source = def?.BuildingComplete?.GetComponent<KBatchedAnimController>();
                if (source == null) return;
                var go = new GameObject(def.Name);
                go.SetActive(false);
                go.layer = source.gameObject.layer;
                go.transform.SetParent(parent, false);
                go.transform.position = Grid.CellToPosCBC(cell, Grid.SceneLayer.FXFront);
                proxy = go.AddComponent<KBatchedAnimController>();
                proxy.AnimFiles = source.AnimFiles; proxy.animScale = source.animScale;
                proxy.initialAnim = manager.GetVisualizerString(connections);
                proxy.sceneLayer = Grid.SceneLayer.FXFront; proxy.fgLayer = Grid.SceneLayer.FXFront2;
                proxy.Offset = source.Offset;
                go.AddComponent<KBoxCollider2D>().size = Vector2.one;
                go.AddComponent<KPrefabID>().PrefabTag = new Tag(def.PrefabID);
                go.AddComponent<KSelectable>().SetName(def.Name);
                go.SetActive(true);
                proxies.Add(cell, proxy);
            }
            HashedString state = manager.GetVisualizerString(connections);
            if (proxy.currentAnim != state && proxy.HasAnimation(state)) proxy.Play(state);
            var culture = CultureInfo.InvariantCulture;
            string reading;
            if (active == 0)
            {
                reading = string.Format(culture, "{0:0} W / {1:0} W", data.WattsUsed, data.WattsSafe);
                proxy.TintColour = data.WattsUsed > data.WattsSafe ? Color.red : Color.white;
            }
            else if (active == 3)
            {
                reading = data.LogicValue.ToString(culture);
                proxy.TintColour = data.LogicValue == 0 ? Color.red : Color.green;
            }
            else
            {
                var pipe = active == 1 ? data.GasPipe : data.LiquidPipe;
                var element = pipe.Mass > 0 ? ElementLoader.FindElementByHash((SimHashes)pipe.Element) : null;
                reading = element == null ? "0 kg" : string.Format(culture, "{0} {1:0.###} kg {2:0.#} K",
                    element.name, pipe.Mass, pipe.Temperature);
                proxy.TintColour = element == null ? Color.white : element.substance.uiColour;
            }
            proxy.GetComponent<KSelectable>().SetName(proxy.gameObject.name + " · " + reading);
        }

        static void Remove(int cell)
        {
            if (!proxies.TryGetValue(cell, out var proxy)) return;
            proxies.Remove(cell);
            if (proxy != null) NativeWorldMirror.DestroySelectableProxy(proxy.gameObject);
        }

        internal static void Reset()
        {
            foreach (var anim in suppressed) if (anim != null) anim.SetVisiblity(true);
            foreach (var proxy in proxies.Values) if (proxy != null) NativeWorldMirror.DestroySelectableProxy(proxy.gameObject);
            suppressed.Clear(); proxies.Clear(); manager = null; parent = null; active = -1;
        }
    }
}
