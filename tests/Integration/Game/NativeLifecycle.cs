using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // Each round joins an actual native ONI scene, returns to the main menu, plays a separate
    // single-player colony, writes a new save and reloads it in the same Unity process.
    internal sealed class NativeLifecycle
    {
        enum Stage { WaitingMenu, Guest, Cleanup, LoadSolo, Advance, LeaveSaved, Readback, LeaveReadback, Finished }
        static readonly MethodInfo ConfirmQuit = AccessTools.DeclaredMethod(typeof(PauseScreen), "OnQuitConfirm", new[] { typeof(bool) });
        static NativeLifecycle active;
        readonly Action<string> log;
        readonly string fixturePath, initialHash, run;
        readonly HashSet<string> replicas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly FixtureDialogs dialogs;
        Stage stage;
        int round, nativeLoads, soloLoads;
        float start, initialClock;
        string replica, expectedLoad, saved, marker, failure;
        NativeScenario guest;

        internal NativeLifecycle(Action<string> log, string report)
        {
            this.log = log;
            run = Path.GetDirectoryName(Path.GetFullPath(report));
            fixturePath = Path.Combine(Util.GetKleiRootPath(), "OxygenNotIncluded", "save_files", "Fixture", "Fixture.sav");
            if (!EqualPath(fixturePath, Environment.GetEnvironmentVariable("QY_SMOKE_SAVE")) ||
                !File.Exists(fixturePath) || ConfirmQuit == null) throw new InvalidOperationException("Missing isolated lifecycle fixture.");
            initialHash = Hash(fixturePath);
            dialogs = new FixtureDialogs(log);
            active = this;
            Application.logMessageReceived += OnLog;
            Set(Stage.WaitingMenu);
        }

        static string Hash(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream));
        }
        static bool EqualPath(string a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        static bool AtMenu() => global::Game.Instance == null && MainMenu.Instance != null && MainMenu.Instance.IsActive();
        static bool Ready() => global::Game.Instance != null && global::Game.Instance.GameStarted() && !global::Game.Instance.IsLoading() &&
            ClusterManager.Instance?.activeWorld != null && GameClock.Instance != null && SpeedControlScreen.Instance != null &&
            PauseScreen.Instance != null && Components.LiveMinionIdentities.Items.Count > 0;
        void Set(Stage next) { stage = next; start = Time.realtimeSinceStartup; }
        void OnLog(string message, string stack, LogType type)
        {
            if (failure == null && type == LogType.Exception) failure = message + "\n" + stack;
        }
        void NextRound()
        {
            VerifyOffline();
            if (!AtMenu() || Hash(fixturePath) != initialHash) throw new InvalidOperationException("Dirty lifecycle starting state");
            round++;
            nativeLoads = 0; replica = null;
            string screenshots = Path.Combine(run, "native-round-" + round);
            Directory.CreateDirectory(screenshots);
            guest = new NativeScenario(line => {
                if (line.StartsWith("PASS ", StringComparison.Ordinal)) log("NATIVE_LIFECYCLE_GUEST_PASS round=" + round);
                else log(line);
            }, Path.Combine(screenshots, "screens"));
            Set(Stage.Guest);
        }

        static void VerifyOffline()
        {
            if (CoopSession.IsClient || CoopSession.IsHosting || CoopSession.IsNativeClient || NativeGuestScene.Active ||
                CoopSession.NativeView != null || CoopSession.View != null || !string.IsNullOrEmpty(CoopSession.Endpoint))
                throw new InvalidOperationException("Networking state survived the native disconnect.");
        }

        [HarmonyPatch(typeof(SaveLoader), "Load", typeof(string))]
        static class ObserveRealLoad
        {
            static void Postfix(string __0, bool __result)
            {
                var s = active;
                if (s == null) return;
                if (!__result) { s.failure = "ONI failed to load " + Path.GetFileName(__0); return; }
                if (s.stage == Stage.Guest && CoopSession.IsNativeClient)
                {
                    s.nativeLoads++;
                    s.replica = Path.GetFullPath(__0);
                    if (!s.replicas.Add(s.replica)) s.failure = "Native join reused a prior temporary save path";
                }
                else if (s.stage == Stage.LoadSolo || s.stage == Stage.Readback)
                {
                    s.soloLoads++;
                    if (!EqualPath(__0,s.expectedLoad)) s.failure = "Loaded the wrong single-player file";
                }
            }
        }

        void Load(string path, Stage next)
        {
            expectedLoad = path; soloLoads = 0;
            KPlayerPrefs.SetString("SavesDefaultToCloud","Disabled");
            SaveLoader.SetActiveSaveFilePath(path);
            Set(next);
            LoadingOverlay.Load(SaveLoader.LoadScene);
        }
        void Quit(Stage next)
        {
            Set(next);
            PauseScreen.Instance.Activate();
            ConfirmQuit.Invoke(PauseScreen.Instance,new object[]{false});
        }

        internal bool Update()
        {
            if (failure != null) throw new InvalidOperationException(failure);
            if (Time.realtimeSinceStartup - start > 145) throw new TimeoutException("Native lifecycle round " + round + " stuck at " + stage);
            if (stage == Stage.Guest)
            {
                if (guest.Update()) { guest = null; Set(Stage.Cleanup); }
                return false;
            }
            VerifyOffline();
            switch (stage)
            {
                case Stage.WaitingMenu:
                    if (AtMenu()) NextRound();
                    break;
                case Stage.Cleanup:
                    if (!AtMenu()) return false;
                    if (nativeLoads != 1 || replica == null) throw new InvalidDataException("Native round did not load exactly one replica");
                    if (Directory.Exists(Path.GetDirectoryName(replica))) return false;
                    if (Hash(fixturePath) != initialHash) throw new IOException("Native guest overwrote the solo input");
                    if (Resources.FindObjectsOfTypeAll<CoopPanel>().Count(p => p != null && p.isActiveAndEnabled) != 1)
                        throw new InvalidOperationException("Unexpected number of persistent co-op panels");
                    Load(saved ?? fixturePath, Stage.LoadSolo);
                    break;
                case Stage.LoadSolo:
                    if (!Ready() || soloLoads == 0 || dialogs.Check(true)) break;
                    if (soloLoads != 1) throw new InvalidDataException("Duplicate offline load");
                    if (marker != null && !ScheduleManager.Instance.GetSchedules().Any(s => s.name == marker))
                        throw new InvalidDataException("Previous solo session marker missing after native rejoin");
                    initialClock = GameClock.Instance.GetTime(); Set(Stage.Advance);
                    break;
                case Stage.Advance:
                    if (dialogs.Check(true)) break;
                    if (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
                    if (GameClock.Instance.GetTime() - initialClock < 1f) break;
                    marker = "NativeLifecycle-" + round + "-" + Guid.NewGuid().ToString("N");
                    ScheduleManager.Instance.GetSchedules().First().name = marker;
                    saved = Path.Combine(Path.GetDirectoryName(fixturePath), "SoloRound" + round + ".sav");
                    if (File.Exists(saved) || !EqualPath(saved,SaveLoader.Instance.Save(saved,false,false)) ||
                        !File.Exists(saved) || new FileInfo(saved).Length == 0) throw new IOException("Offline save was not written to its own path");
                    log("NATIVE_LIFECYCLE_SOLO_SAVED round=" + round + " progressed=True bytes=" + new FileInfo(saved).Length);
                    Quit(Stage.LeaveSaved);
                    break;
                case Stage.LeaveSaved:
                    if (AtMenu()) Load(saved, Stage.Readback);
                    break;
                case Stage.Readback:
                    if (!Ready() || soloLoads == 0 || dialogs.Check(true)) break;
                    if (soloLoads != 1 || !ScheduleManager.Instance.GetSchedules().Any(s => s.name == marker))
                        throw new InvalidDataException("Single-player readback marker did not match");
                    log("NATIVE_LIFECYCLE_SOLO_READBACK round=" + round + " markerVerified=True");
                    Quit(Stage.LeaveReadback);
                    break;
                case Stage.LeaveReadback:
                    if (!AtMenu()) break;
                    if (Hash(fixturePath) != initialHash) throw new IOException("Original solo fixture changed");
                    log("NATIVE_LIFECYCLE_ROUND_PASS round=" + round);
                    if (round < 3) NextRound();
                    else
                    {
                        active = null; Application.logMessageReceived -= OnLog;
                        Set(Stage.Finished);
                        log("PASS three native joins, clean exits, single-player simulation advances and manual save readbacks");
                    }
                    break;
            }
            return stage == Stage.Finished;
        }
    }
}
