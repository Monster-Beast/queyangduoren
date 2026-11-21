using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Integration.Performance
{
    public sealed class SoloPerfMod : KMod.UserMod2 { }

    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    internal static class Bootstrap
    {
        private static SoloSample driver;
        private static void Postfix()
        {
            if (driver != null) return;
            string root = Environment.GetEnvironmentVariable("QY_SMOKE_ROOT");
            if (string.IsNullOrEmpty(root) || Util.GetKleiRootPath() != root ||
                Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") != "solo-performance")
                throw new InvalidOperationException("Solo performance check requires an isolated profile");
            var obj = new GameObject("Solo sample");
            UnityEngine.Object.DontDestroyOnLoad(obj);
            driver = obj.AddComponent<SoloSample>();
        }
    }

    public sealed class SoloSample : MonoBehaviour
    {
        private string report;
        private bool loading, ready, done;
        private int samples;
        private float next;

        private void Awake()
        {
            Application.runInBackground = true;
            report = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
        }
        private void Log(string text) => File.AppendAllText(report, text + Environment.NewLine);
        private void Update()
        {
            if (done) return;
            try
            {
                if (!loading)
                {
                    if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "QueyangCoop"))
                        throw new InvalidOperationException("Co-op is loaded during the pure single-player comparison");
                    loading = true;
                    KPlayerPrefs.SetString("SavesDefaultToCloud", "Disabled");
                    SaveLoader.SetActiveSaveFilePath(Environment.GetEnvironmentVariable("QY_SMOKE_SAVE"));
                    Log("SOLO_LOAD copied save");
                    LoadingOverlay.Load(SaveLoader.LoadScene);
                    return;
                }
                if (global::Game.Instance == null || ClusterManager.Instance?.activeWorld == null ||
                    SpeedControlScreen.Instance == null || GameClock.Instance == null ||
                    Components.LiveMinionIdentities.Count == 0) return;
                var speed = SpeedControlScreen.Instance;
                if (!ready)
                {
                    speed.SetSpeed(0);
                    ready = true;
                }
                if (speed.GetSpeed() != 0) throw new InvalidOperationException("Test speed changed during measurement");
                if (speed.IsPaused) speed.Unpause(false);
                float uptime = Time.realtimeSinceStartup;
                if (uptime < next) return;
                next = uptime + 60;
                Log(string.Format(CultureInfo.InvariantCulture, "SOLO_HOST uptime={0:R} simTime={1:R} speed={2}",
                    uptime, GameClock.Instance.GetTime(), speed.GetSpeed()));
                if (++samples == 12)
                {
                    done = true;
                    Log("PASS solo 60-second warm-up and 10-minute timed sample");
                }
            }
            catch (Exception e) { done = true; Log("FAIL " + e); }
        }
    }
}
