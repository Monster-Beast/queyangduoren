using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Integration.Performance
{
    public sealed class MapBaseline : MonoBehaviour
    {
        static readonly Vector2I[] Route = Environment.GetEnvironmentVariable("QY_MAP01_DOOMED_LAB") == "1"
            ? new[] { new Vector2I(127, 196), new Vector2I(95, 180), new Vector2I(81, 180) }
            : new[] { new Vector2I(129, 193), new Vector2I(99, 166), new Vector2I(140, 174) };
        static readonly AccessTools.FieldRef<ConfirmDialogScreen, LocText> Title =
            AccessTools.FieldRefAccess<ConfirmDialogScreen, LocText>("titleText");
        static readonly AccessTools.FieldRef<ConfirmDialogScreen, LocText> Message =
            AccessTools.FieldRefAccess<ConfirmDialogScreen, LocText>("popupMessage");
        static readonly AccessTools.FieldRef<ConfirmDialogScreen, GameObject> Confirm =
            AccessTools.FieldRefAccess<ConfirmDialogScreen, GameObject>("confirmButton");

        readonly List<float> frameTimes = new List<float>();
        string report, folder, mode;
        Type session;
        bool loading, done;
        int stage = -1, nativeErrors;
        float started, sampleAt, gameTime, nextDialog;
        bool captured;
        float hostRequested;
        readonly bool captureJoin = Environment.GetEnvironmentVariable("QY_MAP01_CAPTURE") == "1";
        readonly int captureWarmup = int.TryParse(Environment.GetEnvironmentVariable("QY_MAP01_CAPTURE_WARMUP"), out var seconds) ? seconds : 0;
        bool pausedForCapture;
        float warmupStarted, warmupSimStart;

        void Awake()
        {
            report = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
            folder = Path.GetDirectoryName(report);
            mode = Environment.GetEnvironmentVariable("QY_MAP01_MODE") ?? "stock";
            Application.runInBackground = true;
            Screen.SetResolution(1280, 800, FullScreenMode.Windowed);
            Application.logMessageReceived += RecordError;
        }

        void OnDestroy() => Application.logMessageReceived -= RecordError;
        void Log(string line) => File.AppendAllText(report, line + Environment.NewLine);

        void RecordError(string condition, string stack, LogType type)
        {
            if (!loading || done || (type != LogType.Exception && type != LogType.Error)) return;
            nativeErrors++;
            if (nativeErrors <= 5)
                Log(FormattableString.Invariant($"MAP01_NATIVE_ERROR sample={stage} time={Time.realtimeSinceStartup:F2} {condition.Split('\n')[0]}"));
        }

        void Update()
        {
            if (done) return;
            try
            {
                if (Screen.width != 1280 || Screen.height != 800) return;
                if (stage < 0 && Time.realtimeSinceStartup >= nextDialog)
                {
                    nextDialog = Time.realtimeSinceStartup + .4f;
                    if (HandleDialog()) return;
                }
                if (!loading)
                {
                    var coop = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "QueyangCoop");
                    if ((coop == null) != (mode == "stock"))
                        throw new InvalidOperationException("Co-op assembly does not match requested MAP-01 mode");
                    session = coop?.GetType("Queyang.Game.CoopSession", true);
                    loading = true;
                    KPlayerPrefs.SetString("SavesDefaultToCloud", "Disabled");
                    SaveLoader.SetActiveSaveFilePath(Environment.GetEnvironmentVariable("QY_SMOKE_SAVE"));
                    Log($"MAP01_LOAD mode={mode} coopLoaded={session != null}");
                    LoadingOverlay.Load(SaveLoader.LoadScene);
                    return;
                }
                if (global::Game.Instance == null || GameClock.Instance == null ||
                    SpeedControlScreen.Instance == null || CameraController.Instance == null) return;
                if (stage < 0 && captureJoin && captureWarmup == 0 && !pausedForCapture)
                {
                    if (!SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Pause(false);
                    pausedForCapture = true;
                }
                if (!global::Game.Instance.GameStarted()) return;
                if (stage < 0)
                {
                    if (mode == "host" && !HostReady()) return;
                    if (captureJoin)
                    {
                        if (!ReadyForSnapshot()) return;
                        var method = session.GetMethod("CaptureNativeSave", BindingFlags.NonPublic | BindingFlags.Static);
                        using (var snapshot = (Stream)method.Invoke(null, null))
                            Log($"MAP01_JOIN_SNAPSHOT bytes={snapshot.Length} paused={SpeedControlScreen.Instance.IsPaused} warmup={captureWarmup}");
                    }
                    SpeedControlScreen.Instance.SetSpeed(0);
                    if (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
                    MoveTo(0);
                    return;
                }
                float now = Time.realtimeSinceStartup;
                if (captured)
                {
                    if (stage + 1 < Route.Length) MoveTo(stage + 1);
                    else
                    {
                        done = true;
                        Log(FormattableString.Invariant($"PASS MAP01_{mode.ToUpperInvariant()} evidenceCaptured=True real={now-started:F3} nativeErrors={nativeErrors}"));
                    }
                    return;
                }
                if (now < sampleAt) return;
                frameTimes.Add(Time.unscaledDeltaTime * 1000);
                if (now - sampleAt < 12) return;
                float elapsed = now - sampleAt;
                frameTimes.Sort();
                Log(FormattableString.Invariant($"MAP01_STAGE index={stage} real={elapsed:F3} sim={GameClock.Instance.GetTime()-gameTime:F3} speed={SpeedControlScreen.Instance.GetSpeed()} paused={SpeedControlScreen.Instance.IsPaused} frames={frameTimes.Count} p50ms={Percentile(.50f):F2} p95ms={Percentile(.95f):F2} p99ms={Percentile(.99f):F2} errors={nativeErrors}"));
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, "map01-" + mode + "-" + stage + ".png"));
                captured = true;
            }
            catch (Exception error)
            {
                done = true;
                Log("FAIL " + error);
            }
        }

        void MoveTo(int index)
        {
            stage = index;
            captured = false;
            frameTimes.Clear();
            float now = Time.realtimeSinceStartup;
            if (stage == 0) started = now;
            // Include the view change in the sample, consistently for each run.
            gameTime = GameClock.Instance.GetTime();
            sampleAt = now;
            var camera = CameraController.Instance;
            var target = Route[index];
            camera.SetTargetPos(new Vector3(target.x + .5f, target.y + .5f, camera.transform.position.z), 12, false);
        }

        float Percentile(float fraction) => frameTimes[Mathf.CeilToInt(frameTimes.Count * fraction) - 1];

        bool ReadyForSnapshot()
        {
            if (captureWarmup == 0) return true;
            var speed = SpeedControlScreen.Instance;
            if (warmupStarted == 0)
            {
                warmupStarted = Time.realtimeSinceStartup;
                warmupSimStart = GameClock.Instance.GetTime();
                speed.SetSpeed(0);
                if (speed.IsPaused) speed.Unpause(false);
            }
            if (GameClock.Instance.GetTime() - warmupSimStart < captureWarmup)
            {
                if (Time.realtimeSinceStartup - warmupStarted > 90)
                    throw new TimeoutException("MAP-01 simulation did not reach the requested snapshot warmup");
                return false;
            }
            if (!speed.IsPaused) speed.Pause(false);
            return true;
        }

        bool HostReady()
        {
            if (hostRequested == 0)
            {
                hostRequested = Time.realtimeSinceStartup;
                session.GetMethod("Host", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { true });
            }
            if (ulong.TryParse((string)session.GetProperty("Endpoint").GetValue(null), out _)) return true;
            if (!(bool)session.GetProperty("IsHosting").GetValue(null))
                throw new InvalidOperationException("MAP-01 could not host: " + session.GetProperty("Status").GetValue(null));
            if (Time.realtimeSinceStartup - hostRequested > 35)
                throw new TimeoutException("MAP-01 empty Steam room was not created");
            return false;
        }

        bool HandleDialog()
        {
            var dialog = UnityEngine.Object.FindObjectsByType<ConfirmDialogScreen>(FindObjectsSortMode.None)
                .FirstOrDefault(item => item.IsActive() && item.gameObject.activeInHierarchy);
            if (dialog == null) return false;
            string title = Title(dialog).text.Trim();
            string message = Message(dialog).text.Trim().Replace("\r\n", "\n");
            const string observer = "Solo performance QA";
            const string coop = "Queyang Co-op (Development)";
            var lines = message.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool saveNotice = loading && title == "MOD DIFFERENCES" &&
                lines.All(line => line == "Save game mods differ from currently active mods:" ||
                    line == "NEWLY ENABLED: " + observer || line == "This mod needs to be disabled" ||
                    line == "NOT FOUND: " + coop || line == "This mod isn't installed");
            bool installNotice = !loading && title == "MOD EVENTS" &&
                lines.Any(line => line == "INSTALLED: " + observer || mode != "stock" && line == "INSTALLED: " + coop) &&
                !lines.Any(line => line.StartsWith("NOT FOUND:", StringComparison.Ordinal) ||
                    line.StartsWith("REQUIRES RESTART:", StringComparison.Ordinal));
            if ((!saveNotice && !installNotice) || Confirm(dialog)?.GetComponent<KButton>() == null)
                throw new InvalidOperationException("Unexpected MAP-01 dialog: " + title + " / " + message);
            Confirm(dialog).GetComponent<KButton>().SignalClick(KKeyCode.Mouse0);
            return true;
        }
    }
}
