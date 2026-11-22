using System.Collections.Generic;
using HarmonyLib;
using Rendering;
using UnityEngine;

namespace Queyang.Game
{
    // The block renderer owns floor geometry independently of BuildingComplete and KAnim.
    // Keep its guest registrations and topology separate from the frozen simulation grid.
    internal static class NativeBlockTiles
    {
        const ObjectLayer Foundation = ObjectLayer.FoundationTile;
        static readonly Dictionary<int, TileChange> changes = new Dictionary<int, TileChange>();
        static BlockTileRenderer renderer;

        readonly struct Tile
        {
            internal readonly BuildingDef Def;
            internal readonly SimHashes Element;
            internal readonly int RenderLayer;
            internal readonly int? Connector;

            internal Tile(BuildingDef def, SimHashes element, GameObject prefab)
            {
                Def = def;
                Element = element;
                RenderLayer = prefab.layer;
                Connector = prefab.GetComponent<IBlockTileInfo>()?.GetBlockTileConnectorID();
            }
        }

        readonly struct TileChange
        {
            internal readonly GameObject Original;
            internal readonly Tile Before, After;

            internal TileChange(GameObject original, Tile before, Tile after)
            {
                Original = original;
                Before = before;
                After = after;
            }
        }

        static bool UsesBlockRenderer(BuildingDef def) => def != null && def.TileLayer == Foundation &&
            def.WidthInCells == 1 && def.HeightInCells == 1 && def.BlockTileAtlas != null && def.BuildingComplete != null;

        internal static void Apply(int cell, ColonyCell sample, BuildingDef definition)
        {
            var active = World.Instance?.blockTileRenderer;
            if (active == null) return;
            if (renderer != active)
            {
                changes.Clear();
                renderer = active;
            }
            bool receivedTile = (sample.Flags & 48) == 16 && UsesBlockRenderer(definition) &&
                sample.AnchorDX == 0 && sample.AnchorDY == 0;
            // A solid occupied sample can report a building ahead of a foundation tile.
            if (!receivedTile && (sample.Flags & 2) != 0 && (sample.Flags & 48) != 0) return;
            var desired = receivedTile ? definition : null;
            bool hasPrevious = changes.TryGetValue(cell, out var previous);
            if (hasPrevious && previous.After.Def == desired) return;
            var original = Grid.Objects[cell, (int)Foundation];
            var building = original == null ? null : original.GetComponent<BuildingComplete>();
            var originalDef = building != null && UsesBlockRenderer(building.Def) ? building.Def : null;
            if (!hasPrevious && originalDef == desired) return;
            var before = hasPrevious ? previous.Before : originalDef != null ?
                new Tile(originalDef, original.GetComponent<PrimaryElement>().ElementID, original) : default;
            if (hasPrevious)
            {
                Remove(previous.After, cell);
                original = previous.Original;
                if (before.Def == desired)
                {
                    Add(before, cell);
                    changes.Remove(cell);
                    Refresh(cell);
                    return;
                }
            }
            else
            {
                Remove(before, cell);
            }
            var after = desired == null ? default : new Tile(desired, Material(sample), desired.BuildingComplete);
            Add(after, cell);
            changes[cell] = new TileChange(original, before, after);
            Refresh(cell);
        }

        static SimHashes Material(ColonyCell sample)
        {
            var supplied = (SimHashes)sample.Structure.Element;
            if (sample.Structure.Element != 0 && supplied != SimHashes.Void && ElementLoader.FindElementByHash(supplied) != null)
                return supplied;
            var terrain = (SimHashes)sample.ElementHash;
            return (sample.Flags & 2) != 0 && terrain != SimHashes.Void && ElementLoader.FindElementByHash(terrain) != null
                ? terrain : SimHashes.SandStone;
        }

        static void Add(Tile tile, int cell)
        {
            if (tile.Def != null) renderer.AddBlock(tile.RenderLayer, tile.Def, false, tile.Element, cell);
        }

        static void Remove(Tile tile, int cell)
        {
            if (tile.Def != null) renderer.RemoveBlock(tile.Def, false, tile.Element, cell);
        }

        static void Refresh(int cell)
        {
            Grid.CellToXY(cell, out int x, out int y);
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (InGrid(x + dx, y + dy)) renderer.Rebuild(Foundation, Grid.XYToCell(x + dx, y + dy));
        }

        static bool InGrid(int x, int y) => (uint)x < Grid.WidthInCells && (uint)y < Grid.HeightInCells;

        static BuildingDef DisplayedDef(int cell) => changes.TryGetValue(cell, out var entry) ? entry.After.Def :
            Grid.Objects[cell, (int)Foundation]?.GetComponent<Building>()?.Def;

        static int? Connector(int cell) => changes.TryGetValue(cell, out var entry) ? entry.After.Connector :
            Grid.Objects[cell, (int)Foundation]?.GetComponent<IBlockTileInfo>()?.GetBlockTileConnectorID();

        static bool Occupied(int cell) => changes.TryGetValue(cell, out var entry) ? entry.After.Def != null :
            Grid.Objects[cell, (int)Foundation] != null;

        static readonly BlockTileRenderer.Bits[,] directions = {
            { BlockTileRenderer.Bits.DownLeft, BlockTileRenderer.Bits.Down, BlockTileRenderer.Bits.DownRight },
            { BlockTileRenderer.Bits.Left, 0, BlockTileRenderer.Bits.Right },
            { BlockTileRenderer.Bits.UpLeft, BlockTileRenderer.Bits.Up, BlockTileRenderer.Bits.UpRight }
        };

        static bool HasChanges(BlockTileRenderer candidate, int x, int y, int layer)
        {
            if (candidate != renderer || layer != (int)Foundation || changes.Count == 0 || !NativeGuestScene.Ready) return false;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (InGrid(x + dx, y + dy) && changes.ContainsKey(Grid.XYToCell(x + dx, y + dy))) return true;
            return false;
        }

        static BlockTileRenderer.Bits Connections(int x, int y, bool decor)
        {
            int cell = Grid.XYToCell(x, y);
            var definition = decor ? null : DisplayedDef(cell);
            int? connector = decor ? Connector(cell) : null;
            BlockTileRenderer.Bits result = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if ((dx == 0 && dy == 0) || !InGrid(x + dx, y + dy)) continue;
                int other = Grid.XYToCell(x + dx, y + dy);
                bool connected = decor ? (dy == 0 ? connector.HasValue && connector == Connector(other) : Occupied(other)) :
                    definition != null && definition == DisplayedDef(other);
                if (connected) result |= directions[dy + 1, dx + 1];
            }
            return result;
        }

        [HarmonyPatch(typeof(BlockTileRenderer), nameof(BlockTileRenderer.GetConnectionBits))]
        static class BlockConnections
        {
            static bool Prefix(BlockTileRenderer __instance, int __0, int __1, int __2, ref BlockTileRenderer.Bits __result)
            {
                if (!HasChanges(__instance, __0, __1, __2)) return true;
                __result = Connections(__0, __1, false);
                return false;
            }
        }

        [HarmonyPatch(typeof(BlockTileRenderer), nameof(BlockTileRenderer.GetDecorConnectionBits))]
        static class DecorConnections
        {
            static bool Prefix(BlockTileRenderer __instance, int __0, int __1, int __2, ref BlockTileRenderer.Bits __result)
            {
                if (!HasChanges(__instance, __0, __1, __2)) return true;
                __result = Connections(__0, __1, true);
                return false;
            }
        }

        internal static void Reset()
        {
            if (renderer != null && renderer == World.Instance?.blockTileRenderer && Grid.Element != null)
            {
                foreach (var entry in changes)
                {
                    int cell = entry.Key;
                    if (!Grid.IsValidCell(cell)) continue;
                    Remove(entry.Value.After, cell);
                    if (entry.Value.Original != null && Grid.Objects[cell, (int)Foundation] == entry.Value.Original)
                        Add(entry.Value.Before, cell);
                    Refresh(cell);
                }
            }
            changes.Clear();
            renderer = null;
        }
    }
}
