using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;

namespace Queyang.Integration
{
    internal sealed class SteamAlertRange
    {
        const string Key = "qy_alert_range_";
        static readonly PrioritySetting Basic = new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);
        static readonly PrioritySetting Urgent = new PrioritySetting(PriorityScreen.PriorityClass.topPriority, 1);
        static readonly System.Reflection.FieldInfo VignetteImage = AccessTools.Field(typeof(Vignette), "image");
        int stage, cell = Grid.InvalidCell;
        float deadline, nextLog;
        bool clicked;
        FixtureFrame frame;
        Vector3 previousCamera;
        float previousZoom;

        internal bool Check(CSteamID lobby, CSteamID peer, bool host, int round, int reserved, Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_ALERT_RANGE") != "1" || round != 1 || stage == 4) return true;
            var camera = CameraController.Instance;
            if (deadline == 0)
            {
                deadline = Time.realtimeSinceStartup + 75;
                previousCamera = camera.transform.position;
                previousZoom = camera.OrthographicSize;
            }
            if (Time.realtimeSinceStartup >= deadline)
                throw new TimeoutException($"Native yellow alert host={host} stage={stage} cell={cell} clicked={clicked}");
            if (cell == Grid.InvalidCell)
            {
                if (host)
                {
                    cell = Designate(reserved);
                    Publish(lobby, "cell", cell);
                    Publish(lobby, "stage", 0);
                }
                else if (!int.TryParse(SteamMatchmaking.GetLobbyData(lobby, Key + "cell"), out int advertised)) return false;
                else cell = advertised;
            }
            var point = Grid.CellToPosCCC(cell, Grid.SceneLayer.Move);
            var focus = new Vector3(point.x, point.y, camera.transform.position.z);
            camera.SetTargetPos(focus, 7f, false);
            camera.SetPosition(focus);
            camera.OrthographicSize = 7f;
            if (stage == 3)
            {
                if (SteamMatchmaking.GetLobbyData(lobby, Key + "stage") != "3") return false;
                camera.SetTargetPos(previousCamera, previousZoom, false);
                camera.SetPosition(previousCamera);
                camera.OrthographicSize = previousZoom;
                stage++;
                return true;
            }
            if (SteamMatchmaking.GetLobbyData(lobby, Key + "stage") != stage.ToString(CultureInfo.InvariantCulture)) return false;

            bool yellow = stage == 1;
            var wanted = yellow ? Urgent : Basic;
            if (!host && !clicked && stage > 0)
            {
                if (!NativeWorldMirror.IsDigVisible(cell)) return false;
                Click(PrioritizeTool.Instance, point, wanted);
                clicked = true;
                log($"ALERT_RANGE_INPUT stage={stage} cell={cell} tool=PrioritizeTool class={wanted.priority_class} value={wanted.priority_value}");
            }
            var alert = ClusterManager.Instance.activeWorld.AlertManager;
            var vignette = Vignette.Instance;
            var image = vignette == null ? null : (Image)VignetteImage.GetValue(vignette);
            var prioritizable = host ? Diggable.GetDiggable(cell)?.GetComponent<Prioritizable>() : null;
            var priority = prioritizable?.GetMasterPriority();
            bool priorityReady = host ? priority == wanted : NativeWorldMirror.IsDigVisible(cell);
            var color = image == null ? Color.clear : image.color;
            bool colorReady = image != null && image.isActiveAndEnabled && color == (yellow ? vignette.yellowAlertColor : vignette.defaultColor);
            if (alert == null || alert.IsRedAlert() || alert.IsYellowAlert() != yellow || !colorReady || !priorityReady)
            {
                if (Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 4;
                    log($"ALERT_RANGE_WAIT stage={stage} yellow={alert?.IsYellowAlert()} red={alert?.IsRedAlert()} color={ColorUtility.ToHtmlStringRGBA(color)} priority={priority?.priority_class}/{priority?.priority_value}");
                }
                return false;
            }
            string role = host ? "host" : "guest";
            if (frame == null)
            {
                var dir = Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                frame = new FixtureFrame(Path.Combine(dir, $"alert-{role}-{stage}.png"));
                frame.Capture();
                return false;
            }
            if (!frame.Complete) return false;
            string ack = Key + "seen_" + stage;
            if (host && SteamMatchmaking.GetLobbyMemberData(lobby, peer, ack) != "1") return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(lobby, ack, "1");
            log($"ALERT_RANGE stage={stage} role={role} cell={cell} yellow={yellow} red=False color={ColorUtility.ToHtmlStringRGBA(color)} priority={priority?.priority_class}/{priority?.priority_value}");
            stage++;
            frame = null;
            clicked = false;
            if (host)
            {
                if (stage == 3) Click(CancelTool.Instance, point);
                Publish(lobby, "stage", stage);
            }
            if (stage == 3) PlayerController.Instance.ActivateTool(SelectTool.Instance);
            return false;
        }

        static int Designate(int reserved)
        {
            if (!CoopSession.IsHosting || Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT") ||
                !SpeedControlScreen.Instance.IsPaused || !Grid.IsValidCell(reserved))
                throw new InvalidOperationException("Native yellow alert needs a paused isolated host");
            int cell = Enumerable.Range(0, Grid.CellCount).Where(c => c != reserved && Grid.WorldIdx[c] == Grid.WorldIdx[reserved] &&
                Grid.Visible[c] != 0 && Grid.Solid[c] && Grid.GetCellDistance(c, reserved) <= 8 && Grid.IsValidBuildingCell(c) &&
                Diggable.IsDiggable(c) && !Diggable.Undiggable(Grid.Element[c]) && HostWorld.FindBuilding(c) == null && Diggable.GetDiggable(c) == null)
                .OrderBy(c => Grid.GetCellDistance(c, reserved)).DefaultIfEmpty(Grid.InvalidCell).First();
            if (cell == Grid.InvalidCell) throw new InvalidDataException("No separate visible dig order for native yellow alert");
            Click(DigTool.Instance, Grid.CellToPosCCC(cell, Grid.SceneLayer.Move), Basic);
            if (Diggable.GetDiggable(cell) == null) throw new InvalidDataException("Native dig tool did not designate the alert cell");
            PlayerController.Instance.ActivateTool(SelectTool.Instance);
            return cell;
        }

        static void Click(DragTool tool, Vector3 point, PrioritySetting? desired = null)
        {
            PlayerController.Instance.ActivateTool(tool);
            if (desired.HasValue)
            {
                var screen = ToolMenu.Instance.PriorityScreen;
                var button = screen.GetComponentsInChildren<PriorityButton>().First(b => b.priority == desired.Value && b.gameObject.activeInHierarchy);
                AccessTools.DeclaredMethod(typeof(PriorityButton), "OnClick").Invoke(button, null);
                if (screen.GetLastSelectedPriority() != desired.Value) throw new InvalidDataException("Native priority button did not select the requested priority");
            }
            tool.OnLeftClickDown(point);
            tool.OnLeftClickUp(point);
        }

        static void Publish(CSteamID lobby, string name, int value)
        {
            if (!SteamMatchmaking.SetLobbyData(lobby, Key + name, value.ToString(CultureInfo.InvariantCulture)))
                throw new IOException("Steam rejected native yellow alert checkpoint " + name);
        }
    }
}
