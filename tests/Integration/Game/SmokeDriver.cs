using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;
using Steamworks;

// This DLL is included only in the isolated test copy, alongside the unmodified package assemblies.
namespace Queyang.Integration
{
    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    internal static class DriverBootstrap
    {
        private static SmokeDriver driver;
        private static void Postfix()
        {
            var expected = Environment.GetEnvironmentVariable("QY_SMOKE_ROOT");
            if (string.IsNullOrEmpty(expected) || !string.Equals(expected, Util.GetKleiRootPath(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Smoke driver requires its own isolated game profile.");
            if (driver != null) return;
            var go = new GameObject("Coop smoke driver");
            UnityEngine.Object.DontDestroyOnLoad(go);
            driver = go.AddComponent<SmokeDriver>();
        }
    }

    public sealed class SmokeDriver : MonoBehaviour
    {
        private string report;
        private bool isHost, hostReady, loading, guestStarted, finished;
        private float start, hostTime;
        private int step;
        private string lastStatus;
        private float lastCheck;
        private bool featuresRun;
        private bool readonlyFixture, steamLobby;
        private void Awake()
        {
            report = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
            isHost = Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "host";
            readonlyFixture = Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "guest-readonly";
            steamLobby = Environment.GetEnvironmentVariable("QY_SMOKE_STEAM_LOBBY") == "1";
            Application.runInBackground = true;
            start = Time.realtimeSinceStartup;
            Log("MENU " + (isHost ? "host" : "client") + " worldLoaded=" + (global::Game.Instance != null));
        }
        private void Log(string line) { File.AppendAllText(report, line + Environment.NewLine); }
        private void Update()
        {
            if (finished || Time.realtimeSinceStartup - start < 2) return;
            try { if (isHost) HostStep(); else GuestStep(); }
            catch (Exception ex) { finished = true; Log("FAIL " + ex); }
        }
        private void HostStep()
        {
            if (!loading)
            {
                loading = true;
                KPlayerPrefs.SetString("SavesDefaultToCloud","Disabled");
                SaveLoader.SetActiveSaveFilePath(Environment.GetEnvironmentVariable("QY_SMOKE_SAVE"));
                Log("LOAD copied Vanilla save");
                LoadingOverlay.Load(SaveLoader.LoadScene);
                return;
            }
            if (global::Game.Instance == null || ClusterManager.Instance?.activeWorld == null ||
                SpeedControlScreen.Instance == null || GameClock.Instance == null || Components.LiveMinionIdentities.Items.Count == 0) return;
            if (!hostReady)
            {
                CoopSession.Host(steamLobby);
                if (!CoopSession.IsHosting) throw new Exception(CoopSession.Status);
                hostReady = true;
                SpeedControlScreen.Instance.SetSpeed(2);
                hostTime = GameClock.Instance.GetTime();
                if (!steamLobby) Log("HOST_READY " + CoopSession.Endpoint);
            }
            if (steamLobby)
            {
                if (!CoopSession.IsHosting) throw new Exception(CoopSession.Status);
                if (!ulong.TryParse(CoopSession.Endpoint,out ulong value)) return;
                var lobby=new CSteamID(value);
                if (!lobby.IsLobby() || SteamMatchmaking.GetLobbyOwner(lobby)!=SteamUser.GetSteamID() ||
                    SteamMatchmaking.GetLobbyMemberLimit(lobby)!=4 || SteamMatchmaking.GetNumLobbyMembers(lobby)!=1 ||
                    SteamMatchmaking.GetLobbyData(lobby,"queyang_coop")!="1") throw new Exception("Steam lobby metadata mismatch.");
                Log("STEAM_HOST_READY ownerVerified=True memberLimit=4 members=1 metadata=1");
                CoopSession.Disconnect();
                if (CoopSession.IsHosting) throw new Exception("Steam transport not closed.");
                finished = true;
                Log("PASS Steam lobby disposed without invitations");
                return;
            }
            if (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
            if (!featuresRun && File.Exists(Path.Combine(Path.GetDirectoryName(report),"features.flag")))
            {
                featuresRun = true;
                HostFeatureChecks.Run(Log);
            }
            if (step == 0 && Math.Abs(GameClock.Instance.GetTime() - hostTime) > 2)
            {
                step = 1;
                Log("HOST_SIM_ADVANCED");
            }
        }
        private void GuestStep()
        {
            if (global::Game.Instance != null) throw new Exception("Client loaded a simulation.");
            if (!guestStarted)
            {
                guestStarted = true;
                CoopSession.Join(Environment.GetEnvironmentVariable("QY_SMOKE_ENDPOINT"),false);
                Log("JOIN " + CoopSession.Status);
            }
            if (CoopSession.Status != lastStatus)
            {
                lastStatus = CoopSession.Status;
                Log("STATUS " + lastStatus);
            }
            if (!CoopSession.IsClient) throw new Exception(CoopSession.Status);
            if (CoopSession.View == null || Time.realtimeSinceStartup - lastCheck < 0.12f) return;
            lastCheck = Time.realtimeSinceStartup;
            if (readonlyFixture)
            {
                ReadonlyStep();
                return;
            }
            if (scenario == null) scenario = new GuestScenario(Log,CoopSession.Command);
            scenario.Update(CoopSession.View,lastStatus);
            if (scenario.Done)
            {
                finished = true;
                Log("PASS guestWorldLoaded=" + (global::Game.Instance != null));
                CoopSession.Disconnect();
            }
        }
        private GuestScenario scenario;

        private void ReadonlyStep()
        {
            var current = ColonyView.Decode(CoopSession.View);
            if (step == 0 && current.X==100 && current.Y==100)
            {
                // Only the real guest panel creates this texture from its received viewport.
                if (!Resources.FindObjectsOfTypeAll<Texture2D>().Any(t => t.name=="Co-op viewport")) return;
                Log("GUEST_TEXTURE_READY cells=" + current.Cells.Length);
                step=1;
                CoopSession.Subscribe(108,102);
            }
            else if (step==1 && current.X==108 && current.Y==102)
            {
                Log("GUEST_PAN_READY origin=108,102");
                step=2;
                CoopSession.Command("inspect",110,104);
            }
            else if (step==2 && CoopSession.Status.Contains("OK Test inspect"))
            {
                CoopSession.Disconnect();
                if (global::Game.Instance != null || CoopSession.IsClient) throw new Exception("Guest did not exit cleanly.");
                finished=true;
                Log("PASS genuine Unity guest; synthetic fixture host; worldLoaded=False; disconnect successful");
            }
        }
    }
}
