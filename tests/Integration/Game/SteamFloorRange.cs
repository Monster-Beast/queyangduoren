using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Rendering;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamFloorRange
    {
        const string Key = "qy_floor_";
        static readonly System.Reflection.FieldInfo RenderInfo = AccessTools.Field(typeof(BlockTileRenderer), "renderInfo");
        Constructable plan;
        BuildingDef def;
        int cell = Grid.InvalidCell, neighbor, stage, planId, completeId;
        int stagedCell = Grid.InvalidCell, stagedNeighbor;
        GameObject stagedTile;
        float until, nextLog, zoom;
        bool activated;
        Vector3 cameraPosition;
        FixtureFrame frame;

        [HarmonyPatch(typeof(NativeWorldMirror), nameof(NativeWorldMirror.Apply))]
        static class CaptureMirrorError
        {
            static Exception Finalizer(Exception __exception)
            {
                if (__exception != null && Environment.GetEnvironmentVariable("QY_FLOOR_RANGE") == "1")
                    Trace("FLOOR_RANGE_MIRROR " + __exception);
                return __exception;
            }
        }

        [HarmonyPatch(typeof(CoopSession), "Close")]
        static class CaptureDisconnect
        {
            static void Prefix(string __0)
            {
                if (Environment.GetEnvironmentVariable("QY_FLOOR_RANGE") == "1") Trace("FLOOR_RANGE_SESSION " + __0);
            }
        }

        internal bool Check(CSteamID room, CSteamID peer, bool host, int round, Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_FLOOR_RANGE") != "1" || round != 1 || stage == 4) return true;
            float now = Time.realtimeSinceStartup;
            var camera = CameraController.Instance;
            if (until == 0)
            {
                until = now + 135;
                zoom = camera.OrthographicSize;
                cameraPosition = camera.transform.position;
                def = Assets.GetBuildingDef(TileConfig.ID);
            }
            if (now >= until) throw new TimeoutException($"Native floor host={host} stage={stage} cell={cell} plan={planId} built={completeId}");
            if (cell == Grid.InvalidCell)
            {
                if (host)
                {
                    if (!SetUp(room, log)) return false;
                }
                else
                {
                    var layout = Read(room, "layout").Split('|');
                    if (layout.Length != 3) return false;
                    cell = Parse(layout[0]); neighbor = Parse(layout[1]); planId = Parse(layout[2]);
                }
            }
            if (stage == 3)
            {
                if (Read(room, "release") != "1") return false;
                camera.SetTargetPos(cameraPosition, zoom, false);
                camera.SetPosition(cameraPosition);
                camera.OrthographicSize = zoom;
                stage++;
                return true;
            }
            var focus = (Grid.CellToPosCCC(cell, Grid.SceneLayer.Building) + Grid.CellToPosCCC(neighbor, Grid.SceneLayer.Building)) * .5f;
            focus.z = camera.transform.position.z;
            camera.SetTargetPos(focus, 4f, false);
            camera.SetPosition(focus);
            camera.OrthographicSize = 4f;

            if (host && stage > 0 && !activated)
            {
                var speed = SpeedControlScreen.Instance;
                if (stage == 2)
                {
                    var native = HostWorld.FindBuilding(cell);
                    native.GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 9));
                    native.GetComponent<Deconstructable>().QueueDeconstruction(false);
                }
                speed.SetSpeed(0);
                while (speed.IsPaused) speed.Unpause(false);
                activated = true;
            }
            var nativeBuilding = HostWorld.FindBuilding(cell);
            bool pending = host ? nativeBuilding?.GetComponent<Constructable>() != null : NativeWorldMirror.GetBuildingSelectable(cell, planId) != null;
            var solid = Grid.Solid[cell];
            var blockRenderer = World.Instance.blockTileRenderer;
            int count = RenderCount(blockRenderer, def, cell);
            int outlines = RenderCount(blockRenderer,def,cell,BlockTileRenderer.RenderInfoLayer.UnderConstruction);
            Grid.CellToXY(neighbor, out int nx, out int ny);
            var direction = cell > neighbor ? BlockTileRenderer.Bits.Right : BlockTileRenderer.Bits.Left;
            bool joined = (blockRenderer.GetConnectionBits(nx, ny, (int)def.TileLayer) & direction) != 0;
            bool decorated = (blockRenderer.GetDecorConnectionBits(nx, ny, (int)def.TileLayer) & direction) != 0;
            if (host && stage == 1 && completeId == 0 && solid && nativeBuilding?.GetComponent<BuildingComplete>() != null)
            {
                completeId = nativeBuilding.GetComponent<KPrefabID>().InstanceID;
                Publish(room, "built", completeId.ToString(CultureInfo.InvariantCulture));
            }
            if (!host && stage > 0 && completeId == 0 && !int.TryParse(Read(room, "built"), out completeId)) return false;
            bool noGuestGridOccupier = host || Grid.Objects[cell, (int)def.TileLayer] == null;
            bool hasSample = Sample(cell, out var observed);
            int neighborCount = RenderCount(blockRenderer, def, neighbor);
            bool observedBuilt = stage == 1 && completeId > 0 && (host ? nativeBuilding?.GetComponent<BuildingComplete>() != null :
                NativeWorldMirror.GetBuildingSelectable(cell, completeId) != null);
            bool valid = noGuestGridOccupier && outlines == (stage == 0 ? 1 : 0) && (stage == 0 ? (host ? pending : hasSample) && !solid && count == 0 :
                neighborCount == 1 && (stage == 1 ? observedBuilt && !pending && solid && count == 1 && joined && decorated :
                    !pending && !solid && count == 0 && !joined && !decorated &&
                    (host ? nativeBuilding == null : NativeWorldMirror.GetBuildingSelectable(cell, completeId) == null)));
            if (!valid)
            {
                if (now >= nextLog)
                {
                    nextLog = now + 4;
                    log($"FLOOR_RANGE_WAIT stage={stage} cell={cell} plan={planId} pending={pending} built={observedBuilt} solid={solid} count={count} neighborCount={neighborCount} connected={joined} decor={decorated} noGuestGridOccupier={noGuestGridOccupier} raw={hasSample}/{observed.Flags}/{observed.BuildingHash}/{observed.BuildingSaveId}");
                }
                return false;
            }
            if (host && stage > 0 && !SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Pause(false);
            if (frame == null)
            {
                string dir = Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                frame = new FixtureFrame(Path.Combine(dir, $"floor-{(host ? "host" : "guest")}-{stage}.png"));
                frame.Capture();
                return false;
            }
            if (!frame.Complete) return false;
            string ack = Key + "seen_" + stage;
            if (host && SteamMatchmaking.GetLobbyMemberData(room, peer, ack) != "1") return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(room, ack, "1");
            log($"FLOOR_RANGE stage={stage} cell={cell} neighbor={neighbor} plan={planId} complete={completeId} pending={pending} solid={solid} blocks={count} outlines={outlines} neighborCount={neighborCount} connected={joined} decor={decorated} noGuestGridOccupier={noGuestGridOccupier} raw={hasSample}/{observed.Flags}/{observed.BuildingHash}/{observed.BuildingSaveId}");
            stage++;
            frame = null;
            activated = false;
            if (host && stage == 3) Publish(room, "release", "1");
            return false;
        }

        bool SetUp(CSteamID room, Action<string> log)
        {
            if (!CoopSession.IsHosting || Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                throw new InvalidOperationException("Floor range needs an isolated host");
            var speed = SpeedControlScreen.Instance;
            if (stagedTile != null && (!stagedTile.GetComponent<KPrefabID>().isSpawned || !Grid.Solid[stagedNeighbor])) return false;
            if (!speed.IsPaused) speed.Pause(false);
            var world = ClusterManager.Instance.activeWorld;
            var workers = Components.LiveMinionIdentities.Items.Where(d => d != null && ClusterUtil.GetMyWorldId(d) == world.id).ToArray();
            var placement = def.BuildingUnderConstruction.GetComponent<Constructable>();
            int origin = Grid.PosToCell(workers[0]);
            var inventory = world.worldInventory.GetAccessibleAmounts();
            var material = MaterialSelector.GetValidMaterials(def.MaterialCategory[0], true)
                .First(tag => inventory.TryGetValue(tag, out float available) && available >= def.Mass[0]);
            if (stagedTile != null)
            {
                if (Place(room, stagedCell, stagedNeighbor, material)) return true;
                throw new InvalidDataException("Native tile blueprint could not be placed next to its prepared neighbor");
            }

            bool Empty(int target) => Grid.IsValidBuildingCell(target) && Grid.WorldIdx[target] == world.id && Grid.Visible[target] != 0 &&
                !Grid.Solid[target] && HostWorld.FindBuilding(target) == null && !workers.Any(worker => Grid.PosToCell(worker) == target) &&
                HostWorld.VacantFootprint(def, target, Orientation.Neutral, world);
            bool Reachable(int target) => workers.Any(worker => worker.GetComponent<Navigator>().CanReach(target, placement.GetOffsets(target)));
            foreach (var existing in Components.BuildingCompletes.Items.Where(b => b != null && b.Def == def && ClusterUtil.GetMyWorldId(b) == world.id)
                .OrderBy(b => Grid.GetCellDistance(origin, b.GetCell())))
            {
                int anchor = existing.GetCell();
                foreach (int target in new[] { anchor - 1, anchor + 1 })
                {
                    if (Empty(target) && Grid.GetCellDistance(anchor, target) == 1 && Reachable(target) && Place(room, target, anchor, material)) return true;
                }
            }

            // Prepare one native neighboring tile in an empty test bay when the save has no usable exposed edge.
            foreach (int target in Enumerable.Range(0, Grid.CellCount).Where(c => Grid.GetCellDistance(origin, c) <= 28 && Empty(c))
                .OrderBy(c => Grid.GetCellDistance(origin, c)))
            {
                if (!Reachable(target)) continue;
                foreach (int anchor in new[] { target - 1, target + 1 })
                {
                    if (!Empty(anchor) || Grid.GetCellDistance(target, anchor) != 1 ||
                        !def.IsValidBuildLocation(def.BuildingPreview, anchor, Orientation.Neutral, false, out _)) continue;
                    stagedTile = def.Build(anchor, Orientation.Neutral, null, new[] { material }, 295f, false, -1f);
                    if (stagedTile == null) continue;
                    stagedCell = target;
                    stagedNeighbor = anchor;
                    log($"FLOOR_RANGE_BAY cell={target} nativeNeighbor={anchor} isolated=True");
                    while (speed.IsPaused) speed.Unpause(false);
                    return false;
                }
            }
            throw new InvalidDataException("No reachable native floor location or empty test bay");
        }

        bool Place(CSteamID room, int target, int anchor, Tag material)
        {
            var obj = def.TryPlace(def.BuildingPreview, Grid.CellToPosCBC(target, def.SceneLayer), Orientation.Neutral, new[] { material });
            if (obj == null) return false;
            cell = target; neighbor = anchor;
            plan = obj.GetComponent<Constructable>();
            planId = obj.GetComponent<KPrefabID>().InstanceID;
            plan.GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 9));
            Publish(room, "layout", FormattableString.Invariant($"{cell}|{neighbor}|{planId}"));
            return true;
        }

        static int RenderCount(BlockTileRenderer renderer, BuildingDef def, int cell, BlockTileRenderer.RenderInfoLayer layer=BlockTileRenderer.RenderInfoLayer.Built)
        {
            var records = (IDictionary)RenderInfo.GetValue(renderer);
            var info = records[new KeyValuePair<BuildingDef, BlockTileRenderer.RenderInfoLayer>(def,layer)];
            var occupied = info == null ? null : (IDictionary)AccessTools.Field(info.GetType(), "occupiedCells").GetValue(info);
            return occupied != null && occupied.Contains(cell) ? (int)occupied[cell] : 0;
        }

        static bool Sample(int cell, out ColonyCell sample)
        {
            sample = default;
            var view = CoopSession.NativeView;
            if (view == null) return false;
            Grid.CellToXY(cell, out int x, out int y);
            if ((uint)(x - view.X) >= ColonyView.Width || (uint)(y - view.Y) >= ColonyView.Height) return false;
            sample = view.Cells[(y - view.Y) * ColonyView.Width + x - view.X];
            return (sample.Flags & 1) != 0;
        }

        static void Trace(string line)
        {
            var path = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
            if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, line + Environment.NewLine);
        }

        static int Parse(string value) => int.Parse(value, CultureInfo.InvariantCulture);
        static string Read(CSteamID room, string name) => SteamMatchmaking.GetLobbyData(room, Key + name);
        static void Publish(CSteamID room, string key, string value)
        {
            if (!SteamMatchmaking.SetLobbyData(room, Key + key, value)) throw new IOException("Could not publish native floor checkpoint " + key);
        }
    }
}
