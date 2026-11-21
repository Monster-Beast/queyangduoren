using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Integration.Persistence
{
    // Installed alone in a fresh, temporary dev profile. This assembly has no reference to either co-op DLL.
    public sealed class ReadbackMod : KMod.UserMod2 { }

    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    internal static class Bootstrap
    {
        private static ReadbackDriver driver;
        private static void Postfix()
        {
            string root = Environment.GetEnvironmentVariable("QY_SMOKE_ROOT");
            if (string.IsNullOrEmpty(root) || root != Util.GetKleiRootPath() ||
                Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") != "readback")
                throw new InvalidOperationException("Readback requires its isolated profile.");
            if (driver != null) return;
            var go = new GameObject("Native save QA");
            UnityEngine.Object.DontDestroyOnLoad(go);
            driver = go.AddComponent<ReadbackDriver>();
        }
    }

    public sealed class ReadbackDriver : MonoBehaviour
    {
        private bool loading, done;
        private string report, expectedMarker, save;
        private void Awake()
        {
            Application.runInBackground = true;
            report = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
            save = Environment.GetEnvironmentVariable("QY_SMOKE_SAVE");
            expectedMarker = File.ReadAllText(Path.Combine(Path.GetDirectoryName(save), "marker.txt")).Trim();
        }
        private void Update()
        {
            if (done) return;
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "QueyangCoop" ||
                    a.GetName().Name == "Queyang.Core")) throw new Exception("Production mod was loaded.");
                if (!loading)
                {
                    loading = true;
                    KPlayerPrefs.SetString("SavesDefaultToCloud", "Disabled");
                    SaveLoader.SetActiveSaveFilePath(save);
                    File.AppendAllText(report, "REOPEN native load with co-op uninstalled\n");
                    LoadingOverlay.Load(SaveLoader.LoadScene);
                    return;
                }
                if (Game.Instance == null || ClusterManager.Instance?.activeWorld == null ||
                    SaveLoader.Instance == null || !SaveLoader.Instance.loadedFromSave ||
                    Components.LiveMinionIdentities.Count == 0 || ScheduleManager.Instance == null) return;
                if (!ScheduleManager.Instance.GetSchedules().Any(s => s.name == expectedMarker))
                    throw new Exception("Host-generated marker missing after re-open.");
                done = true;
                File.AppendAllText(report, "PASS native host save re-opened; production co-op assemblies absent; schedule marker verified\n");
            }
            catch (Exception e) { done = true; File.AppendAllText(report, "FAIL " + e + "\n"); }
        }
    }
}
