using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Queyang.Integration
{
    // The peer replays observations previously made by a separate real hosting game.
    internal sealed class NativeScenario
    {
        enum Stage { Join, Load, Pan, Overlay, Details, Screenshot, Order, Completion, ProxySelect, ProxyOrder, ProxyCancel, ProxyDone, Exit }
        readonly Action<string> log;
        readonly string screenshot, recording;
        Stage stage;
        int target;
        float until, nextDialogCheck;
        Vector3 originalCamera;
        KSelectable dupe;
        HashedString originalOverlay;
        bool restarting;
        int ladderCell;
        NativeProxySettings proxy;
        string engineException;

        public NativeScenario(Action<string> log, string report)
        {
            this.log = log;
            screenshot = Path.Combine(Path.GetDirectoryName(report), "native-guest.png");
            recording = Environment.GetEnvironmentVariable("QY_NATIVE_RECORDING");
            Application.logMessageReceived += OnUnityLog;
        }

        void OnUnityLog(string message, string stack, LogType level)
        {
            if (level == LogType.Exception && engineException == null) engineException = message + "\n" + stack;
        }

        // ConfirmDialogScreen stores these LocText references privately. Read the
        // rendered text so a restart request or mixed mod errors cannot match.
        static string DialogText(ConfirmDialogScreen dialog, string field)
        {
            object label = AccessTools.Field(typeof(ConfirmDialogScreen), field).GetValue(dialog);
            return ((string)AccessTools.Property(label.GetType(), "text").GetValue(label, null)).Trim();
        }

        bool CheckDialogs()
        {
            if (UnityEngine.Object.FindAnyObjectByType<ReportErrorDialog>() != null)
                throw new Exception("ONI opened its crash reporting dialog");
            foreach (var screen in UnityEngine.Object.FindObjectsByType<KScreen>(FindObjectsSortMode.None))
            {
                if (!screen.gameObject.activeInHierarchy || !screen.IsActive()) continue;
                if (screen is ConfirmDialogScreen dialog)
                {
                    string title = DialogText(dialog, "titleText");
                    string message = DialogText(dialog, "popupMessage");
                    const string fixture = "Queyang Co-op (Development)";
                    string upgrade = STRINGS.UI.FRONTEND.MOD_EVENTS.VERSION_UPDATE + ": " + fixture + "\n" +
                        STRINGS.UI.FRONTEND.MOD_EVENTS.REQUIRES_RESTART + ": " + fixture + "\n";
                    string normalized = message.Replace("\r\n", "\n");
                    if (title == STRINGS.UI.FRONTEND.MOD_DIALOGS.MOD_EVENTS.TITLE.text &&
                        (normalized == string.Format(STRINGS.UI.FRONTEND.MOD_DIALOGS.RESTART.MESSAGE.text, upgrade).Trim() ||
                         normalized == string.Format(STRINGS.UI.FRONTEND.MOD_DIALOGS.RESTART.DEV_MESSAGE.text, upgrade).Trim()))
                    {
                        restarting = true;
                        if (CoopSession.IsClient) CoopSession.Disconnect();
                        log("NATIVE_RESTART_REQUIRED");
                        Application.Quit();
                        return true;
                    }
                    string installed = STRINGS.UI.FRONTEND.MOD_EVENTS.INSTALLED + ": ";
                    string template = STRINGS.UI.FRONTEND.MOD_DIALOGS.MOD_EVENTS.DEV_MESSAGE.text;
                    string footer = template.StartsWith("{0}", StringComparison.Ordinal) ? template.Substring(3).Trim() : "";
                    string events = message;
                    int lastLine = message.LastIndexOf('\n');
                    if (lastLine >= 0 && footer.Length > 0 && message.Substring(lastLine + 1).Trim() == footer)
                        events = message.Substring(0, lastLine);
                    string[] lines = events.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var buttonObject = (GameObject)AccessTools.Field(typeof(ConfirmDialogScreen), "confirmButton").GetValue(dialog);
                    var buttons = dialog.GetComponentsInChildren<KButton>().Where(b => b.gameObject.activeInHierarchy).ToArray();
                    if (title != STRINGS.UI.FRONTEND.MOD_DIALOGS.MOD_EVENTS.TITLE.text || lines.Length == 0 ||
                        !lines.All(s => s.StartsWith(installed, StringComparison.Ordinal) && s.Length > installed.Length) ||
                        buttons.Length != 1 || buttons[0] != buttonObject.GetComponent<KButton>())
                        throw new Exception("Unexpected confirmation dialog: " + title + " / " + message);
                    buttons[0].SignalClick(KKeyCode.Mouse0);
                    log("NATIVE_INSTALL_NOTIFICATION_CONFIRMED count=" + lines.Length);
                    return true;
                }
                if (screen is CustomizableDialogScreen || screen is InfoDialogScreen)
                    throw new Exception("Unexpected game dialog: " + screen.GetType().Name + " / " + screen.name);
            }
            return false;
        }

        public bool Update()
        {
            if (engineException != null) throw new InvalidOperationException("Unity logged an exception during native play or teardown: " + engineException);
            if (restarting) return false;
            if (Time.unscaledTime >= nextDialogCheck || stage == Stage.Join || stage == Stage.Details)
            {
                nextDialogCheck = Time.unscaledTime + .25f;
                if (CheckDialogs()) return false;
            }
            if (stage != Stage.Join && stage != Stage.Exit && !CoopSession.IsNativeClient)
                throw new Exception("Native join failed: " + CoopSession.Status);
            switch (stage)
            {
                case Stage.Join:
                    CoopSession.Join(Environment.GetEnvironmentVariable("QY_SMOKE_ENDPOINT"), false);
                    stage = Stage.Load;
                    break;
                case Stage.Load:
                    if (!NativeGuestScene.Ready || CoopSession.NativeView == null || CameraController.Instance == null ||
                        DigTool.Instance == null) break;
                    target = ChooseDigCell(CoopSession.NativeView);
                    if (!Grid.Solid[target] || SelectTool.Instance == null || PlanScreen.Instance == null)
                        throw new Exception("Missing pre-dig solid, native selection tool or planning screen");
                    log("NATIVE_WORLD_READY dupes=" + Components.LiveMinionIdentities.Items.Count +
                        " camera=True nativeTools=True initialElement=" + Grid.Element[target].id);
                    originalCamera = CameraController.Instance.transform.position;
                    CameraController.Instance.SetTargetPos(originalCamera + new Vector3(2,0,0),
                        Mathf.Max(5f,CameraController.Instance.OrthographicSize), false);
                    until = Time.unscaledTime + 1;
                    stage = Stage.Pan;
                    break;
                case Stage.Pan:
                    if (Time.unscaledTime <= until || CoopSession.View == null) break;
                    float pan = CameraController.Instance.transform.position.x - originalCamera.x;
                    if (Mathf.Abs(pan) < .5f) break;
                    log("NATIVE_CAMERA_PANNED deltaX=" + pan);
                    originalOverlay = OverlayScreen.Instance.GetMode();
                    OverlayScreen.Instance.ToggleOverlay(OverlayModes.Temperature.ID, true);
                    stage = Stage.Overlay;
                    break;
                case Stage.Overlay:
                    if (OverlayScreen.Instance.GetMode() != OverlayModes.Temperature.ID)
                        throw new Exception("Vanilla temperature overlay did not activate");
                    log("NATIVE_TEMPERATURE_OVERLAY_ACTIVE");
                    OverlayScreen.Instance.ToggleOverlay(originalOverlay, true);
                    var nearest = Components.LiveMinionIdentities.Items
                        .Where(d => d != null && ClusterUtil.GetMyWorldId(d) == ClusterManager.Instance.activeWorldId)
                        .OrderBy(d => (d.transform.position - CameraController.Instance.transform.position).sqrMagnitude)
                        .FirstOrDefault();
                    if (nearest == null) throw new Exception("No duplicant in guest world");
                    dupe = nearest.GetComponent<KSelectable>();
                    PlayerController.Instance.ActivateTool(SelectTool.Instance);
                    SelectTool.Instance.Select(dupe, false);
                    until = Time.unscaledTime + 5;
                    stage = Stage.Details;
                    break;
                case Stage.Details:
                    if (!DetailsScreen.Instance.IsActive() || DetailsScreen.Instance.target != dupe.gameObject)
                    {
                        if (Time.unscaledTime > until) throw new Exception("Duplicant details sidebar did not open");
                        break;
                    }
                    log("NATIVE_DUPLICANT_DETAILS target=" + dupe.GetProperName());
                    ScreenCapture.CaptureScreenshot(screenshot);
                    stage = Stage.Screenshot;
                    break;
                case Stage.Screenshot:
                    if (!File.Exists(screenshot) || CoopSession.View == null) break;
                    PlayerController.Instance.ActivateTool(DigTool.Instance);
                    Vector3 pos = Grid.CellToPosCCC(target, Grid.SceneLayer.Move);
                    DigTool.Instance.OnLeftClickDown(pos);
                    DigTool.Instance.OnLeftClickUp(pos);
                    if (Diggable.GetDiggable(target) != null) throw new Exception("Guest locally created an order before confirmation");
                    log("NATIVE_DIG_TOOL_SENT " + target);
                    stage = Stage.Order;
                    break;
                case Stage.Order:
                    if (!NativeWorldMirror.IsDigVisible(target)) break;
                    log("NATIVE_HOST_ORDER_VISIBLE");
                    stage = Stage.Completion;
                    break;
                case Stage.Completion:
                    if (Grid.Solid[target] || NativeWorldMirror.IsDigVisible(target)) break;
                    log("NATIVE_DIG_FINISHED element=" + Grid.Element[target].id + " mass=" + Grid.Mass[target] +
                        " temperature=" + Grid.Temperature[target]);
                    string file = string.IsNullOrEmpty(recording) ? null : Path.Combine(recording,"ladder-menu.bin");
                    if (file != null && File.Exists(file))
                    {
                        using (var r = new BinaryReader(File.OpenRead(file))) ladderCell = Grid.XYToCell(r.ReadInt32(),r.ReadInt32());
                        until = Time.unscaledTime + 15;
                        stage = Stage.ProxySelect;
                    }
                    else Leave();
                    break;
                case Stage.ProxySelect:
                    proxy = UnityEngine.Object.FindObjectsByType<NativeProxySettings>(FindObjectsSortMode.None)
                        .FirstOrDefault(p => Grid.PosToCell(p.gameObject) == ladderCell);
                    if (proxy == null) { CheckProxyTimeout(); break; }
                    if (proxy.GetComponent<Building>() != null || proxy.GetComponent<Workable>() != null)
                        throw new Exception("Replicated ladder spawned production components");
                    PlayerController.Instance.ActivateTool(SelectTool.Instance);
                    SelectTool.Instance.Select(proxy.GetComponent<KSelectable>(),false);
                    stage = Stage.ProxyOrder;
                    break;
                case Stage.ProxyOrder:
                    if (!ProxyButton("指派拆除建筑", true)) { CheckProxyTimeout(); break; }
                    stage = Stage.ProxyCancel;
                    break;
                case Stage.ProxyCancel:
                    if (!ProxyButton("取消拆除建筑", true)) { CheckProxyTimeout(); break; }
                    stage = Stage.ProxyDone;
                    break;
                case Stage.ProxyDone:
                    if (!ProxyButton("指派拆除建筑", false)) { CheckProxyTimeout(); break; }
                    log("NATIVE_PROXY_LADDER_STOCK_MENU hostConfirmedOrder=True hostConfirmedCancel=True");
                    Leave();
                    break;
                case Stage.Exit:
                    if (global::Game.Instance != null || NativeGuestScene.Active || MainMenu.Instance == null || !MainMenu.Instance.IsActive()) break;
                    Application.logMessageReceived -= OnUnityLog;
                    log("PASS native game client loaded the colony, sent a stock dig order and returned to menu");
                    return true;
            }
            return false;
        }

        void CheckProxyTimeout()
        {
            if (Time.unscaledTime > until) throw new TimeoutException("Native projected ladder's stock action menu failed at " + stage);
        }
        bool ProxyButton(string label, bool click)
        {
            if (!DetailsScreen.Instance.IsActive() || DetailsScreen.Instance.target != proxy.gameObject) return false;
            foreach (var menu in UnityEngine.Object.FindObjectsByType<UserMenuScreen>(FindObjectsSortMode.None))
            {
                var infos = (List<KIconButtonMenu.ButtonInfo>)AccessTools.Field(typeof(UserMenuScreen), "buttonInfos").GetValue(menu);
                var info = infos?.FirstOrDefault(row => row.text == label && row.buttonGo != null && row.buttonGo.activeInHierarchy);
                if (info == null) continue;
                if (!click) return true;
                ClickStockControl(info);
                return true;
            }
            return false;
        }
        void ClickStockControl(KIconButtonMenu.ButtonInfo info)
        {
            var go = info.buttonGo;
            var button = go.GetComponent<KButton>();
            var toggle = go.GetComponent<KToggle>();
            var basic = go.GetComponent<KBasicToggle>();
            if (button == null && toggle == null && basic == null)
                throw new Exception("The stock action template has no supported click control");
            if (!info.isInteractable || (button != null && !button.isInteractable) || (toggle != null && !toggle.IsInteractable()))
                throw new Exception("Stock action is not interactable: " + info.text);
            // The screenshot runner starts the game with its OS window hidden. Klei's mouse
            // handlers discard events while hasFocus is false. Reproduce focused player input
            // only during this event dispatch, without bringing the window to the foreground.
            var focus = AccessTools.DeclaredField(typeof(KInputManager), "hasFocus");
            bool wasFocused = (bool)focus.GetValue(null);
            Vector2 screenPos = go.transform.position;
            var pointer = new PointerEventData(UnityEngine.EventSystems.EventSystem.current) {
                button = PointerEventData.InputButton.Left, clickCount = 1, clickTime = Time.unscaledTime,
                eligibleForClick = true, position = screenPos, pressPosition = screenPos,
                pointerPress = go, rawPointerPress = go, pointerEnter = go
            };
            bool clicked = false;
            void Observed() { clicked = true; log("NATIVE_STOCK_UI_CLICKED " + info.text); }
            if (button != null) button.onClick += Observed;
            if (toggle != null) toggle.onClick += Observed;
            if (basic != null) basic.onClick += Observed;
            try
            {
                if (!wasFocused) focus.SetValue(null,true);
                if (!KInputManager.isFocused) throw new Exception("Stock input is captured by another tool");
                log("NATIVE_STOCK_POINTER " + info.text + " hasFocus=" + wasFocused + " control=" +
                    (button != null ? "KButton" : toggle != null ? "KToggle" : "KBasicToggle"));
                ExecuteEvents.Execute(go,pointer,ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(go,pointer,ExecuteEvents.pointerUpHandler);
                if (!ExecuteEvents.Execute(go,pointer,ExecuteEvents.pointerClickHandler))
                    throw new Exception("No pointer handler on stock action");
                if (!clicked) throw new Exception("Pointer event did not activate the rendered stock control: " + info.text);
            }
            finally
            {
                if (!wasFocused) focus.SetValue(null,false);
                if (button != null) button.onClick -= Observed;
                if (toggle != null) toggle.onClick -= Observed;
                if (basic != null) basic.onClick -= Observed;
            }
        }
        void Leave()
        {
            log("NATIVE_COUNTERS " + GuestDiagnostics.Summary);
            if (!GuestDiagnostics.Summary.StartsWith("simStep=0 findChore=0 setChore=0 saveAttempts=0 worldStarts=0 "))
                throw new Exception("Native replica performed guest-side simulation, work, or saving");
            CoopSession.Disconnect();
            stage = Stage.Exit;
        }

        int ChooseDigCell(ColonyView view)
        {
            if (!string.IsNullOrEmpty(recording))
            {
                var location = File.ReadAllText(Path.Combine(recording,"dig.txt")).Trim().Split(',');
                return Grid.XYToCell(int.Parse(location[0]),int.Parse(location[1]));
            }
            var candidates = Enumerable.Range(ColonyView.Width,view.Cells.Length-2*ColonyView.Width)
                .Where(i => i % ColonyView.Width > 0 && i % ColonyView.Width < ColonyView.Width-1 &&
                    (view.Cells[i].Flags & 63) == 3 &&
                    new[] { -1, 1, ColonyView.Width }.Any(d => (view.Cells[i+d].Flags & 3) == 1))
                .Select(i => Grid.XYToCell(view.X+i%ColonyView.Width,view.Y+i/ColonyView.Width))
                .Where(c => Diggable.IsDiggable(c) && !Diggable.Undiggable(Grid.Element[c]));
            return candidates.OrderBy(c => Components.LiveMinionIdentities.Items.Min(d =>
                (Grid.CellToPosCCC(c,Grid.SceneLayer.Move)-d.transform.position).sqrMagnitude)).First();
        }
    }
}
