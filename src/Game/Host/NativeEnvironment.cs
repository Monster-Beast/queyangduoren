using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeEnvironment
    {
        static readonly ConditionalWeakTable<WorldContainer, Dictionary<int, int>> pages =
            new ConditionalWeakTable<WorldContainer, Dictionary<int, int>>();
        sealed class Reading
        {
            internal readonly int Lux, Anchor, Size;
            internal readonly RoomType Type;
            internal Reading(ColonyCell data, RoomType type)
            { Lux = data.Lux; Anchor = data.RoomAnchor; Size = data.RoomSize; Type = type; }
        }
        static Reading[] readings;
        static readonly Dictionary<int, RoomType> roomTypes = new Dictionary<int, RoomType>();

        internal static int NextPage(WorldContainer world, int x, int y)
        {
            var perWorld = pages.GetOrCreateValue(world);
            int key = Grid.XYToCell(x, y);
            perWorld.TryGetValue(key, out int page);
            perWorld[key] = (page + 1) % 4;
            return page;
        }

        internal static void Capture(int cell, ref ColonyCell data)
        {
            data.Germs = Math.Max(0, Grid.DiseaseCount[cell]);
            data.GermType = data.Germs == 0 ? byte.MaxValue : Grid.DiseaseIdx[cell];
            data.Lux = Math.Max(0, Grid.LightIntensity[cell]);
            data.Decor = Grid.Decor[cell];
            var game = global::Game.Instance;
            var cavity = game.roomProber?.GetCavityForCell(cell);
            if (cavity?.room?.roomType != null && cavity.NumCells > 0)
            {
                data.RoomType = new HashedString(cavity.room.roomType.Id).HashValue;
                data.RoomAnchor = cavity.cells[0];
                data.RoomSize = cavity.NumCells;
            }
            data.RailPresent = Grid.Objects[cell, (int)ObjectLayer.SolidConduit] != null ||
                Grid.Objects[cell, (int)ObjectLayer.SolidConduitTile] != null;
            if (!data.RailPresent) return;
            data.RailConnections = (byte)((int)game.solidConduitSystem.GetDisplayConnections(cell) & 15);
            if (!game.solidConduitFlow.HasConduit(cell)) return;
            var item = game.solidConduitFlow.GetPickupable(game.solidConduitFlow.GetContents(cell).pickupableHandle);
            if (item == null) return;
            data.RailItem = item.GetComponent<KPrefabID>().PrefabTag.GetHash();
            data.RailMass = item.TotalAmount;
            var material = item.GetComponent<PrimaryElement>();
            if (material != null) data.RailMass = material.Mass;
        }

        internal static unsafe void Apply(int cell, ColonyCell data)
        {
            if (Grid.diseaseCount != null && Grid.diseaseIdx != null)
            {
                Grid.diseaseCount[cell] = data.Germs;
                Grid.diseaseIdx[cell] = data.Germs == 0 ? byte.MaxValue : data.GermType;
            }
            Grid.Decor[cell] = data.Decor;
            if (readings == null)
            {
                readings = new Reading[Grid.CellCount];
                foreach (var type in Db.Get().RoomTypes.resources)
                    roomTypes[new HashedString(type.Id).HashValue] = type;
            }
            roomTypes.TryGetValue(data.RoomType, out var typeOfRoom);
            var old = readings[cell];
            if (old == null || old.Lux != data.Lux || old.Anchor != data.RoomAnchor || old.Size != data.RoomSize || old.Type != typeOfRoom)
                readings[cell] = new Reading(data, typeOfRoom);
        }

        static Reading At(int cell)
        {
            var snapshot = readings;
            return snapshot != null && (uint)cell < snapshot.Length ? snapshot[cell] : null;
        }

        [HarmonyPatch(typeof(Grid.LightIntensityIndexer), "get_Item")]
        static class LightReading
        {
            static bool Prefix(int __0, ref int __result)
            {
                var value = At(__0);
                if (value == null || !CoopSession.IsNativeClient) return true;
                __result = value.Lux;
                return false;
            }
        }

        static readonly AccessTools.FieldRef<SimDebugView, int> selectedCell = AccessTools.FieldRefAccess<SimDebugView,int>("selectedCell");

        [HarmonyPatch(typeof(SimDebugView), "GetRoomsColour")]
        static class RoomColor
        {
            static bool Prefix(SimDebugView __0, int __1, ref Color __result)
            {
                var value = At(__1);
                if (value == null || !CoopSession.IsNativeClient) return true;
                __result = Color.black;
                int selected = selectedCell(__0);
                if (value.Type != null && Grid.IsValidCell(selected))
                {
                    __result = GlobalAssets.Instance.colorSet.GetColorByName(value.Type.category.colorName);
                    var hover = At(selected);
                    __result.a = hover?.Type != null && hover.Anchor == value.Anchor ? .75f : .45f;
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(SelectToolHoverTextCard), "UpdateHoverElements")]
        static class RoomHover
        {
            static bool Prefix(SelectToolHoverTextCard __instance)
            {
                if (!CoopSession.IsNativeClient || OverlayScreen.Instance?.GetMode() != OverlayModes.Rooms.ID ||
                    Camera.main == null || HoverTextScreen.Instance == null) return true;
                var value = At(Grid.PosToCell(Camera.main.ScreenToWorldPoint(KInputManager.GetMousePos())));
                if (value == null) return true;
                var drawer = HoverTextScreen.Instance.BeginDrawing();
                drawer.BeginShadowBar(false);
                drawer.DrawText(value.Type == null ? "无房间" : $"{value.Type.Name} · {value.Size} 格", __instance.Styles_BodyText.Standard);
                drawer.EndShadowBar();
                drawer.EndDrawing();
                return false;
            }
        }

        internal static void Reset() { readings = null; roomTypes.Clear(); }
    }
}
