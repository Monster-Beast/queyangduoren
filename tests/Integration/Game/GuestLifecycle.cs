using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // A single genuine Unity process alternates between being a guest and playing an isolated save.
    internal sealed class GuestLifecycle
    {
        private enum Stage { Menu, FirstView, PannedView, Reply, Cleared, Loading, Advancing, Returning, Done }
        private static readonly MethodInfo QuitConfirmed = AccessTools.DeclaredMethod(
            typeof(PauseScreen), "OnQuitConfirm", new[] { typeof(bool) });

        private readonly Action<string> log;
        private readonly string save, initialHash, screenshot;
        private Stage stage;
        private int round = 1, firstViewFrame;
        private float since, initialClock, paintReady;
        private static GuestLifecycle waitingForLoad;
        private int completedLoads;
        private string loadedFile;
        private bool loadSucceeded;
        public bool Done => stage == Stage.Done;

        public GuestLifecycle(Action<string> log, string report)
        {
            this.log = log;
            string root = Path.GetFullPath(Util.GetKleiRootPath());
            save = Path.GetFullPath(Environment.GetEnvironmentVariable("QY_SMOKE_SAVE"));
            string expected = Path.Combine(root, "OxygenNotIncluded", "save_files", "Fixture", "Fixture.sav");
            if (!string.Equals(save, expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(save))
                throw new InvalidOperationException("Lifecycle test requires the isolated Fixture.sav.");
            if (QuitConfirmed == null || QuitConfirmed.ReturnType != typeof(void))
                throw new MissingMethodException("PauseScreen.OnQuitConfirm(bool)");
            if (Environment.GetEnvironmentVariable("QY_SMOKE_CAPTURE") == "1")
                screenshot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report)), "guest-lifecycle.png");
            initialHash = FileHash();
            Set(Stage.Menu);
        }

        private string FileHash()
        {
            using (var src = File.OpenRead(save))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(src));
        }

        private void CheckSave()
        {
            if (FileHash() != initialHash) throw new IOException("Lifecycle fixture save was overwritten in round " + round);
        }

        private void Set(Stage value)
        {
            stage = value;
            since = Time.realtimeSinceStartup;
            log("LIFECYCLE round=" + round + " stage=" + value);
        }

        private static bool AtMenu() => global::Game.Instance == null && MainMenu.Instance != null && MainMenu.Instance.IsActive();
        private static bool HasPanelTexture() =>
            Resources.FindObjectsOfTypeAll<Texture2D>().Any(t => t != null && t.name == "Co-op viewport");

        private static void CheckOffline()
        {
            if (CoopSession.IsHosting || CoopSession.IsClient || CoopSession.View != null ||
                !string.IsNullOrEmpty(CoopSession.Endpoint)) throw new Exception("Co-op session state survived disconnect.");
        }

        private static void CheckPanelCount()
        {
            int count = Resources.FindObjectsOfTypeAll<CoopPanel>().Count(p => p != null && p.isActiveAndEnabled);
            if (count != 1) throw new Exception("Expected one active guest panel, found " + count);
        }

        // An autosave can redirect GetActiveSaveFilePath to the original colony's next save
        // destination. Observe the actual disk loader argument and completion for this request.
        [HarmonyPatch(typeof(SaveLoader), "Load", typeof(string))]
        private static class FileLoadObservation
        {
            private static void Postfix(string __0, bool __result)
            {
                var request = waitingForLoad;
                if (request == null) return;
                request.completedLoads++;
                request.loadedFile = __0;
                request.loadSucceeded = __result;
            }
        }

        public void Update()
        {
            if (Time.realtimeSinceStartup - since > 140)
                throw new TimeoutException("Lifecycle round " + round + " stuck in " + stage + ": " + CoopSession.Status);
            if ((stage == Stage.FirstView || stage == Stage.PannedView || stage == Stage.Reply) &&
                (global::Game.Instance != null || !CoopSession.IsClient))
                throw new Exception("Guest role lost or local world started: " + CoopSession.Status);
            switch (stage)
            {
                case Stage.Menu:
                    if (!AtMenu()) return;
                    CheckPanelCount();
                    CheckOffline();
                    CheckSave();
                    if (HasPanelTexture()) throw new Exception("Stale viewport before joining.");
                    if (round == 1 && screenshot != null && (Screen.width != 1280 || Screen.height != 800))
                        Screen.SetResolution(1280, 800, FullScreenMode.Windowed);
                    CoopSession.Join(Environment.GetEnvironmentVariable("QY_SMOKE_ENDPOINT"), false);
                    firstViewFrame = -1;
                    Set(Stage.FirstView);
                    break;
                case Stage.FirstView:
                    if (CoopSession.View == null || !HasPanelTexture()) return;
                    var first = ColonyView.Decode(CoopSession.View);
                    if (first.X != 100 || first.Y != 100) throw new Exception("Stale subscription on fresh join.");
                    if (firstViewFrame < 0) { firstViewFrame = Time.frameCount; paintReady = Time.realtimeSinceStartup; }
                    if (Time.frameCount - firstViewFrame < 3) return;
                    if (round == 1 && screenshot != null)
                    {
                        if (Time.realtimeSinceStartup - paintReady < 1f) return;
                        try { ScreenCapture.CaptureScreenshot(screenshot); log("SCREENSHOT_REQUESTED " + Screen.width + "x" + Screen.height); }
                        catch (Exception e) { log("SCREENSHOT_UNAVAILABLE " + e.GetType().Name); }
                    }
                    CoopSession.Subscribe(100 + round, 102 + round);
                    Set(Stage.PannedView);
                    break;
                case Stage.PannedView:
                    if (CoopSession.View == null) return;
                    var view = ColonyView.Decode(CoopSession.View);
                    if (view.X != 100 + round || view.Y != 102 + round) return;
                    CoopSession.Command("inspect", view.X + 2, view.Y + 2);
                    Set(Stage.Reply);
                    break;
                case Stage.Reply:
                    if (!CoopSession.Status.Contains("OK Test inspect")) return;
                    CoopSession.Disconnect();
                    CheckOffline();
                    Set(Stage.Cleared);
                    break;
                case Stage.Cleared:
                    if (!AtMenu() || HasPanelTexture()) return;
                    CheckSave();
                    KPlayerPrefs.SetString("SavesDefaultToCloud", "Disabled");
                    SaveLoader.SetActiveSaveFilePath(save);
                    completedLoads = 0;
                    loadedFile = null;
                    loadSucceeded = false;
                    waitingForLoad = this;
                    Set(Stage.Loading);
                    LoadingOverlay.Load(SaveLoader.LoadScene);
                    break;
                case Stage.Loading:
                    CheckOffline();
                    if (completedLoads == 0) return;
                    if (completedLoads != 1 || !loadSucceeded || string.IsNullOrEmpty(loadedFile) ||
                        !string.Equals(Path.GetFullPath(loadedFile), save, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Lifecycle round " + round + ": expected one successful Fixture.sav load, observed " +
                            completedLoads + "; success=" + loadSucceeded + "; file=" + Path.GetFileName(loadedFile));
                    if (global::Game.Instance == null || !global::Game.Instance.GameStarted() ||
                        ClusterManager.Instance?.activeWorld == null || GameClock.Instance == null || SpeedControlScreen.Instance == null ||
                        Components.LiveMinionIdentities.Items.Count == 0) return;
                    waitingForLoad = null;
                    CheckSave();
                    log("LIFECYCLE_LOAD_VERIFIED round=" + round + " file=Fixture.sav success=True hashUnchanged=True");
                    initialClock = GameClock.Instance.GetTime();
                    Set(Stage.Advancing);
                    break;
                case Stage.Advancing:
                    CheckOffline();
                    if (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
                    if (GameClock.Instance.GetTime() - initialClock < 1f || PauseScreen.Instance == null) return;
                    log("LIFECYCLE_SOLO_ADVANCED round=" + round + " dupes=" + Components.LiveMinionIdentities.Items.Count);
                    Set(Stage.Returning);
                    PauseScreen.Instance.Activate();
                    QuitConfirmed.Invoke(PauseScreen.Instance, new object[] { false });
                    break;
                case Stage.Returning:
                    if (!AtMenu()) return;
                    CheckOffline();
                    CheckPanelCount();
                    CheckSave();
                    if (HasPanelTexture()) throw new Exception("Guest viewport survived single-player return.");
                    log("LIFECYCLE_ROUND_PASS round=" + round);
                    if (round < 3) { round++; Set(Stage.Menu); }
                    else { Set(Stage.Done); log("PASS three guest -> native solo -> frontend cycles in one Unity process"); }
                    break;
            }
        }
    }
}
