using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Queyang.Game
{
    public sealed class CoopPanel : MonoBehaviour
    {
        private static readonly string[][] Actions = {
            new[] { "inspect", "building", "dig", "sweep", "mop", "harvest", "cancel", "deconstruct", "priority5", "priority9" },
            new[] { "ladder", "tile", "door", "storage", "oxygen", "generator", "wire", "gaspipe", "liquidpipe" }
        };
        private static readonly string[][] Labels = {
            new[] { "检查格子", "建筑设置", "挖掘", "清扫", "擦拭液体", "收获", "取消订单", "拆除建筑", "优先级 5", "优先级 9" },
            new[] { "梯子", "砖块", "门", "储物箱", "供氧设备", "发电机", "电线", "气管", "液管" }
        };
        private static readonly string[] Groups = { "工具", "建造" };
        private static readonly string[] Overlays = { "地形", "温度", "氧气", "电力", "气管", "液管", "自动化" };
        private const int MaxBatch = 256;

        private struct PendingCommand { public string Action; public int X, Y; }
        private readonly Queue<PendingCommand> pending = new Queue<PendingCommand>();
        private readonly ColonyMap map = new ColonyMap();
        private readonly ColonyMotion motion = new ColonyMotion();
        private readonly BuildingPanel buildings = new BuildingPanel();
        private readonly BuildOptionsPanel buildOptions = new BuildOptionsPanel();
        private ColonyView view;
        private byte[] received;
        private bool expanded, launchChecked, hadClient;
        private IDisposable invites;
        private float nextInviteCheck, nextSend;
        private int group, tool, overlay, zoom = 1;
        private bool showResult;
        private Vector2 resultScroll;
        private string lan = "127.0.0.1:24714", lobby = "", localMessage = "";
        private int selectedX = -1, selectedY = -1, dragX, dragY, dragOriginX, dragOriginY;
        private bool dragging;
        private Canvas blocker;
        private GUISkin panelSkin;
        private Font panelFont;
        private RectTransform blockerRect;
        private Rect screenBlock;
        private bool gateInput;
        internal static bool BlockMenuKeys { get; private set; }
        internal static int PreventedGuestSteps { get; private set; }

        private static bool MenuReady() => global::Game.Instance == null &&
            MainMenu.Instance != null && MainMenu.Instance.IsActive();
        private static bool HostMenu() => global::Game.Instance != null &&
            global::Game.Instance.GameStarted() && PauseScreen.Instance != null && PauseScreen.Instance.IsActive();

        private void OnEnable()
        {
            CoopSession.CommandResult += buildings.Receive;
            CoopSession.CommandResult += buildOptions.Receive;
        }
        private void OnDisable()
        {
            CoopSession.CommandResult -= buildings.Receive;
            CoopSession.CommandResult -= buildOptions.Receive;
        }

        private void Update()
        {
            bool menu = MenuReady();
            UpdateInvites(menu);
            if (CoopSession.IsClient && (MainMenu.Instance == null || global::Game.Instance != null) ||
                CoopSession.IsHosting && global::Game.Instance == null)
            {
                CoopSession.Disconnect();
                localMessage = "场景已切换，会话已结束。";
            }
            CoopSession.Pump();
            if (CoopSession.IsClient)
            {
                if (!hadClient)
                {
                    PreventedGuestSteps = 0;
                    expanded = true;
                    localMessage = "";
                }
                var next = CoopSession.View;
                if (!ReferenceEquals(next, received))
                {
                    received = next;
                    if (next == null) ClearReplica();
                    else
                    {
                        try
                        {
                            var replacement = ColonyView.Decode(next);
                            map.Paint(replacement, (ColonyMap.Overlay)overlay);
                            motion.Apply(replacement, Time.unscaledTime);
                            view = replacement;
                            if (buildings.IsOpen && !buildings.TargetExists(view))
                            {
                                buildings.Close();
                                localMessage = "所选建筑已变更或移除。";
                            }
                        }
                        catch (Exception e) when (e is InvalidDataException || e is IOException)
                        {
                            CoopSession.Disconnect();
                            localMessage = "地图数据无效，会话已结束。";
                            ClearReplica();
                        }
                    }
                }
                if (view != null && pending.Count != 0 && Time.unscaledTime >= nextSend)
                {
                    var c = pending.Dequeue();
                    nextSend = Time.unscaledTime + 0.12f;
                    CoopSession.Command(c.Action, c.X, c.Y);
                }
            }
            if (!CoopSession.IsClient && hadClient) { ClearReplica(); buildOptions.Reset(); }
            hadClient = CoopSession.IsClient;
            if (!menu && !HostMenu()) expanded = false;
            gateInput = CoopSession.IsClient || menu || HostMenu();
            BlockMenuKeys = CoopSession.IsClient || menu && expanded;
            UpdateBlocker();
        }

        private void UpdateInvites(bool menu)
        {
            if (!menu)
            {
                invites?.Dispose();
                invites = null;
                return;
            }
            if (Time.unscaledTime < nextInviteCheck) return;
            nextInviteCheck = Time.unscaledTime + 3f;
            try
            {
                if (invites == null)
                    invites = SteamInvites.Listen(id =>
                    {
                        if (MenuReady() && !CoopSession.IsClient && !CoopSession.IsHosting)
                            JoinSteam(id);
                    });
                if (!launchChecked)
                {
                    string id = SteamInvites.LaunchLobby();
                    launchChecked = true;
                    if (!string.IsNullOrEmpty(id) && !CoopSession.IsClient) JoinSteam(id);
                }
            }
            catch (InvalidOperationException)
            {
                // Steam may not be initialized on the first menu frame; LAN remains usable.
            }
        }

        private void JoinSteam(string id)
        {
            expanded = true;
            lobby = id ?? "";
            localMessage = "";
            CoopSession.Join(lobby.Trim(), true);
        }

        private void ClearReplica()
        {
            pending.Clear();
            view = null;
            received = null;
            dragging = false;
            showResult = false;
            buildings.Close();
            buildOptions.Close();
            ColonyManagementPanel.Close();
            selectedX = selectedY = -1;
            map.Dispose();
            motion.Clear();
        }

        // Unity's menu uses a GraphicRaycaster. Reserve the IMGUI panel's screen area
        // so the same mouse click cannot also press Load Game or Resume underneath.
        private void UpdateBlocker()
        {
            float scale = UiScale();
            float width = Screen.width / scale;
            float height = Screen.height / scale;
            Rect logical = PanelRect(width, height);
            screenBlock = new Rect(logical.x * scale, logical.y * scale, logical.width * scale, logical.height * scale);
            if (!gateInput)
            {
                if (blocker) blocker.enabled = false;
                return;
            }
            if (!blocker)
            {
                var canvasObject = new GameObject("Co-op input area", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
                DontDestroyOnLoad(canvasObject);
                blocker = canvasObject.GetComponent<Canvas>();
                blocker.renderMode = RenderMode.ScreenSpaceOverlay;
                blocker.sortingOrder = short.MaxValue;
                var area = new GameObject("Panel input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                area.transform.SetParent(blocker.transform, false);
                blockerRect = area.GetComponent<RectTransform>();
                blockerRect.anchorMin = blockerRect.anchorMax = blockerRect.pivot = new Vector2(0, 1);
                var image = area.GetComponent<Image>();
                image.color = Color.clear;
                image.raycastTarget = true;
                image.canvasRenderer.cullTransparentMesh = false;
            }
            blocker.enabled = true;
            blockerRect.anchoredPosition = new Vector2(screenBlock.x, -screenBlock.y);
            blockerRect.sizeDelta = screenBlock.size;
        }

        private static float UiScale() => Mathf.Max(0.35f, Mathf.Min(1.4f, Mathf.Min(Screen.width / 900f, Screen.height / 600f)));

        private Rect PanelRect(float width, float height)
        {
            if (CoopSession.IsClient) return new Rect(0, 0, width, height);
            return expanded ? new Rect(width - 366, 12, 354, 312) : new Rect(width - 126, 12, 114, 34);
        }

        private void OnGUI()
        {
            if (!gateInput) return;
            Matrix4x4 old = GUI.matrix;
            int oldDepth = GUI.depth;
            GUISkin oldSkin = GUI.skin;
            float scale = UiScale();
            try
            {
                GUI.depth = -3000;
                if (!panelSkin)
                {
                    panelSkin = Instantiate(oldSkin);
                    panelSkin.label.wordWrap = true;
                    panelSkin.button.wordWrap = true;
                    panelFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" }, 15);
                    if (panelFont) panelSkin.font = panelFont;
                }
                GUI.skin = panelSkin;
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                var rect = PanelRect(Screen.width / scale, Screen.height / scale);
                GUI.BeginGroup(rect);
                try { DrawPanel(rect.width, rect.height); }
                finally { GUI.EndGroup(); }
            }
            finally { GUI.skin = oldSkin; GUI.matrix = old; GUI.depth = oldDepth; }
        }

        private void DrawPanel(float w, float h)
        {
            if (CoopSession.IsClient)
            {
                Fill(new Rect(0, 0, w, h), new Color(.10f, .10f, .14f));
                DrawRemote(w, h);
                return;
            }
            if (!expanded)
            {
                if (GUI.Button(new Rect(0, 0, w, h), "多人合作")) expanded = true;
                return;
            }
            Fill(new Rect(0, 0, w, h), new Color(.11f, .11f, .16f));
            GUI.Label(new Rect(12, 10, 260, 26), "多人合作");
            if (GUI.Button(new Rect(w - 78, 8, 66, 26), "收起")) expanded = false;
            if (CoopSession.IsHosting)
            {
                GUI.Label(new Rect(12, 48, w - 24, 48), "房间地址：" + CoopSession.Endpoint);
                if (GUI.Button(new Rect(12, 103, 150, 32), "邀请 Steam 好友")) CoopSession.Invite();
                if (GUI.Button(new Rect(170, 103, 150, 32), "复制房间地址"))
                    GUIUtility.systemCopyBuffer = CoopSession.Endpoint;
                if (GUI.Button(new Rect(12, 151, 150, 32), "结束房间")) { CoopSession.Disconnect(); localMessage = ""; }
            }
            else if (MenuReady())
            {
                GUI.Label(new Rect(12, 46, 320, 24), "通过 Steam 房间号加入");
                lobby = GUI.TextField(new Rect(12, 74, 220, 30), lobby, 24);
                if (GUI.Button(new Rect(240, 74, 98, 30), "加入房间")) JoinSteam(lobby);
                GUI.Label(new Rect(12, 122, 320, 24), "开发局域网地址");
                lan = GUI.TextField(new Rect(12, 151, 220, 30), lan, 96);
                if (GUI.Button(new Rect(240, 151, 98, 30), "局域网加入"))
                {
                    localMessage = "";
                    CoopSession.Join(lan.Trim(), false);
                }
                GUI.Label(new Rect(12, 198, w - 24, 42), "开房请先加载殖民地，再打开暂停菜单。");
            }
            else
            {
                GUI.Label(new Rect(12, 48, w - 24, 48), "与好友共享当前殖民地。由房主保存进度。");
                if (GUI.Button(new Rect(12, 103, 152, 32), "创建 Steam 房间")) CoopSession.Host(true);
                if (GUI.Button(new Rect(172, 103, 166, 32), "创建开发局域网房间")) CoopSession.Host(false);
            }
            GUI.Label(new Rect(12, h - 68, w - 24, 64), Status());
        }

        private void DrawRemote(float w, float h)
        {
            var management = new Rect(w - 328, 9, 104, 32);
            if (view != null && ColonyManagementPanel.IsOpen) { ColonyManagementPanel.Draw(management); return; }
            if (buildings.IsOpen) { buildings.Draw(w, h); return; }
            if (buildOptions.IsOpen) { buildOptions.Draw(w, h); return; }
            string clock = view == null ? "" : "周期 " + view.Cycle + " · " + (view.CycleFraction * 100).ToString("0") +
                "% · " + (view.Paused ? "已暂停" : view.Speed == 0 ? "运行中" : view.Speed + "×") + "   ";
            GUI.Label(new Rect(16, 12, w - 360, 35), clock + Status());
            if (GUI.Button(new Rect(w - 216, 9, 100, 32), showResult ? "返回地图" : "会话状态"))
                showResult = !showResult;
            if (GUI.Button(new Rect(w - 108, 9, 96, 32), "离开房间"))
            {
                CoopSession.Disconnect();
                ClearReplica();
                return;
            }
            if (view == null)
            {
                GUI.Label(new Rect(16, 66, w - 32, 30), "正在等待房主发送殖民地视图…");
                return;
            }
            if (showResult)
            {
                string text = Status() + "\n\n客机模拟步拦截计数：" + PreventedGuestSteps;
                float contentWidth = w - 74f;
                float contentHeight = GUI.skin.label.CalcHeight(new GUIContent(text), contentWidth) + 16f;
                resultScroll = GUI.BeginScrollView(new Rect(16, 56, w - 32f, h - 70f), resultScroll,
                    new Rect(0, 0, contentWidth, contentHeight));
                GUI.Label(new Rect(0, 0, contentWidth, contentHeight), text);
                GUI.EndScrollView();
                return;
            }
            const float sidebar = 238f;
            float availableWidth = w - sidebar - 42f, availableHeight = h - 74f;
            int cols = ColonyView.Width / zoom, rows = ColonyView.Height / zoom;
            int left = (ColonyView.Width - cols) / 2, bottom = (ColonyView.Height - rows) / 2;
            float unit = Mathf.Min(availableWidth / cols, availableHeight / rows);
            var bounds = new Rect(16, 54, cols * unit, rows * unit);
            GUI.DrawTextureWithTexCoords(bounds, map.Texture,
                new Rect((float)left / ColonyView.Width, (float)bottom / ColonyView.Height,
                         (float)cols / ColonyView.Width, (float)rows / ColonyView.Height));
            if (Event.current.type == EventType.Repaint && overlay < (int)ColonyMap.Overlay.Power)
            {
                GUI.BeginGroup(bounds);
                foreach (var d in view.Dupes)
                {
                    if (!motion.TryGet(d.Id, Time.unscaledTime, out float dx, out float dy)) continue;
                    float mx = (dx - view.X - left) * unit;
                    float my = (rows - (dy - view.Y - bottom)) * unit;
                    Fill(new Rect(mx - .23f * unit, my - .52f * unit, .46f * unit, .48f * unit), new Color32(239,117,98,255));
                    Fill(new Rect(mx - .21f * unit, my - .85f * unit, .42f * unit, .36f * unit), new Color32(255,221,174,255));
                }
                GUI.EndGroup();
            }
            HandleMap(bounds, left, bottom, cols, rows, unit);
            DrawSidebar(w - sidebar - 12f, sidebar, h);
            ColonyManagementPanel.Draw(management);
        }

        private void DrawSidebar(float x, float width, float height)
        {
            int choice = GUI.SelectionGrid(new Rect(x, 48, width, 48), overlay, Overlays, 4);
            if (choice != overlay) { overlay = choice; map.Paint(view, (ColonyMap.Overlay)overlay); }
            if (GUI.Button(new Rect(x, 101, 42, 27), "←")) Pan(-1, 0);
            if (GUI.Button(new Rect(x + 46, 101, 42, 27), "→")) Pan(1, 0);
            if (GUI.Button(new Rect(x + 92, 101, 42, 27), "↑")) Pan(0, 1);
            if (GUI.Button(new Rect(x + 138, 101, 42, 27), "↓")) Pan(0, -1);
            if (GUI.Button(new Rect(x + 184, 101, 54, 27), zoom == 1 ? "放大" : "缩小"))
                zoom = zoom == 1 ? 2 : 1;
            if (GUI.Button(new Rect(x, 129, 116, 28), "返回打印舱")) CoopSession.Subscribe(-1, -1);
            if (GUI.Button(new Rect(x + 122, 129, 116, 28), "殖民地概览"))
            {
                CoopSession.Command("overview", view.X, view.Y);
                localMessage = "";
                showResult = true;
            }
            int nextGroup = GUI.Toolbar(new Rect(x, 168, width, 28), group, Groups);
            if (nextGroup != group) { group = nextGroup; tool = 0; }
            tool = GUI.SelectionGrid(new Rect(x, 201, width, 155), tool, Labels[group], 2);
            string selected = Actions[group][tool];
            if (group == 1 && !IsLine(selected))
            {
                if (GUI.Button(new Rect(x, 364, width, 35), "方向 / 材料：" + buildOptions.LabelFor(selected)))
                    buildOptions.Open(selected);
            }
            else
                GUI.Label(new Rect(x, 364, width, 42), selected == "inspect" ? "点击格子检查环境和管网。" :
                    IsLine(selected) ? "沿水平方向或垂直方向拖动铺设。" : "点击或拖选格子下令。");
            if (pending.Count > 0 && GUI.Button(new Rect(x, 404, width, 28), "停止发送剩余 " + pending.Count + " 格"))
                pending.Clear();
            long dx = (long)selectedX - view.X, dy = (long)selectedY - view.Y;
            if (dx >= 0 && dy >= 0 && dx < ColonyView.Width && dy < ColonyView.Height)
            {
                var cell = view.Cells[(int)dy * ColonyView.Width + (int)dx];
                string detail = selectedX + ", " + selectedY;
                if ((cell.Flags & 1) != 0)
                {
                    detail += "\n" + cell.Element + "  " + cell.Mass.ToString("0.##") + " kg  " +
                              (cell.Temperature - 273.15f).ToString("0.0") + " °C";
                    if (!string.IsNullOrEmpty(cell.Building))
                    {
                        detail += "\n" + cell.Building;
                        if ((cell.Flags & 32) != 0) detail += " · 施工 " + cell.Progress + "%";
                        if ((cell.BuildingStyle & 128) != 0) detail += " · 待拆除";
                    }
                    foreach (var d in view.Dupes)
                        if (d.X == selectedX && d.Y == selectedY) detail += "\n" + d.Name + " · " + d.Activity;
                }
                else detail += "\n未探索";
                GUI.Label(new Rect(x, 440, width, height - 446), detail);
            }
        }

        private void Pan(int dx, int dy)
        {
            int x = (int)Math.Max(0, Math.Min((long)int.MaxValue - ColonyView.Width, view.X + (long)dx * 12 / zoom));
            int y = (int)Math.Max(0, Math.Min((long)int.MaxValue - ColonyView.Height, view.Y + (long)dy * 8 / zoom));
            CoopSession.Subscribe(x, y);
        }

        private void HandleMap(Rect bounds, int left, int bottom, int cols, int rows, float unit)
        {
            var e = Event.current;
            bool inside = bounds.Contains(e.mousePosition);
            int cx = view.X + left + Mathf.Clamp(Mathf.FloorToInt((e.mousePosition.x - bounds.x) / unit), 0, cols - 1);
            int cy = view.Y + bottom + rows - 1 - Mathf.Clamp(Mathf.FloorToInt((e.mousePosition.y - bounds.y) / unit), 0, rows - 1);
            if (dragging && (dragOriginX != view.X || dragOriginY != view.Y)) dragging = false;
            if (e.type == EventType.MouseDown && e.button == 0 && inside)
            {
                selectedX = dragX = cx; selectedY = dragY = cy;
                dragging = true;
                dragOriginX = view.X; dragOriginY = view.Y;
                e.Use();
            }
            else if (dragging && e.type == EventType.MouseUp && e.button == 0)
            {
                dragging = false;
                if (inside) Enqueue(dragX, dragY, cx, cy);
                if (inside) { selectedX = cx; selectedY = cy; }
                e.Use();
            }
            if (e.type != EventType.Repaint) return;
            int sx = dragging ? Math.Min(cx, dragX) : selectedX;
            int sy = dragging ? Math.Max(cy, dragY) : selectedY;
            if (sx < view.X + left || sx >= view.X + left + cols || sy < view.Y + bottom || sy >= view.Y + bottom + rows) return;
            int nx = dragging ? Math.Abs(cx - dragX) + 1 : 1;
            int ny = dragging ? Math.Abs(cy - dragY) + 1 : 1;
            var selection = new Rect(bounds.x + (sx - view.X - left) * unit,
                bounds.y + (rows - 1 - (sy - view.Y - bottom)) * unit, nx * unit, ny * unit);
            Fill(selection, new Color(1f, 1f, .65f, .24f));
        }

        private void Enqueue(int x1, int y1, int x2, int y2)
        {
            string selected = Actions[group][tool];
            if (selected == "building")
            {
                var cell = view.Cells[(y2 - view.Y) * ColonyView.Width + x2 - view.X];
                if ((cell.Flags & (16 | 32)) != 0 && cell.TargetId > 0)
                {
                    localMessage = "";
                    buildings.Open(x2, y2, cell);
                }
                else localMessage = "请点击建筑所在的格子。";
                return;
            }
            if (selected == "inspect")
            {
                if ((view.Cells[(y2 - view.Y) * ColonyView.Width + x2 - view.X].Flags & 1) != 0)
                {
                    localMessage = "";
                    CoopSession.Command("inspect", x2, y2);
                }
                return;
            }
            if (IsLine(selected))
            {
                if (x1 != x2 && y1 != y2) { localMessage = "拖选一条水平或垂直的直线。"; return; }
                localMessage = "";
                CoopSession.Command(selected + "@" + x1 + "," + y1, x2, y2);
                return;
            }
            int count = (Math.Abs(x2 - x1) + 1) * (Math.Abs(y2 - y1) + 1);
            if (pending.Count + count > MaxBatch) { localMessage = "一次最多排队 256 格。"; return; }
            int queued = 0;
            for (int y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
                for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
                {
                    var c = view.Cells[(y - view.Y) * ColonyView.Width + x - view.X];
                    if ((c.Flags & 1) == 0) continue;
                    string action = group == 1 ? buildOptions.ActionFor(selected) : selected;
                    if (action == "cancel" || action == "deconstruct" || action == "priority5" || action == "priority9")
                    {
                        if (c.TargetId <= 0) continue;
                        action += ":" + c.TargetId;
                    }
                    pending.Enqueue(new PendingCommand { X = x, Y = y, Action = action });
                    queued++;
                }
            localMessage = queued == 0 ? "选区内没有可操作的目标。" : "";
        }

        private static bool IsLine(string action) => action == "wire" || action == "gaspipe" || action == "liquidpipe";

        private string Status()
        {
            string status = CoopSession.Status;
            if (status.IndexOf("] BLD1 ", StringComparison.Ordinal) >= 0) status = "建筑设置已更新。";
            else if (status.IndexOf("] M1:", StringComparison.Ordinal) >= 0) status = "殖民地管理数据已更新。";
            else if (status.IndexOf("] B1:", StringComparison.Ordinal) >= 0) status = "材料与方向列表已更新。";
            return string.IsNullOrEmpty(localMessage) ? status : localMessage + " " + status;
        }

        private static void Fill(Rect rect, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private void OnDestroy()
        {
            BlockMenuKeys = false;
            invites?.Dispose();
            invites = null;
            ClearReplica();
            if (blocker) Destroy(blocker.gameObject);
            if (panelSkin) Destroy(panelSkin);
            if (panelFont) Destroy(panelFont);
            CoopSession.Disconnect();
        }

        [HarmonyPatch(typeof(MainMenu), "OnKeyDown")]
        private static class MenuKeyboard
        {
            private static bool Prefix() => !BlockMenuKeys;
        }

        [HarmonyPatch(typeof(global::Game), "StepTheSim")]
        private static class GuestSimulationGuard
        {
            private static bool Prefix()
            {
                if (!CoopSession.IsClient) return true;
                PreventedGuestSteps++;
                return false;
            }
        }
    }
}
