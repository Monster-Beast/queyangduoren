using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    // Only intercept the guest's native input path. World updates from the host do not enter these tools.
    internal static class NativeToolCommands
    {
        sealed class Order { public string Action; public int X, Y; }
        static readonly Queue<Order> pending = new Queue<Order>();
        static readonly List<ColonyView> lastTiles = new List<ColonyView>();
        static float nextDispatch;
        static bool collectingDig;
        static readonly List<int> digCells = new List<int>();
        static readonly Dictionary<Type,string> tools = new Dictionary<Type,string> {
            { typeof(DigTool), "dig" }, { typeof(MopTool), "mop" }, { typeof(ClearTool), "sweep" },
            { typeof(HarvestTool), "harvest" }, { typeof(CancelTool), "cancel" },
            { typeof(DeconstructTool), "deconstruct" }, { typeof(PrioritizeTool), "priority" }
        };
        internal static void Reset() { pending.Clear();lastTiles.Clear();digCells.Clear();collectingDig=false;nextDispatch=0; }
        internal static void Observe(ColonyView view)
        {
            lastTiles.RemoveAll(tile => tile.X == view.X && tile.Y == view.Y);
            lastTiles.Insert(0,view);
            if (lastTiles.Count > 64) lastTiles.RemoveAt(lastTiles.Count-1);
        }
        internal static void Tick()
        {
            if(pending.Count==0 || Time.unscaledTime<nextDispatch || !CoopSession.NativeCommandsReady) return;
            var cmd = pending.Dequeue();
            CoopSession.Command(cmd.Action, cmd.X, cmd.Y);
            nextDispatch = Time.unscaledTime + 0.055f;
        }

        [HarmonyPatch(typeof(DragTool),nameof(DragTool.OnLeftClickUp))]
        static class DigSelection
        {
            static void Prefix(DragTool __instance,out bool __state)
            {
                __state=__instance is DigTool && NativeGuestScene.Ready && CoopSession.IsNativeClient;
                if(!__state) return;
                digCells.Clear();collectingDig=true;
            }
            static void Postfix(bool __state)
            {
                if(!__state) return;
                collectingDig=false;
                int start=0;
                while(start<digCells.Count)
                {
                    int cell=digCells[start];
                    Grid.CellToXY(cell,out int x,out int y);
                    int length=1;
                    while(length<64 && x+length<Grid.WidthInCells && start+length<digCells.Count &&
                          digCells[start+length]==cell+length) length++;
                    pending.Enqueue(new Order {Action=length==1?"dig":"digspan/"+length,X=x,Y=y});
                    start+=length;
                }
                digCells.Clear();
            }
            static Exception Finalizer(Exception __exception,bool __state)
            {
                if(__state) {collectingDig=false;digCells.Clear();}
                return __exception;
            }
        }
        internal static void EnqueueSelected(string action, GameObject selected)
        {
            if (selected == null) return;
            var prefab = selected.GetComponent<KPrefabID>();
            var marker = selected.GetComponent<NativeBuildingIdentity>();
            var entity = selected.GetComponent<NativeEntityIdentity>();
            int id = marker != null ? marker.SaveId : entity != null ? entity.SaveId : prefab != null ? prefab.InstanceID : -1;
            if (id <= 0) { NativeGuestFeedback.Show("正在等待房主确认这个对象。"); return; }
            var building = selected.GetComponent<Building>();
            int cell = marker != null ? marker.Cell : building != null ? building.GetCell() : Grid.PosToCell(selected);
            if (action != "sweep")
            {
                if (!TrySample(cell, out var sample) || sample.TargetId <= 0)
                { NativeGuestFeedback.Show("正在等待房主确认这个订单。"); return; }
                bool separate=marker==null && building==null && sample.OrderTargetId>0;
                int token=separate?sample.OrderTargetId:sample.TargetId;
                int priority=separate?sample.SeparateOrderPriority:sample.OrderPriority;
                if (action.StartsWith("priority", StringComparison.Ordinal))
                {
                    if (priority == 0) { NativeGuestFeedback.Show("此对象没有可修改的普通优先级。"); return; }
                    action += "/" + priority;
                }
                action += ":" + token;
            }
            Enqueue("object/" + id + "/" + action, cell);
        }
        static bool TrySample(int cell, out ColonyCell sample)
        {
            sample = default;
            if (!Grid.IsValidCell(cell)) return false;
            Grid.CellToXY(cell, out int x, out int y);
            var tile = lastTiles.Find(t => x >= t.X && y >= t.Y && x < t.X+ColonyView.Width && y < t.Y+ColonyView.Height);
            if (tile == null) return false;
            sample = tile.Cells[(y-tile.Y)*ColonyView.Width+x-tile.X];
            return (sample.Flags & 1) != 0;
        }
        internal static void Enqueue(string action, int cell)
        {
            var view = CoopSession.NativeView;
            if (view == null || !Grid.IsValidCell(cell))
            { NativeGuestFeedback.Show("该格子的联机数据尚未就绪。"); return; }
            Grid.CellToXY(cell, out int x, out int y);
            if (action == "cancel" || action == "deconstruct" || action.StartsWith("priority", StringComparison.Ordinal))
            {
                if (!TrySample(cell, out var sample)) { NativeGuestFeedback.Show("正在等待这一区域的数据。"); return; }
                bool separate=action!="deconstruct" && sample.OrderTargetId>0;
                int id = separate?sample.OrderTargetId:sample.TargetId;
                int priority=separate?sample.SeparateOrderPriority:sample.OrderPriority;
                if (id < 1) { NativeGuestFeedback.Show("此处没有可修改的订单或建筑。"); return; }
                if (action.StartsWith("priority", StringComparison.Ordinal) && priority > 0)
                    action += "/" + priority;
                action += ":" + id;
            }
            if (pending.Count >= 256) { NativeGuestFeedback.Show("已选格子过多，请等待当前订单发送后再试。"); return; }
            pending.Enqueue(new Order { Action = action, X = x, Y = y });
        }

        [HarmonyPatch]
        static class Drag
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in tools.Keys) yield return AccessTools.DeclaredMethod(type, "OnDragTool");
            }
            static bool Prefix(DragTool __instance, int __0)
            {
                if (!NativeGuestScene.Active) return true;
                if (!NativeGuestScene.Ready || !tools.TryGetValue(__instance.GetType(), out var action)) return false;
                if(action=="dig" && collectingDig)
                {
                    if(Grid.IsValidCell(__0)) digCells.Add(__0);
                    return false;
                }
                if (action == "priority")
                {
                    var selected = ToolMenu.Instance.PriorityScreen.GetLastSelectedPriority();
                    if (selected.priority_class != PriorityScreen.PriorityClass.basic)
                    { NativeGuestFeedback.Show("紧急优先级由房主设置。"); return false; }
                    action += selected.priority_value;
                }
                Enqueue(action, __0);
                return false;
            }
        }
        [HarmonyPatch(typeof(BuildTool), "TryBuild")]
        static class Build
        {
            static bool Prefix(int __0, BuildingDef ___def, Orientation ___buildingOrientation, IList<Tag> ___selectedElements,
                ref int ___lastDragCell, ref Orientation ___lastDragOrientation)
            {
                if (!NativeGuestScene.Active) return true;
                if (!NativeGuestScene.Ready || ___def == null || ___selectedElements == null || ___selectedElements.Count == 0 ||
                    (__0 == ___lastDragCell && ___buildingOrientation == ___lastDragOrientation)) return false;
                ___lastDragCell = __0; ___lastDragOrientation = ___buildingOrientation;
                var materials = new string[___selectedElements.Count];
                for (int i = 0; i < materials.Length; ++i) materials[i] = ___selectedElements[i].ToString();
                Enqueue("plan/" + ___def.PrefabID + "/" + (int)___buildingOrientation + "/" +
                    string.Join(",",materials), __0);
                return false;
            }
        }

        // The vanilla path list also contains the corners of a bent pipe or wire.
        // Send straight segments so the host can run its own network placement checks.
        [HarmonyPatch(typeof(BaseUtilityBuildTool), "BuildPath")]
        static class Utility
        {
            static bool Prefix(BuildingDef ___def, IList ___path, IList<Tag> ___selectedElements)
            {
                if (!NativeGuestScene.Active) return true;
                if (!NativeGuestScene.Ready || ___def == null || ___path == null || ___path.Count == 0) return false;
                string tool = ___def.PrefabID == WireConfig.ID ? "wire" : ___def.PrefabID == GasConduitConfig.ID ? "gaspipe" :
                    ___def.PrefabID == LiquidConduitConfig.ID ? "liquidpipe" : ___def.PrefabID == SolidConduitConfig.ID ? "rail" :
                    ___def.PrefabID == LogicWireConfig.ID ? "logicwire" : null;
                if (tool == null) { NativeGuestFeedback.Show("这种管线请由房主铺设。"); return false; }
                if (___selectedElements == null || ___selectedElements.Count != 1)
                { NativeGuestFeedback.Show("请重新选择管线材料。"); return false; }
                tool += "/" + ___selectedElements[0];
                var cellField = AccessTools.Field(___path[0].GetType(), "cell");
                int previous = (int)cellField.GetValue(___path[0]);
                for (int i = 0; i < ___path.Count; i++)
                {
                    int cell = (int)cellField.GetValue(___path[i]);
                    Grid.CellToXY(previous, out int x, out int y);
                    Enqueue(tool + "@" + x + "," + y, cell);
                    previous = cell;
                }
                return false;
            }
        }
    }
}
