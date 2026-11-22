using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeGuestScene
    {
        private enum Phase { Idle, Loading, Live, Leaving }
        private static Phase phase;
        private static string savePath, previousCrashSave;
        private static MainMenu initialMenu;
        private static int replicaNextId, replicaGameId;
        private static volatile bool hasNextId, hasGameId;
        private static bool loaded, exitQueued;
        private static float nextCameraCheck, nextPeripheralCheck;
        private static int left, bottom;
        private static readonly Dictionary<int, float> sampled = new Dictionary<int, float>();

        internal static bool Active => phase != Phase.Idle;
        internal static bool Ready => phase == Phase.Live;
        internal static bool ApplicationQuitting { get; private set; }


        internal static void QuitApplication()
        {
            ApplicationQuitting = true;
            CoopSession.Disconnect();
            CoopSession.CleanupClosedReplica();
        }

        internal static void Load(string verifiedTempSaveFile)
        {
            if (Active || !CoopSession.IsNativeClient || global::Game.Instance != null || MainMenu.Instance == null)
                throw new InvalidOperationException("Join the colony from the main menu.");
            var file = new FileInfo(verifiedTempSaveFile);
            var parent = file.Directory;
            var temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QueyangCoop"));
            if (!file.Exists || !string.Equals(file.Extension, ".sav", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parent?.Parent?.FullName, temp, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(parent.Name, "N", out _))
                throw new InvalidDataException("Invalid temporary colony save.");
            initialMenu = MainMenu.Instance;
            previousCrashSave = KCrashReporter.MOST_RECENT_SAVEFILE;
            savePath = file.FullName;
            phase = Phase.Loading;
            loaded = exitQueued = false;
            hasNextId = hasGameId = false;
            left = bottom = -1;
            nextCameraCheck = nextPeripheralCheck = 0f;
            try { SaveLoader.LoadScene(); }
            catch { Release(); throw; }
        }

        internal static void End()
        {
            GuestGasTextures.Release();
            if (Active) phase = Phase.Leaving;
        }

        private static void Release()
        {
            KCrashReporter.MOST_RECENT_SAVEFILE = previousCrashSave;
            previousCrashSave = savePath = null;
            initialMenu = null;
            sampled.Clear();
            phase = Phase.Idle;
        }

        internal static void Tick()
        {
            if (ApplicationQuitting) return;
            if (Active)
            {
                var game = global::Game.Instance;
                // ONI also takes this route on load failure. The original main menu can
                // still be visible for a frame after LoadScene requests the backend.
                if (game == null && MainMenu.Instance != null && MainMenu.Instance != initialMenu)
                {
                    bool failedToLoad = phase == Phase.Loading;
                    Release();
                    if (CoopSession.IsNativeClient)
                        CoopSession.Disconnect(failedToLoad ? "未能载入房主的殖民地存档。" : "已离开联机殖民地。");
                }
                else if (phase == Phase.Leaving && !exitQueued && game != null && !game.IsLoading())
                {
                    exitQueued = true;
                    LoadingOverlay.Load(PauseScreen.TriggerQuitGame);
                }
                else if (phase == Phase.Loading && loaded && game != null && game.GameStarted() &&
                         ClusterManager.Instance?.activeWorld != null && CameraController.Instance != null)
                {
                    phase = Phase.Live;
                    NativeManagement.ShowBaselineHud();
                    Debug.Log("[QueyangCoop] Native guest colony ready");
                }
                else if (Ready && CoopSession.View != null && Time.unscaledTime >= nextPeripheralCheck)
                {
                    nextPeripheralCheck = Time.unscaledTime + 0.125f;
                    SubscribeToCamera();
                }
            }
            if (!Active) CoopSession.CleanupClosedReplica();
        }

        private static void SubscribeToCamera()
        {
            var world = ClusterManager.Instance?.activeWorld;
            var camera = CameraController.Instance;
            if (world == null || camera == null || camera.baseCamera == null) return;
            var lens = camera.baseCamera;
            var center = lens.transform.position;
            var origin = world.WorldOffset;
            var size = world.WorldSize;
            int x = Math.Max(origin.x, Math.Min(origin.x + size.x - ColonyView.Width,
                Mathf.FloorToInt(center.x) - ColonyView.Width / 2));
            int y = Math.Max(origin.y, Math.Min(origin.y + size.y - ColonyView.Height,
                Mathf.FloorToInt(center.y) - ColonyView.Height / 2));
            if (Time.unscaledTime >= nextCameraCheck)
            {
                nextCameraCheck = Time.unscaledTime + 0.25f;
                if (left < 0 || Math.Abs(left - x) >= 3 || Math.Abs(bottom - y) >= 2)
                {
                    left = x;
                    bottom = y;
                    var current = CoopSession.NativeView;
                    if (current == null || current.X != x || current.Y != y)
                    {
                        CoopSession.Subscribe(x, y);
                        return;
                    }
                }
            }
            RequestVisibleTile(lens, center, origin, size);
        }

        // Use stable world-aligned tiles so a small pan retains the age of the previous
        // samples. Choose the least recently requested visible tile, nearest first on ties.
        private static void RequestVisibleTile(Camera lens, Vector3 center, Vector2I origin, Vector2I size)
        {
            const int width = ColonyView.Width, height = ColonyView.Height;
            if (size.x < width || size.y < height) return;
            float halfHeight = lens.orthographicSize, halfWidth = halfHeight * lens.aspect;
            int minX = Mathf.Clamp(Mathf.FloorToInt(center.x - halfWidth) - origin.x, 0, size.x - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(center.y - halfHeight) - origin.y, 0, size.y - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(center.x + halfWidth) - 1 - origin.x, 0, size.x - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(center.y + halfHeight) - 1 - origin.y, 0, size.y - 1);
            if (origin.x + minX >= left && origin.x + maxX < left + width &&
                origin.y + minY >= bottom && origin.y + maxY < bottom + height) return;
            float oldest = float.PositiveInfinity, nearest = float.PositiveInfinity;
            int chosenX = -1, chosenY = -1, key = -1;
            for (int row = minY / height; row <= maxY / height; row++)
            {
                for (int col = minX / width; col <= maxX / width; col++)
                {
                    int x = origin.x + Math.Min(col * width, size.x - width);
                    int y = origin.y + Math.Min(row * height, size.y - height);
                    // Entire visible intersection already receives central deltas.
                    if (Math.Max(x, minX + origin.x) >= left && Math.Min(x + width, maxX + origin.x + 1) <= left + width &&
                        Math.Max(y, minY + origin.y) >= bottom && Math.Min(y + height, maxY + origin.y + 1) <= bottom + height) continue;
                    int cell = Grid.XYToCell(x, y);
                    if (!sampled.TryGetValue(cell, out float lastRequest)) lastRequest = float.NegativeInfinity;
                    if (Time.unscaledTime - lastRequest < 1f) continue;
                    float dx = center.x - (x + width * 0.5f), dy = center.y - (y + height * 0.5f);
                    float distance = dx * dx + dy * dy;
                    if (lastRequest > oldest || lastRequest == oldest && distance >= nearest) continue;
                    oldest = lastRequest;
                    nearest = distance;
                    key = cell;
                    chosenX = x;
                    chosenY = y;
                }
            }
            if (key >= 0 && CoopSession.RequestPeripheral(chosenX, chosenY)) sampled[key] = Time.unscaledTime;
        }

        // Game.LoadSettings restores these from the host's save. SetInt immediately
        // persists KPlayerPrefs, yet Game.OnPrefabInit needs the loaded NextUniqueID.
        [HarmonyPatch(typeof(KPlayerPrefs), "SetInt")]
        private static class WriteGamePreference
        {
            private static bool Prefix(string __0, int __1)
            {
                if (!Active) return true;
                if (__0 == "NextUniqueID") { replicaNextId = __1; hasNextId = true; return false; }
                if (__0 == "GAME_ID") { replicaGameId = __1; hasGameId = true; return false; }
                return true;
            }
        }

        [HarmonyPatch]
        private static class ReadGamePreference
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(KPlayerPrefs), "GetInt", new[] { typeof(string) });
                yield return AccessTools.Method(typeof(KPlayerPrefs), "GetInt", new[] { typeof(string), typeof(int) });
            }
            private static bool Prefix(string __0, ref int __result)
            {
                if (!Active) return true;
                if (__0 == "NextUniqueID" && hasNextId) { __result = replicaNextId; return false; }
                if (__0 == "GAME_ID" && hasGameId) { __result = replicaGameId; return false; }
                return true;
            }
        }

        [HarmonyPatch(typeof(SaveLoader), "GetActiveSaveFilePath")]
        private static class ReadActivePath
        {
            private static bool Prefix(ref string __result)
            {
                if (!Active) return true;
                __result = savePath;
                return false;
            }
        }

        [HarmonyPatch(typeof(SaveLoader), "Load", typeof(string))]
        private static class ReadSave
        {
            private static bool Prefix(string __0) => !Active ||
                (!loaded && string.Equals(__0, savePath, StringComparison.OrdinalIgnoreCase));
            private static void Postfix(string __0, bool __result)
            {
                if (Active && string.Equals(__0, savePath, StringComparison.OrdinalIgnoreCase))
                    loaded |= __result;
            }
        }

        [HarmonyPatch(typeof(PauseScreen), "OnQuitConfirm")]
        private static class QuitWithoutSaving
        {
            private static void Prefix(ref bool __0)
            {
                if (!Active) return;
                __0 = false;
                exitQueued = true;
                CoopSession.Disconnect();
            }
        }

        [HarmonyPatch(typeof(PauseScreen), "OnDesktopQuitConfirm")]
        private static class DesktopQuitWithoutSaving
        {
            private static void Prefix(ref bool __0)
            {
                if (Active) __0 = false;
            }
        }

        // Leave the zero-time native grid binding in Game.UnsafePrefabInit and the
        // render/UI frames intact. Only the host advances simulation or schedules AI.
        [HarmonyPatch]
        private static class GuestGuards
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(global::Game), "SimEveryTick");
                yield return AccessTools.Method(typeof(BrainScheduler), "RenderEveryTick");
                yield return AccessTools.Method(typeof(Brain), "UpdateBrain");
                yield return AccessTools.Method(typeof(RemoteWorkerSM), "StartNextChore");
                yield return AccessTools.Method(typeof(ColonyAchievementTracker), "UnlockPlatformAchievement");
                yield return AccessTools.Method(typeof(SaveLoader), "SetActiveSaveFilePath");
                yield return AccessTools.Method(typeof(SaveLoader), "MoveCorruptFile");
                yield return AccessTools.Method(typeof(SaveLoader), "LoadFromWorldGen");
                yield return AccessTools.Method(typeof(SaveLoader), "InitialSave");
                yield return AccessTools.Method(typeof(SaveLoader), "Save", new[] { typeof(string), typeof(bool), typeof(bool) });
                yield return AccessTools.Method(typeof(global::Game), "StartDelayedSave");
                yield return AccessTools.Method(typeof(global::Game), "StartDelayedInitialSave");
                yield return AccessTools.Method(typeof(RetireColonyUtility), "SaveColonySummaryData");
                yield return AccessTools.Method(typeof(Timelapser), "SaveColonyPreview");
                yield return AccessTools.Method(typeof(PauseScreen), "OnLoad");
                yield return AccessTools.Method(typeof(PauseScreen), "OnSave");
                yield return AccessTools.Method(typeof(PauseScreen), "OnSaveAs");
            }
            private static bool Prefix() => !Active;
        }
    }
}
