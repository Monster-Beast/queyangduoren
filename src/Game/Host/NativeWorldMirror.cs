using System.Collections.Generic;
using UnityEngine;

namespace Queyang.Game
{
    // Only the disposable guest scene receives these samples. No simulation messages or work
    // orders are issued here. Klei renderers and tooltips read the frozen scene's display grid.
    internal static class NativeWorldMirror
    {
        static readonly Dictionary<int, BuildingDef> buildings = new Dictionary<int, BuildingDef>();
        static readonly Dictionary<int, Marker> buildingMarkers = new Dictionary<int, Marker>();
        static readonly Dictionary<int, GameObject> digMarkers = new Dictionary<int, GameObject>();
        static readonly Dictionary<int, GameObject> mopMarkers = new Dictionary<int, GameObject>();
        static readonly HashSet<int> visibleDupes = new HashSet<int>();
        static readonly Dictionary<int, MinionIdentity> localDupes = new Dictionary<int, MinionIdentity>();
        static readonly Dictionary<int, KBatchedAnimController> spawnedDupes = new Dictionary<int, KBatchedAnimController>();
        static readonly List<int> expiredDupes = new List<int>();
        static readonly HashSet<KBatchedAnimController> hidden = new HashSet<KBatchedAnimController>();
        static GameObject markerRoot;

        sealed class Marker
        {
            public int Hash, Target, SaveId;
            public byte Style, Flags;
            public GameObject Object;
            public KBatchedAnimController Anim;
            public PrimaryElement Primary;
        }

        internal static void Apply(ColonyView view)
        {
            var world = ClusterManager.Instance?.activeWorld;
            if (!CoopSession.IsNativeClient || !NativeGuestScene.Ready || view == null || !view.Native ||
                view.X < 0 || view.Y < 0 || view.X > int.MaxValue - ColonyView.Width ||
                view.Y > int.MaxValue - ColonyView.Height || world == null || Grid.Element == null ||
                Grid.WorldIdx == null || Grid.Visible == null || Grid.VisMasks == null || Grid.BuildMasks == null)
                return;
            if (markerRoot == null)
            {
                markerRoot = new GameObject("Co-op display objects");
                markerRoot.AddComponent<NativeDupePose>();
                foreach (var def in Assets.BuildingDefs)
                    buildings[new HashedString(def.PrefabID).HashValue] = def;
            }
            NativeUtilities.Clock(view);
            NativeUtilityDisplay.Begin(markerRoot.transform);
            for (int i = 0; i < view.Cells.Length; i++)
            {
                int x = view.X + i % ColonyView.Width, y = view.Y + i / ColonyView.Width;
                if (x >= Grid.WidthInCells || y >= Grid.HeightInCells) continue;
                int cell = Grid.XYToCell(x, y);
                if (!Grid.IsValidCell(cell) || cell >= Grid.WorldIdx.Length || cell >= Grid.Element.Length ||
                    cell >= Grid.Visible.Length || cell >= Grid.VisMasks.Length || cell >= Grid.BuildMasks.Length ||
                    Grid.WorldIdx[cell] != world.id) continue;
                var sample = view.Cells[i];
                if ((sample.Flags & 1) == 0) continue;
                ApplyElement(cell, sample);
                ApplyDig(cell, (sample.Flags & 8) != 0);
                if (sample.DigMaterial!=0) NativeOrderColors.ApplyDig(GetDigRenderer(cell),sample);
                if (view.HasEnvironment) ApplyMop(cell, sample.MopOrdered);
                ApplyBuilding(cell, sample);
                if (view.CoversEnvironment(i) && sample.BuildingSaveId > 0)
                    NativeMaterialReadings.Apply(GetBuildingSelectable(cell, sample.BuildingSaveId)?.GetComponent<PrimaryElement>(), sample.Structure);
                if (view.Detailed)
                {
                    bool hasEnvironment = view.CoversEnvironment(i);
                    if (hasEnvironment) NativeEnvironment.Apply(cell, sample);
                    NativeUtilities.Apply(cell, sample, hasEnvironment);
                    NativeUtilityDisplay.Apply(cell, sample, hasEnvironment);
                }
            }
            ApplyDupes(view);
            if (view.HasResources) NativeResources.Apply(view, world, markerRoot.transform);
        }

        static unsafe void ApplyElement(int cell, ColonyCell sample)
        {
            // Bound by Game.StepTheSim(0) during scene initialization. Re-read on each call
            // since Sim.Shutdown releases them. Never retain a native pointer across frames.
            if (Grid.mass == null || Grid.elementIdx == null || Grid.temperature == null) return;
            var element = ElementLoader.FindElementByHash((SimHashes)sample.ElementHash);
            if (element == null || element.idx >= ElementLoader.elements.Count || float.IsNaN(sample.Mass) ||
                float.IsInfinity(sample.Mass) || float.IsNaN(sample.Temperature) || float.IsInfinity(sample.Temperature)) return;
            bool solid = (sample.Flags & 2) != 0;
            bool redraw = Grid.Element[cell] != element || Grid.RenderedByWorld[cell] != sample.RenderGround ||
                Grid.Solid[cell] != solid;
            if (redraw && Grid.Damage!=null) Grid.Damage[cell]=0;
            Grid.Element[cell] = element;
            Grid.elementIdx[cell] = element.idx;
            Grid.mass[cell] = Mathf.Clamp(sample.Mass, 0, Sim.MaxMass);
            Grid.temperature[cell] = Mathf.Clamp(sample.Temperature, 0, Sim.MaxTemperature);
            Grid.Visible[cell] = sample.Visibility;
            Grid.Revealed[cell] = sample.Visibility != 0;
            Grid.RenderedByWorld[cell] = sample.RenderGround;
            if (solid) Grid.BuildMasks[cell] |= Grid.BuildFlags.Solid;
            else Grid.BuildMasks[cell] &= ~Grid.BuildFlags.Solid;
            if (redraw) World.Instance?.groundRenderer?.MarkDirty(cell);
        }

        static void ApplyDig(int cell, bool ordered)
        {
            var local = Grid.Objects[cell, (int)ObjectLayer.DigPlacer];
            if (local != null)
            {
                var dig = local.GetComponent<Diggable>();
                if (dig != null && dig.materialDisplay != null) dig.materialDisplay.enabled = ordered;
            }
            bool needed = ordered && local == null;
            ApplyDesignation(cell, needed, DigPlacerConfig.ID, DigTool.Instance, digMarkers);
        }

        static void ApplyMop(int cell, bool ordered)
        {
            var local = Grid.Objects[cell, (int)ObjectLayer.MopPlacer];
            if (local != null)
            {
                var mesh = local.GetComponentInChildren<MeshRenderer>(true);
                if (mesh != null) mesh.enabled = ordered;
            }
            ApplyDesignation(cell, ordered && local == null, MopPlacerConfig.ID, MopTool.Instance, mopMarkers);
        }

        static void ApplyDesignation(int cell, bool needed, string prefabId, InterfaceTool tool, Dictionary<int, GameObject> markers)
        {
            if (markers.TryGetValue(cell, out var marker))
            {
                if (!needed) { Object.Destroy(marker); markers.Remove(cell); }
                return;
            }
            if (!needed || tool == null) return;
            var prefab = Assets.TryGetPrefab(new Tag(prefabId));
            if (prefab == null) return;
            var sourceRenderer = prefabId==DigPlacerConfig.ID ? prefab.GetComponent<Diggable>()?.materialDisplay :
                prefab.GetComponentInChildren<MeshRenderer>(true);
            if (sourceRenderer == null) return;
            var source = sourceRenderer.GetComponent<MeshFilter>();
            if (source==null || source.sharedMesh==null) return;
            marker = new GameObject(prefabId);
            marker.layer = source.gameObject.layer;
            marker.transform.SetParent(markerRoot.transform, false);
            // Vanilla DigTool.PlaceDig and MopTool.OnDragTool both position the
            // prefab at the bottom centre of the tile, slightly in front of its tool layer.
            var origin=Grid.CellToPosCBC(cell,tool.visualizerLayer);
            origin.z-=.15f;
            marker.transform.position = origin + (source.transform.position - prefab.transform.position);
            marker.transform.localScale = source.transform.lossyScale;
            marker.transform.rotation = source.transform.rotation;
            marker.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
            marker.AddComponent<MeshRenderer>().sharedMaterial = sourceRenderer.sharedMaterial;
            markers.Add(cell, marker);
        }

        internal static MeshRenderer GetDigRenderer(int cell)
        {
            if (!Grid.IsValidCell(cell)) return null;
            var local = Grid.Objects[cell, (int)ObjectLayer.DigPlacer];
            var renderer = local == null ? null : local.GetComponent<Diggable>()?.materialDisplay;
            if (renderer!=null) return renderer;
            return digMarkers.TryGetValue(cell,out var marker) && marker!=null ? marker.GetComponent<MeshRenderer>() : null;
        }

        internal static bool IsDigVisible(int cell)
        {
            var mesh=GetDigRenderer(cell);
            return mesh!=null && mesh.enabled && mesh.sharedMaterial!=null && mesh.gameObject.activeInHierarchy;
        }

        internal static bool IsMopVisible(int cell)
        {
            if (!Grid.IsValidCell(cell)) return false;
            var local=Grid.Objects[cell,(int)ObjectLayer.MopPlacer];
            var renderer=local==null ? null : local.GetComponentInChildren<MeshRenderer>();
            if (renderer!=null && renderer.enabled && renderer.gameObject.activeInHierarchy) return true;
            return mopMarkers.TryGetValue(cell,out var proxy) && proxy!=null && proxy.activeInHierarchy &&
                proxy.GetComponent<MeshRenderer>()?.enabled==true;
        }

        static void ApplyBuilding(int cell, ColonyCell sample)
        {
            var local = HostWorld.FindBuilding(cell);
            var item = local == null ? null : local.GetComponent<Building>();
            bool match = item != null && new HashedString(item.Def.PrefabID).HashValue == sample.BuildingHash &&
                (item.GetComponent<Constructable>() != null) == ((sample.Flags & 32) != 0) &&
                (int)item.Orientation == ((sample.BuildingStyle >> 4) & 7);
            if (match && sample.AnchorDX == 0 && sample.AnchorDY == 0 && sample.BuildingSaveId > 0)
                match = item.GetComponent<KPrefabID>()?.InstanceID == sample.BuildingSaveId;
            if (item != null)
            {
                var anim = item.GetComponent<KBatchedAnimController>();
                if (!match || (sample.AnchorDX == 0 && sample.AnchorDY == 0)) SetVisible(anim, match);
                if (match && sample.AnchorDX == 0 && sample.AnchorDY == 0)
                {
                    if (anim!=null && sample.HasBlueprintTint) anim.TintColour=NativeOrderColors.Unpack(sample.BlueprintTint);
                    Pose(anim, sample.BuildingAnimation, sample.BuildingFrame);
                    ApplyPrimary(item.GetComponent<PrimaryElement>(), sample.Structure);
                    if (sample.TargetId > 0 && item.GetComponent<Door>() != null && item.GetComponent<BuildingComplete>() != null)
                    {
                        var controls = item.GetComponent<NativeProxySettings>() ?? item.gameObject.AddComponent<NativeProxySettings>();
                        controls.Initialize(cell, sample.TargetId);
                    }
                }
            }
            if (sample.AnchorDX != 0 || sample.AnchorDY != 0)
            {
                RemoveBuildingMarker(cell);
                return;
            }
            byte flags = (byte)(sample.Flags & 48);
            bool deconstruct = (sample.BuildingStyle & 128) != 0;
            if (flags == 0 || (match && !deconstruct) || !buildings.TryGetValue(sample.BuildingHash, out var def))
            {
                RemoveBuildingMarker(cell);
                return;
            }
            if (buildingMarkers.TryGetValue(cell, out var current) && current.Hash == sample.BuildingHash &&
                (current.Style & 127) == (sample.BuildingStyle & 127) && current.Flags == flags &&
                current.Target == sample.TargetId && current.SaveId == sample.BuildingSaveId)
            {
                current.Anim.TintColour = NativeOrderColors.BuildingTint(sample);
                current.Style = sample.BuildingStyle;
                Pose(current.Anim, sample.BuildingAnimation, sample.BuildingFrame);
                ApplyPrimary(current.Primary, sample.Structure);
                return;
            }
            RemoveBuildingMarker(cell);
            var prefab = (flags & 32) != 0 ? def.BuildingUnderConstruction : def.BuildingComplete;
            var source = prefab == null ? null : prefab.GetComponent<KBatchedAnimController>();
            if (source == null || source.AnimFiles == null || source.AnimFiles.Length == 0) return;
            var marker = new GameObject(def.PrefabID);
            marker.SetActive(false);
            marker.layer = prefab.layer;
            marker.transform.SetParent(markerRoot.transform, false);
            marker.transform.position = Grid.CellToPosCBC(cell, source.sceneLayer);
            var controller = marker.AddComponent<KBatchedAnimController>();
            controller.AnimFiles = source.AnimFiles;
            controller.animScale = source.animScale;
            controller.initialAnim = source.initialAnim;
            controller.defaultAnim = source.defaultAnim;
            controller.sceneLayer = source.sceneLayer;
            controller.fgLayer = source.fgLayer;
            controller.Offset = source.Offset;
            controller.materialType = source.materialType;
            var orientation = (Orientation)((sample.BuildingStyle >> 4) & 7);
            if (orientation >= Orientation.R90 && orientation <= Orientation.R270)
                controller.Rotation = -90 * (int)orientation;
            controller.FlipX = orientation == Orientation.FlipH;
            controller.FlipY = orientation == Orientation.FlipV;
            controller.TintColour = NativeOrderColors.BuildingTint(sample);
            var collider = marker.AddComponent<KBoxCollider2D>();
            collider.size = new Vector2(def.WidthInCells, def.HeightInCells);
            collider.offset = new Vector2(def.GetVisualizerOffset().x, def.HeightInCells * .5f);
            // ONI's hover card reads the tag on every selected object. It is metadata only:
            // the host's saved KPrefabID and spawn functions must never be copied to the proxy.
            marker.AddComponent<KPrefabID>().PrefabTag = new Tag(def.PrefabID);
            marker.AddComponent<KSelectable>().SetName(def.Name);
            var identity = marker.AddComponent<NativeBuildingIdentity>();
            identity.Cell = cell; identity.SaveId = sample.BuildingSaveId;
            if ((flags & 32) == 0 && (sample.Flags & 8) == 0 && sample.TargetId > 0)
                marker.AddComponent<NativeProxySettings>().Initialize(cell, sample.TargetId);
            PrimaryElement primary = null;
            if (sample.Structure.Element != 0 && ElementLoader.FindElementByHash((SimHashes)sample.Structure.Element) != null)
            {
                primary = marker.AddComponent<PrimaryElement>();
                primary.SetElement((SimHashes)sample.Structure.Element, false);
                ApplyPrimary(primary, sample.Structure);
            }
            marker.SetActive(true);
            buildingMarkers[cell] = new Marker { Hash = sample.BuildingHash, Target = sample.TargetId, SaveId = sample.BuildingSaveId, Style = sample.BuildingStyle,
                Flags = flags, Object = marker, Anim = controller, Primary = primary };
        }

        internal static void ApplyPrimary(PrimaryElement primary, MaterialSample material)
        {
            if (primary == null || (int)primary.ElementID != material.Element || material.Element == 0) return;
            // A real building can redirect its temperature accessor into the sim. Its visual
            // copy reads the authoritative scalar without invoking that setter or destroying
            // a zero-mass construction through PrimaryElement.SetMass.
            primary.getTemperatureCallback = ReadTemperature;
            primary.InternalTemperature = Mathf.Clamp(material.Temperature, 0, Sim.MaxTemperature);
            float mass = Mathf.Clamp(material.Mass, 0, PrimaryElement.MAX_MASS);
            if (primary.MassPerUnit > 0 && primary.Mass != mass)
            {
                float units = mass / primary.MassPerUnit;
                if (!float.IsInfinity(units) && !float.IsNaN(units)) primary.Units = units;
            }
        }

        static float ReadTemperature(PrimaryElement p) => p.InternalTemperature;

        internal static KSelectable GetBuildingSelectable(int cell, int saveId)
        {
            if (!Grid.IsValidCell(cell) || saveId <= 0) return null;
            if (buildingMarkers.TryGetValue(cell, out var marker))
                return marker.SaveId == saveId ? marker.Object.GetComponent<KSelectable>() : null;
            var obj = HostWorld.FindBuilding(cell);
            if (obj == null || obj.GetComponent<KPrefabID>()?.InstanceID != saveId) return null;
            var selectable = obj.GetComponent<KSelectable>();
            return selectable != null && selectable.IsSelectable ? selectable : null;
        }

        static void RemoveBuildingMarker(int cell)
        {
            if (!buildingMarkers.TryGetValue(cell, out var marker)) return;
            DestroySelectableProxy(marker.Object);
            buildingMarkers.Remove(cell);
        }

        internal static KSelectable GetDupeSelectable(int hostId)
        {
            if (localDupes.TryGetValue(hostId, out var local) && local != null && !hidden.Contains(local.GetComponent<KBatchedAnimController>())) return local.GetComponent<KSelectable>();
            if (spawnedDupes.TryGetValue(hostId, out var marker) && marker != null) return marker.GetComponent<KSelectable>();
            return null;
        }

        static void ApplyDupes(ColonyView view)
        {
            visibleDupes.Clear();
            localDupes.Clear();
            foreach (var local in Components.LiveMinionIdentities.Items)
            {
                if (local == null) continue;
                var prefab = local.GetComponent<KPrefabID>();
                if (localDupes.TryGetValue(prefab.InstanceID, out var duplicate))
                {
                    if (KPrefabIDTracker.Get().GetInstance(prefab.InstanceID) != prefab)
                    {
                        SetVisible(local.GetComponent<KBatchedAnimController>(), false);
                        continue;
                    }
                    SetVisible(duplicate.GetComponent<KBatchedAnimController>(), false);
                }
                localDupes[prefab.InstanceID] = local;
            }
            foreach (var d in view.Dupes)
            {
                if (d.X < view.X || d.Y < view.Y || d.X >= view.X + ColonyView.Width ||
                    d.Y >= view.Y + ColonyView.Height || d.X >= Grid.WidthInCells || d.Y >= Grid.HeightInCells ||
                    (view.Cells[(d.Y - view.Y) * ColonyView.Width + d.X - view.X].Flags & 1) == 0) continue;
                if (d.SaveId < 0 || !visibleDupes.Add(d.SaveId)) continue;
                bool current = NativeDupePose.Current(d.SaveId, view.DupePoseTick);
                if (!current && !NativeDupePose.Knows(d.SaveId)) continue;
                KBatchedAnimController anim;
                if (localDupes.TryGetValue(d.SaveId, out var dupe))
                {
                    if (spawnedDupes.ContainsKey(d.SaveId)) RemoveDupeProxy(d.SaveId);
                    if (!string.IsNullOrEmpty(d.Name) && dupe.GetProperName() != d.Name) dupe.SetName(d.Name);
                    anim = dupe.GetComponent<KBatchedAnimController>();
                }
                else if (!spawnedDupes.TryGetValue(d.SaveId, out anim))
                {
                    anim = CreateDupe(d);
                    if (anim == null) continue;
                    spawnedDupes.Add(d.SaveId, anim);
                }
                if (anim == null) continue;
                if (dupe == null && !string.IsNullOrEmpty(d.Name) && anim.gameObject.name != d.Name)
                {
                    anim.gameObject.name = d.Name;
                    anim.GetComponent<KSelectable>().SetName(d.Name);
                }
                // A newer unreliable motion sample must not suppress name/selector
                // updates from the reliable identity stream.
                if (!current) continue;
                SetVisible(anim, true);
                if (view.DupePoseTick > 0)
                {
                    NativeDupePose.Apply(anim, d, view.DupePoseTick, view.Paused);
                    continue;
                }
                NativeDupePose.Forget(d.SaveId, 0);
                var pos = anim.transform.position;
                anim.transform.position = new Vector3(d.X + d.SubX / 255f, d.Y + d.SubY / 255f, pos.z);
                anim.FlipX = d.FlipX;
                Pose(anim, d.Animation, d.AnimationPosition);
                anim.SetDirty();
            }
            foreach (var dupe in Components.LiveMinionIdentities.Items)
            {
                if (dupe == null) continue;
                int id=dupe.GetComponent<KPrefabID>().InstanceID;
                if (visibleDupes.Contains(id) || !NativeDupePose.Current(id, view.DupePoseTick)) continue;
                var pos = NativeDupePose.LastPosition(id, dupe.transform.position);
                bool sampled = pos.x >= view.X && pos.x < view.X + ColonyView.Width && pos.y >= view.Y &&
                    pos.y < view.Y + ColonyView.Height && view.CoversDupeId(id);
                if (sampled || (view.DupePoseTick > 0 && !NativeDupePose.Knows(id)))
                {
                    if (sampled) NativeDupePose.Forget(id, view.DupePoseTick);
                    SetVisible(dupe.GetComponent<KBatchedAnimController>(), false);
                }
            }
            expiredDupes.Clear();
            var camera = CameraController.Instance?.baseCamera;
            foreach (var pair in spawnedDupes)
            {
                var anim = pair.Value;
                if (anim == null || visibleDupes.Contains(pair.Key) || !NativeDupePose.Current(pair.Key, view.DupePoseTick)) continue;
                var pos = NativeDupePose.LastPosition(pair.Key, anim.transform.position);
                bool sampled = pos.x >= view.X && pos.x < view.X + ColonyView.Width && pos.y >= view.Y &&
                    pos.y < view.Y + ColonyView.Height;
                bool offCamera = camera != null && (Mathf.Abs(pos.x - camera.transform.position.x) >
                    camera.orthographicSize * camera.aspect + 2 || Mathf.Abs(pos.y - camera.transform.position.y) >
                    camera.orthographicSize + 2);
                if (sampled ? view.CoversDupeId(pair.Key) : offCamera) expiredDupes.Add(pair.Key);
            }
            foreach (int id in expiredDupes)
            {
                NativeDupePose.Forget(id, view.DupePoseTick);
                RemoveDupeProxy(id);
            }
        }

        static void RemoveDupeProxy(int id)
        {
            var anim = spawnedDupes[id];
            spawnedDupes.Remove(id);
            if (anim == null) return;
            SetVisible(anim, false);
            hidden.Remove(anim);
            DestroySelectableProxy(anim.gameObject);
        }

        static KBatchedAnimController CreateDupe(ColonyDupe dupe)
        {
            var personality = Db.Get().Personalities.TryGet(new HashedString(dupe.Personality));
            if (personality == null) return null;
            var prefab = Assets.TryGetPrefab(new Tag(MinionConfig.ID));
            var source = prefab == null ? null : prefab.GetComponent<KBatchedAnimController>();
            if (source == null) return null;
            var marker = new GameObject(dupe.Name);
            marker.SetActive(false);
            marker.layer = prefab.layer;
            marker.transform.SetParent(markerRoot.transform, false);
            marker.transform.position = Grid.CellToPosCBC(Grid.XYToCell(dupe.X, dupe.Y), source.sceneLayer);
            var anim = marker.AddComponent<KBatchedAnimController>();
            anim.AnimFiles = source.AnimFiles;
            anim.sceneLayer = source.sceneLayer;
            anim.fgLayer = source.fgLayer;
            anim.initialAnim = source.initialAnim;
            anim.defaultAnim = source.defaultAnim;
            anim.animScale = source.animScale;
            anim.isMovable = true;
            var collider = marker.AddComponent<KBoxCollider2D>();
            collider.size = new Vector2(.75f, 1.8f); collider.offset = new Vector2(0, .9f);
            marker.AddComponent<KPrefabID>().PrefabTag = new Tag(MinionConfig.ID);
            marker.AddComponent<KSelectable>().SetName(dupe.Name);
            marker.AddComponent<NativeDupeIdentityMarker>().HostId = dupe.SaveId;
            marker.AddComponent<NativePersonMenu>();
            SymbolOverrideControllerUtil.AddToPrefab(marker);
            var appearance = marker.AddComponent<Accessorizer>();
            marker.SetActive(true);
            appearance.ApplyMinionPersonality(personality);
            return anim;
        }

        static void SetVisible(KBatchedAnimController anim, bool visible)
        {
            if (anim == null) return;
            if (visible ? hidden.Remove(anim) : hidden.Add(anim))
            {
                var tool = SelectTool.Instance;
                if (!visible && tool != null && tool.selected != null && tool.selected.gameObject == anim.gameObject)
                    ClearSelection(anim.gameObject);
                var selectable = anim.GetComponent<KSelectable>();
                if (selectable != null) selectable.IsSelectable = visible;
                anim.SetVisiblity(visible);
            }
        }

        static void Pose(KBatchedAnimController anim, int hash, byte frame)
        {
            if (anim == null || hash == 0) return;
            var name = new HashedString(hash);
            if (!anim.HasAnimation(name)) return;
            if (anim.currentAnim.HashValue != hash) anim.Play(name);
            anim.SetPositionPercent(frame / 255f);
        }

        internal static void Reset()
        {
            NativeDupePose.ClearTracks();
            if (markerRoot != null) DestroySelectableProxy(markerRoot);
            markerRoot = null;
            buildingMarkers.Clear(); digMarkers.Clear(); mopMarkers.Clear(); buildings.Clear(); visibleDupes.Clear(); hidden.Clear();
            spawnedDupes.Clear(); expiredDupes.Clear(); localDupes.Clear(); NativeUtilities.Reset(); NativeUtilityDisplay.Reset();
            NativeResources.Reset();
            NativeEnvironment.Reset();
            NativeMaterialReadings.Reset();
        }

        internal static void DestroySelectableProxy(GameObject target)
        {
            if (target == null) return;
            // KSelectable.OnCleanUp normally unselects itself. The transient proxy's
            // animator can already be destroyed at that point. Let ONI clear the live
            // highlight and close the details panel before releasing any components.
            ClearSelection(target);
            Object.Destroy(target);
        }

        internal static void ClearSelection(GameObject target)
        {
            var details = DetailsScreen.Instance;
            if (details != null && details.target != null && details.target.transform.IsChildOf(target.transform))
            {
                details.DeselectAndClose();
                return;
            }
            var tool = SelectTool.Instance;
            var selected = tool == null ? null : tool.selected;
            if (selected != null && selected.transform.IsChildOf(target.transform))
                tool.Select(null, true);
        }
    }
}
