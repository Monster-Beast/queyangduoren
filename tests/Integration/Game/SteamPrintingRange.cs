using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamPrintingRange
    {
        const string Key = "qy_print_";
        const string Renamed = "Printing Fixture";
        const string CandidateName = "Printing Candidate";
        const string Reply = "NM1/printing/";
        int stage, cell, target, beforeSpawnIndex, born, step;
        float deadline, snapshotAt;
        Telepad pod;
        FixtureFrame photo;
        HashSet<int> before;
        string[] offer;
        string choice, acknowledgment, packageId;
        float packageQuantity;
        bool replayRejected, renameSent, finishedRejoin;
        Action<string> resultObserver;
        static CarePackage deliveredPackage;
        static int packageOpenings;

        static readonly AccessTools.FieldRef<Immigration,int> SpawnIndex =
            AccessTools.FieldRefAccess<Immigration,int>("spawnIdx");
        static readonly AccessTools.FieldRef<CharacterSelectionController,List<ITelepadDeliverableContainer>> Containers =
            AccessTools.FieldRefAccess<CharacterSelectionController,List<ITelepadDeliverableContainer>>("containers");
        static readonly AccessTools.FieldRef<TelepadSideScreen,Telepad> SideTarget =
            AccessTools.FieldRefAccess<TelepadSideScreen,Telepad>("targetTelepad");
        static readonly AccessTools.FieldRef<TelepadSideScreen,KButton> ViewCandidates =
            AccessTools.FieldRefAccess<TelepadSideScreen,KButton>("viewImmigrantsBtn");
        static readonly AccessTools.FieldRef<CharacterSelectionController,KButton> ProceedButton =
            AccessTools.FieldRefAccess<CharacterSelectionController,KButton>("proceedButton");
        static readonly AccessTools.FieldRef<ImmigrantScreen,KButton> RejectButton =
            AccessTools.FieldRefAccess<ImmigrantScreen,KButton>("rejectButton");
        static readonly AccessTools.FieldRef<ImmigrantScreen,KButton> ConfirmRejection =
            AccessTools.FieldRefAccess<ImmigrantScreen,KButton>("confirmRejectionBtn");
        static readonly MethodInfo EditName = AccessTools.Method(typeof(CharacterContainer), "OnNameChanged");
        static readonly AccessTools.FieldRef<ImmigrantScreen,KButton> CloseButton =
            AccessTools.FieldRefAccess<ImmigrantScreen,KButton>("closeButton");
        static readonly AccessTools.FieldRef<CarePackageContainer,LocText> SupplyCount =
            AccessTools.FieldRefAccess<CarePackageContainer,LocText>("currentQuantity");

        [HarmonyPatch(typeof(CarePackage), "SpawnContents")]
        static class SupplyDelivered
        {
            static void Postfix(CarePackage __instance)
            {
                if (ReferenceEquals(__instance, deliveredPackage)) packageOpenings++;
            }
        }

        internal bool Check(CSteamID room, CSteamID peer, bool host, int round, Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_PRINTING") != "1") return true;
            if (round == 2) return Rejoin(room, host, log);
            if (stage == 4) return true;
            if (deadline == 0) deadline = Time.unscaledTime + 130;
            if (Time.unscaledTime > deadline)
                throw new TimeoutException($"Printing fixture role={Role(host)} stage={stage} step={step} target={target} ack={acknowledgment}");
            return host ? Host(room, peer, log) : Guest(room, log);
        }

        bool Host(CSteamID room, CSteamID guest, Action<string> log)
        {
            if (step == 0)
            {
                if (Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT") || !CoopSession.IsHosting)
                    throw new InvalidOperationException("Printing fixture requires an isolated host");
                pod = Components.Telepads.Items.First(p => p != null);
                cell = Grid.PosToCell(pod);
                Grid.CellToXY(cell, out int px, out int py);
                var view = ColonyView.Decode(HostWorld.Capture(-1, -1));
                target = view.Cells[(py - view.Y) * ColonyView.Width + px - view.X].TargetId;
                if (target <= 0) throw new InvalidDataException("Missing stable printing pod target");
                before = Ids();
                beforeSpawnIndex = SpawnIndex(Immigration.Instance);
                if (stage == 1)
                {
                    Immigration.Instance.timeBeforeSpawn = 0;
                    Immigration.Instance.Sim200ms(.2f);
                }
                Set(room, "pod", $"{px},{py},{target}");
                Set(room, "stage", stage.ToString());
                if (stage != 1) OpenRound();
                step = 1;
                log($"PRINT_FIXTURE_STARTED stage={stage} pod={target} count={before.Count}");
                return false;
            }

            Grid.CellToXY(cell, out int x, out int y);
            var screen = ImmigrantScreen.instance;
            if (step == 1)
            {
                if (Containers(screen)?.Any(card => card is CharacterContainer c && c.Stats != null) != true) return false;
                string digest = SteamMatchmaking.GetLobbyMemberData(room, guest, Key + "offer" + stage);
                if (string.IsNullOrEmpty(digest)) return false;
                if (screen.gameObject.activeSelf) throw new InvalidDataException("Guest request opened the host's native screen");
                string response = HostWorld.Execute($"nm/printing/{target}", x, y);
                offer = response.Split('/');
                if (offer.Length != 7 || BatchDigest(offer) != digest)
                    throw new InvalidDataException("Guest and host did not observe the same printing options");
                ImmigrantScreen.InitializeImmigrantScreen(pod);
                var ordered = Containers(screen).OrderBy(c => c.GetGameObject().transform.GetSiblingIndex()).ToArray();
                if (PrintingCandidates.Write(ordered.Select(Delivery).ToArray()) != offer[5])
                    throw new InvalidDataException("Native candidate stats or order differ from the guest offer");
                if (stage == 1)
                {
                    var package = ordered.OfType<CarePackageContainer>().FirstOrDefault()?.Info;
                    if (package == null) throw new InvalidDataException("Fixture colony generated no care package");
                    packageId = package.id;
                    packageQuantity = package.quantity;
                }
                Set(room, "same" + stage, "1");
                snapshotAt = Time.unscaledTime + .55f;
                step = 2;
                return false;
            }
            if (step == 2)
            {
                if (photo == null)
                {
                    if (Time.unscaledTime < snapshotAt) return false;
                    if (!screen.gameObject.activeInHierarchy) throw new InvalidDataException("Host candidate screen closed before capture");
                    photo = Shot(true);
                }
                if (!photo.Complete || SteamMatchmaking.GetLobbyMemberData(room, guest, Key + "ready" + stage) != "1") return false;
                if (stage == 3)
                {
                    RejectButton(screen).SignalClick(KKeyCode.Mouse0);
                    ConfirmRejection(screen).SignalClick(KKeyCode.Mouse0);
                }
                Set(room, "go" + stage, "1");
                step = 3;
                return false;
            }

            if (SteamMatchmaking.GetLobbyMemberData(room, guest, Key + "done" + stage) != "1") return false;
            if (Immigration.Instance.ImmigrantsAvailable || SpawnIndex(Immigration.Instance) != beforeSpawnIndex + 1)
                throw new InvalidDataException("A printing decision did not consume exactly one native round");
            var added = Ids().Except(before).ToArray();
            if (added.Length != (stage == 0 ? 1 : 0) || screen.gameObject.activeSelf)
                throw new InvalidDataException("Unexpected new duplicant count or open host printing screen after decision");
            if (stage == 0)
            {
                born = added[0];
                if (FindDupe(born)?.GetProperName() != CandidateName)
                    throw new InvalidDataException("Delivered duplicant did not retain the guest's candidate rename");
                Set(room, "born", born.ToString());
            }
            if (stage == 1)
            {
                var packages = UnityEngine.Object.FindObjectsByType<CarePackage>(FindObjectsSortMode.None)
                    .Where(p => p.info?.id == packageId && Mathf.Approximately(p.info.quantity, packageQuantity)).ToArray();
                if (packages.Length != 1) throw new InvalidDataException("Expected exactly one native package with the selected contents");
                deliveredPackage = packages[0];
            }
            log($"PRINT_RANGE role=host stage={stage} sameCards=True consumedOnce=True added={added.Length}");
            Advance();
            return stage == 4;
        }

        bool Guest(CSteamID room, Action<string> log)
        {
            if (step == 0)
            {
                if (!string.Equals(SteamMatchmaking.GetLobbyData(room, Key + "stage"), stage.ToString(), StringComparison.Ordinal))
                    return false;
                var at = SteamMatchmaking.GetLobbyData(room, Key + "pod").Split(',');
                if (at.Length != 3) return false;
                int px = int.Parse(at[0]), py = int.Parse(at[1]);
                cell = Grid.XYToCell(px, py);
                target = int.Parse(at[2]);
                var camera = CameraController.Instance;
                camera.SetTargetPos(new Vector3(px + .5f, py + 1f, camera.transform.position.z), camera.OrthographicSize, false);
                CoopSession.Subscribe(px - ColonyView.Width / 2, py - ColonyView.Height / 2);
                step = 1;
            }
            if (step == 1)
            {
                pod = Components.Telepads.Items.FirstOrDefault(p => p != null && Grid.PosToCell(p) == cell);
                if (pod?.GetComponent<NativeProxySettings>() == null) return false;
                SelectTool.Instance.Select(pod.GetComponent<KSelectable>(), false);
                step = 10;
                return false;
            }
            if (step == 10)
            {
                var side = UnityEngine.Object.FindObjectsByType<TelepadSideScreen>(FindObjectsSortMode.None)
                    .FirstOrDefault(s => SideTarget(s) == pod && ViewCandidates(s).isInteractable);
                if (side == null) return false;
                resultObserver = Receive;
                CoopSession.CommandResult += resultObserver;
                ViewCandidates(side).SignalClick(KKeyCode.Mouse0);
                step = 2;
                return false;
            }
            if (step == 2)
            {
                if (offer == null) return false;
                var screen = ImmigrantScreen.instance;
                if (photo == null)
                {
                    if (!screen.gameObject.activeInHierarchy) return false;
                    var cards = Containers(screen).OrderBy(c => c.GetGameObject().transform.GetSiblingIndex()).ToArray();
                    var nativeOptions = cards.Select(Delivery).ToArray();
                    if (nativeOptions.Any(card => card == null)) return false;
                    if (PrintingCandidates.Write(nativeOptions) != offer[5])
                        throw new InvalidDataException("Guest native cards do not match the offered stats or order");
                    if (snapshotAt == 0) snapshotAt = Time.unscaledTime + .55f;
                    if (Time.unscaledTime < snapshotAt) return false;
                    CheckGuestLayout(screen, cards);
                    string[] quantities = offer[6].Split(',');
                    for (int i = 0; i < cards.Length; i++)
                    {
                        if (!(cards[i] is CarePackageContainer supply)) continue;
                        string expected = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(quantities[i].Replace('_', '/')));
                        if (SupplyCount(supply).text != expected)
                            throw new InvalidDataException("Guest stock label is not showing the host's inventory");
                    }
                    photo = Shot(false);
                }
                if (!photo.Complete) return false;
                if (SteamMatchmaking.GetLobbyData(room, Key + "same" + stage) != "1") return false;
                SteamMatchmaking.SetLobbyMemberData(room, Key + "ready" + stage, "1");
                if (SteamMatchmaking.GetLobbyData(room, Key + "go" + stage) != "1") return false;
                int index = Array.FindIndex(PrintingCandidates.Read(offer[5]), card => stage == 1 ? card is CarePackageContainer.CarePackageInstanceData :
                    card is MinionStartingStats);
                if (index < 0) throw new InvalidDataException("Missing required printing candidate");
                choice = stage == 2 ? "reject" : index.ToString();
                if (stage == 3)
                {
                    // The host's decision may already be visible in the guest panel.
                    // Send the revision captured before that decision to exercise the race.
                    Grid.CellToXY(cell, out int x, out int y);
                    CoopSession.Command($"nm/printing/{target}/{offer[4]}/{choice}", x, y);
                }
                else if (stage == 2)
                {
                    RejectButton(screen).SignalClick(KKeyCode.Mouse0);
                    ConfirmRejection(screen).SignalClick(KKeyCode.Mouse0);
                }
                else
                {
                    var card = Containers(screen).OrderBy(c => c.GetGameObject().transform.GetSiblingIndex()).ElementAt(index);
                    if (stage == 0) EditName.Invoke(card, new object[] { CandidateName });
                    card.SelectDeliverable();
                    if (!ProceedButton(screen).isInteractable) throw new InvalidDataException("Native print button did not accept the chosen card");
                    ProceedButton(screen).SignalClick(KKeyCode.Mouse0);
                }
                step = 3;
                return false;
            }
            if (step == 3)
            {
                if (acknowledgment == null) return false;
                string expected = stage == 2 ? "rejected" : stage == 3 ? "changed" : "accepted";
                if (acknowledgment != expected) throw new InvalidDataException("Unexpected host printing acknowledgment: " + acknowledgment);
                Grid.CellToXY(cell, out int x, out int y);
                CoopSession.Command($"nm/printing/{target}/{offer[4]}/{choice}", x, y);
                step = 4;
                return false;
            }
            if (!replayRejected) return false;
            SteamMatchmaking.SetLobbyMemberData(room, Key + "done" + stage, "1");
            if (stage == 0)
            {
                if (!int.TryParse(SteamMatchmaking.GetLobbyData(room, Key + "born"), out born)) return false;
                var dupe = NativeWorldMirror.GetDupeSelectable(born);
                if (dupe?.GetComponent<KBatchedAnimController>()?.IsVisible() != true) return false;
                if (!renameSent)
                {
                    SelectTool.Instance.Select(dupe, false);
                    if (!NativeManagement.RequestObjectName(dupe.gameObject, Renamed))
                        throw new InvalidDataException("New duplicant is not manageable by stable host identity");
                    renameSent = true;
                }
                if (dupe.GetProperName() != Renamed) return false;
            }
            log($"PRINT_RANGE role=guest stage={stage} sameCards=True replayRejected=True newDupeManaged={stage == 0}");
            CoopSession.CommandResult -= resultObserver;
            resultObserver = null;
            NativePrinting.ResetGuest();
            SelectTool.Instance.Select(null, false);
            Advance();
            return stage == 4;

            void Receive(string result)
            {
                if (!result.StartsWith(Reply + target + "/", StringComparison.Ordinal)) return;
                var parts = result.Split('/');
                if (parts[3] == "cards" && step == 2 && offer == null)
                {
                    offer = parts;
                    SteamMatchmaking.SetLobbyMemberData(room, Key + "offer" + stage, BatchDigest(parts));
                }
                if (step == 3 && (parts[3] == "accepted" || parts[3] == "rejected" || parts[3] == "changed"))
                    acknowledgment = parts[3];
                if (step == 4 && parts[3] == "changed") replayRejected = true;
            }
        }

        bool Rejoin(CSteamID room, bool host, Action<string> log)
        {
            if (finishedRejoin) return true;
            if (!int.TryParse(SteamMatchmaking.GetLobbyData(room, Key + "born"), out born)) return false;
            var dupe = host ? FindDupe(born)?.GetComponent<KSelectable>() : NativeWorldMirror.GetDupeSelectable(born);
            if (dupe?.GetProperName() != Renamed) return false;
            if (host && packageOpenings != 1) throw new InvalidDataException("Native delivery did not open the selected package exactly once");
            finishedRejoin = true;
            log($"PRINT_REJOIN role={Role(host)} id={born} retainedName=True packageOpenings={packageOpenings}");
            return true;
        }

        void Advance()
        {
            stage++;
            step = 0;
            photo = null;
            offer = null;
            acknowledgment = null;
            replayRejected = false;
            snapshotAt = 0;
        }

        static void OpenRound()
        {
            Immigration.Instance.timeBeforeSpawn = 0;
            Immigration.Instance.Sim200ms(.2f);
            if (!Immigration.Instance.ImmigrantsAvailable) throw new InvalidDataException("Fixture printing pod is not operational");
        }

        static HashSet<int> Ids() => new HashSet<int>(Components.LiveMinionIdentities.Items.Where(d => d != null)
            .Select(d => d.GetComponent<KPrefabID>().InstanceID));
        static MinionIdentity FindDupe(int id) =>
            Components.LiveMinionIdentities.Items.FirstOrDefault(d => d != null && d.GetComponent<KPrefabID>().InstanceID == id);
        static ITelepadDeliverable Delivery(ITelepadDeliverableContainer card) => card is CharacterContainer dupe ?
            (ITelepadDeliverable)dupe.Stats : ((CarePackageContainer)card).carePackageInstanceData;
        static string BatchDigest(string[] fields) => NativeManagement.Digest(string.Join("/", fields.Take(6)));
        static void CheckGuestLayout(ImmigrantScreen screen, ITelepadDeliverableContainer[] cards)
        {
            var canvas = screen.GetComponentInParent<Canvas>()?.rootCanvas;
            var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var corners = new Vector3[4];
            foreach (var rect in cards.Select(card => (RectTransform)card.GetGameObject().transform)
                .Concat(new[] { (RectTransform)CloseButton(screen).transform, (RectTransform)ProceedButton(screen).transform }))
            {
                rect.GetWorldCorners(corners);
                if (corners.Select(corner => RectTransformUtility.WorldToScreenPoint(camera, corner))
                    .Any(point => point.x < -1 || point.y < -1 || point.x > Screen.width + 1 || point.y > Screen.height + 1))
                    throw new InvalidDataException("The original guest printing screen exceeds the window");
            }
        }
        static string Role(bool host) => host ? "host" : "guest";
        FixtureFrame Shot(bool host)
        {
            var frame = new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),
                $"printing-{Role(host)}-{stage}.png"));
            frame.Capture();
            return frame;
        }
        static void Set(CSteamID room, string key, string value)
        {
            if (!SteamMatchmaking.SetLobbyData(room, Key + key, value)) throw new IOException("Could not publish printing test state");
        }
    }
}
