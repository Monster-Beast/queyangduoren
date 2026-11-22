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
        private readonly bool soak = Environment.GetEnvironmentVariable("QY_SMOKE_SOAK") == "1";
        private float nextSample;
        private string report;
        private bool isHost, hostReady, loading, guestStarted, finished;
        private MaterialCompetition materials;
        private TerrainNetworks terrain;
        private readonly NativeInfrastructure infrastructure = new NativeInfrastructure();
        private float start, hostTime;
        private int step;
        private string lastStatus;
        private float lastCheck;
        private bool featuresRun;
        private NativeEcology ecology;
        private NativeFoodChain foodChain;
        private GameObject printed;
        private NativeOrderChecks orderChecks;
        private NativeFacilityChecks facilityChecks;
        private FacilityRangeSeed facilityRange;
        private ColonyRangeSurvey colonySurvey;
        private NativePrintedDeath printedDeath;
        private bool facilitiesDone;
        private bool readonlyFixture, steamLobby;
        private SteamSocketPairCheck steamPair;
        private SteamDualHost dualHost;
        private SteamDualGuest dualGuest;
        private GuestLifecycle lifecycle;
        private NativeScenario native;
        private NativeLifecycle nativeLifecycle;
        private FixtureDialogs hostDialogs;
        private FixtureFrame hostFrame;
        private float hostHudSince;
        private bool hostPublished;
        private int windowWidth, windowHeight;
        private float windowDeadline, nextWindowRequest;
        private void Awake()
        {
            report = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
            string dualRole=Environment.GetEnvironmentVariable("QY_DUAL_ROLE");
            if (!string.IsNullOrEmpty(dualRole))
            {
                Application.runInBackground=true;
                start=Time.realtimeSinceStartup;
                try
                {
                    if (int.TryParse(Environment.GetEnvironmentVariable("QY_WINDOW_WIDTH"), out windowWidth) &&
                        int.TryParse(Environment.GetEnvironmentVariable("QY_WINDOW_HEIGHT"), out windowHeight))
                        Screen.SetResolution(windowWidth, windowHeight, FullScreenMode.Windowed);
                    if (dualRole=="host")
                    {
                        isHost=true; steamLobby=true; hostDialogs=new FixtureDialogs(Log);
                        dualHost=new SteamDualHost(Log,report);
                    }
                    else if (dualRole=="guest") dualGuest=new SteamDualGuest(Log);
                    else throw new InvalidOperationException("Unknown dual Steam fixture role");
                    Log("DUAL_MENU "+dualRole);
                }
                catch (Exception ex) { finished=true; Log("FAIL "+ex); }
                return;
            }
            isHost = Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "host";
            if (isHost && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("QY_NATIVE_RECORDING")) ||
                           Environment.GetEnvironmentVariable("QY_SMOKE_HOST_CAPTURE") == "1"))
            {
                hostDialogs = new FixtureDialogs(Log);
                hostFrame = new FixtureFrame(Path.Combine(Path.GetDirectoryName(report), "host-native.png"));
            }
            readonlyFixture = Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "guest-readonly";
            steamLobby = Environment.GetEnvironmentVariable("QY_SMOKE_STEAM_LOBBY") == "1";
            Application.runInBackground = true;
            start = Time.realtimeSinceStartup;
            Log("MENU " + (isHost ? "host" : "client") + " worldLoaded=" + (global::Game.Instance != null));
            if (Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "guest-native" ||
                Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "client")
                native = new NativeScenario(Log, report);
            if (Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "guest-lifecycle")
            {
                try { lifecycle = new GuestLifecycle(Log, report); }
                catch (Exception error) { finished = true; Log("FAIL " + error); }
            }
            if (Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") == "native-lifecycle")
            {
                try { nativeLifecycle = new NativeLifecycle(Log, report); }
                catch (Exception error) { finished = true; Log("FAIL " + error); }
            }
        }
        private void Log(string line) { File.AppendAllText(report, line + Environment.NewLine); }
        private void Update()
        {
            if (finished || Time.realtimeSinceStartup - start < 2) return;
            try
            {
                if ((dualHost != null || dualGuest != null) && !WindowReady()) return;
                if (dualGuest!=null) dualGuest.Tick();
                else if (nativeLifecycle != null) finished = nativeLifecycle.Update();
                else if (native != null) finished = native.Update();
                else if (lifecycle != null) { lifecycle.Update(); finished = lifecycle.Done; }
                else if (isHost) HostStep();
                else GuestStep();
            }
            catch (Exception ex)
            {
                finished = true;
                Log("FAIL " + ex);
                if (dualHost!=null) dualHost.Abort();
            }
        }
        private bool WindowReady()
        {
            if (windowWidth <= 0 || windowHeight <= 0) return true;
            if (Screen.fullScreenMode == FullScreenMode.Windowed && Screen.width == windowWidth && Screen.height == windowHeight)
            {
                if (windowDeadline >= 0)
                    Log($"DUAL_WINDOW role={(isHost ? "host" : "guest")} mode={Screen.fullScreenMode} width={Screen.width} height={Screen.height}");
                windowDeadline = -1;
                return true;
            }
            float now = Time.realtimeSinceStartup;
            if (windowDeadline <= 0) windowDeadline = now + 8f;
            if (now >= windowDeadline)
                throw new InvalidOperationException($"Window request {windowWidth}x{windowHeight} did not take effect: {Screen.width}x{Screen.height} {Screen.fullScreenMode}");
            if (now >= nextWindowRequest)
            {
                Screen.SetResolution(windowWidth, windowHeight, FullScreenMode.Windowed);
                nextWindowRequest = now + .5f;
            }
            return false;
        }
        private void HostStep()
        {
            if (!ModEntry.PatchesLoaded) throw new InvalidOperationException("Production co-op patches did not load. Check build and Harmony errors in host.log.");
            if (!loading)
            {
                if (hostDialogs != null && hostDialogs.Check()) return;
                loading = true;
                KPlayerPrefs.SetString("SavesDefaultToCloud","Disabled");
                SaveLoader.SetActiveSaveFilePath(Environment.GetEnvironmentVariable("QY_SMOKE_SAVE"));
                Log("LOAD copied Vanilla save");
                LoadingOverlay.Load(SaveLoader.LoadScene);
                return;
            }
            if (global::Game.Instance == null || ClusterManager.Instance?.activeWorld == null ||
                SpeedControlScreen.Instance == null || GameClock.Instance == null || Components.LiveMinionIdentities.Items.Count == 0) return;
            if (dualHost!=null && hostDialogs.Check(loadedVanillaHost:true)) return;
            if (Environment.GetEnvironmentVariable("QY_SURVEY_COLONY") == "1")
            {
                if (!global::Game.Instance.GameStarted()) return;
                colonySurvey = colonySurvey ?? new ColonyRangeSurvey(report, Log);
                finished = colonySurvey.Tick();
                return;
            }
            if (Environment.GetEnvironmentVariable("QY_PREPARE_FACILITY_RANGE") == "1")
            {
                if (!global::Game.Instance.GameStarted()) return;
                facilityRange = facilityRange ?? new FacilityRangeSeed(report, Log);
                finished = facilityRange.Tick();
                return;
            }
            if (!hostReady)
            {
                if (dualHost!=null && !dualHost.BeforeHosting()) return;
                CoopSession.Host(steamLobby);
                if (!CoopSession.IsHosting) throw new Exception(CoopSession.Status);
                hostReady = true;
                SpeedControlScreen.Instance.SetSpeed(soak ? 0 : 2);
                hostTime = GameClock.Instance.GetTime();
            }
            if (dualHost!=null) { finished=dualHost.Tick(); return; }
            if (!steamLobby && !hostPublished)
            {
                if (hostFrame != null && !CaptureHostHud()) return;
                if (!infrastructure.Record(Environment.GetEnvironmentVariable("QY_NATIVE_RECORDING"), Log)) return;
                hostPublished = true;
                Log("HOST_READY " + CoopSession.Endpoint);
                if (File.Exists(Path.Combine(Path.GetDirectoryName(report), "materials.flag")))
                    materials = HostFeatureChecks.BeginMaterialCompetition(Log);
                if (File.Exists(Path.Combine(Path.GetDirectoryName(report), "terrain.flag")))
                    terrain = new TerrainNetworks(Log);
            }
            if (materials != null) { finished = materials.Tick(); return; }
            if (terrain != null) { finished=terrain.Tick(); return; }
            if (steamLobby)
            {
                if (!CoopSession.IsHosting) throw new Exception(CoopSession.Status);
                if (steamPair == null)
                {
                    if (!ulong.TryParse(CoopSession.Endpoint,out ulong value)) return;
                    var lobby=new CSteamID(value);
                    if (!lobby.IsLobby() || SteamMatchmaking.GetLobbyOwner(lobby)!=SteamUser.GetSteamID() ||
                        SteamMatchmaking.GetLobbyMemberLimit(lobby)!=4 || SteamMatchmaking.GetNumLobbyMembers(lobby)!=1 ||
                        SteamMatchmaking.GetLobbyData(lobby,"queyang_coop")!="1") throw new Exception("Steam lobby metadata mismatch.");
                    Log("STEAM_HOST_READY ownerVerified=True memberLimit=4 members=1 metadata=1");
                    steamPair = new SteamSocketPairCheck(Log);
                }
                try { if (!steamPair.Poll()) return; }
                catch { steamPair.Dispose(); CoopSession.Disconnect(); throw; }
                steamPair.Dispose();
                Log("STEAM_PAIR_CLOSED");
                CoopSession.Disconnect();
                if (CoopSession.IsHosting) throw new Exception("Steam transport not closed.");
                finished = true;
                Log("PASS Steam lobby disposed without invitations");
                return;
            }
            if (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
            if (soak && Time.realtimeSinceStartup >= nextSample)
            {
                nextSample=Time.realtimeSinceStartup+60;
                using(var process=System.Diagnostics.Process.GetCurrentProcess())
                    Log("SOAK_HOST uptime="+Time.realtimeSinceStartup+" simTime="+GameClock.Instance.GetTime()+
                        " speed="+SpeedControlScreen.Instance.GetSpeed()+" workingSet="+process.WorkingSet64+
                        " managed="+GC.GetTotalMemory(false));
            }
            if (!featuresRun && File.Exists(Path.Combine(Path.GetDirectoryName(report),"features.flag")))
            {
                string recording = Environment.GetEnvironmentVariable("QY_NATIVE_RECORDING");
                if (!string.IsNullOrEmpty(recording))
                {
                    if (foodChain == null)
                    {
                        if (ecology == null) ecology = new NativeEcology(recording,Log);
                        if (!ecology.Tick()) return;
                        foodChain = new NativeFoodChain(recording,Log);
                    }
                    if (printed == null && orderChecks == null)
                    {
                        if (!foodChain.Tick()) return;
                        var d = Components.LiveMinionIdentities.Items.First(person => person != null &&
                            ClusterUtil.GetMyWorldId(person)==ClusterManager.Instance.activeWorldId);
                        Grid.CellToXY(Grid.PosToCell(d),out int x,out int y);
                        printed=NativePrintedDupe.Print(x,y);
                        return;
                    }
                    if (orderChecks == null && !NativePrintedDupe.Record(printed,recording,Log)) return;
                }
                if (facilityChecks==null)
                {
                    if (orderChecks==null) orderChecks=new NativeOrderChecks(Log);
                    if (!orderChecks.Tick()) return;
                    facilityChecks=new NativeFacilityChecks(Log);
                }
                if (!facilitiesDone) facilitiesDone=facilityChecks.Tick();
                if (!facilitiesDone) return;
                if (!string.IsNullOrEmpty(recording))
                {
                    if (printedDeath==null) printedDeath=new NativePrintedDeath(printed,recording,Log);
                    if (!printedDeath.Tick()) return;
                }
                featuresRun = true;
                Log("FEATURE native management " + NativeManagementChecks.Run());
                HostFeatureChecks.Run(Log);
            }
            if (step == 0 && Math.Abs(GameClock.Instance.GetTime() - hostTime) > 2)
            {
                step = 1;
                Log("HOST_SIM_ADVANCED");
            }
        }

        private bool CaptureHostHud()
        {
            if (hostDialogs.Check(loadedVanillaHost: true)) { hostHudSince = 0; return false; }
            if (hostFrame.Requested)
            {
                if (!hostFrame.Complete) return false;
                var lens = CameraController.Instance;
                Log("NATIVE_HOST_HUD " + Screen.width + "x" + Screen.height + " cycle=" + (1 + GameClock.Instance.GetCycle()) +
                    " camera=" + lens.transform.position + " zoom=" + lens.OrthographicSize + " dupes=" + Components.LiveMinionIdentities.Items.Count);
                return true;
            }
            if (!global::Game.Instance.GameStarted() || global::Game.Instance.IsLoading() || CameraController.Instance?.baseCamera == null ||
                PlanScreen.Instance == null || !PlanScreen.Instance.gameObject.activeInHierarchy ||
                SelectTool.Instance == null || ToolMenu.Instance == null || !ToolMenu.Instance.gameObject.activeInHierarchy ||
                PauseScreen.Instance != null && PauseScreen.Instance.IsActive()) return false;
            if (hostHudSince == 0) hostHudSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - hostHudSince < .5f) return false;
            hostFrame.Capture();
            return false;
        }
        private void GuestStep()
        {
            if (global::Game.Instance != null) throw new Exception("Client loaded a simulation.");
            if (!guestStarted)
            {
                guestStarted = true;
                CoopSession.JoinDiagnostic(Environment.GetEnvironmentVariable("QY_SMOKE_ENDPOINT"),false);
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
        private void OnDestroy() { steamPair?.Dispose(); terrain?.Dispose(); }

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
