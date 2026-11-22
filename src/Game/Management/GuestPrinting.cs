using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativePrinting
    {
        static readonly FieldInfo DupePrefab = AccessTools.Field(typeof(CharacterSelectionController), "containerPrefab");
        static readonly FieldInfo SupplyPrefab = AccessTools.Field(typeof(CharacterSelectionController), "carePackageContainerPrefab");
        static readonly FieldInfo CardParent = AccessTools.Field(typeof(CharacterSelectionController), "containerParent");
        static readonly MethodInfo DisableProceed = AccessTools.Method(typeof(CharacterSelectionController), "DisableProceedButton");
        static readonly MethodInfo SupplyAnimation = AccessTools.Method(typeof(CarePackageContainer), "SetAnimator");
        static readonly MethodInfo SupplyText = AccessTools.Method(typeof(CarePackageContainer), "SetInfoText");
        static readonly FieldInfo SupplyCount = AccessTools.Field(typeof(CarePackageContainer), "currentQuantity");
        static readonly FieldInfo OutfitIndex = AccessTools.Field(typeof(CharacterContainer), "outfitSelectorIndex");
        static readonly MethodInfo RefreshOutfit = AccessTools.Method(typeof(CharacterContainer), "RefreshOutfitSelector");
        static readonly Dictionary<ITelepadDeliverableContainer, ITelepadDeliverable> guestCards =
            new Dictionary<ITelepadDeliverableContainer, ITelepadDeliverable>();

        static Telepad guestPod;
        static int guestTarget, guestCell;
        static float nextPoll, pendingUntil, observedUntil, guestWait;
        static bool requested, guestReady, mounting;
        static string guestRevision;
        static ITelepadDeliverable[] guestOptions;
        static string[] offeredNames, offeredOutfits, offeredStock;

        internal static void ResetGuest()
        {
            guestPod = null;
            guestTarget = guestCell = 0;
            nextPoll = pendingUntil = observedUntil = guestWait = 0;
            requested = guestReady = mounting = false;
            ResetPrintingLayout();
            ClearGuestOffer();
        }

        static void ClearGuestOffer()
        {
            guestRevision = null;
            guestOptions = null;
            offeredNames = offeredOutfits = offeredStock = null;
            guestCards.Clear();
        }

        static bool TrackGuest(Telepad pod)
        {
            if (!CoopSession.IsNativeClient || !NativeGuestScene.Ready || pod == null) return false;
            var proxy = pod.GetComponent<NativeProxySettings>();
            if (proxy == null || proxy.HostTarget <= 0 || !Grid.IsValidCell(proxy.AnchorCell)) return false;
            if (guestPod != pod || guestTarget != proxy.HostTarget)
            {
                if (requested || pendingUntil > 0) return false;
                ResetGuest();
                guestPod = pod;
                guestTarget = proxy.HostTarget;
                guestCell = proxy.AnchorCell;
            }
            observedUntil = Time.unscaledTime + 1.8f;
            return true;
        }

        static string PrintRoute() => "printing/" + guestTarget.ToString(CultureInfo.InvariantCulture);

        internal static void TickGuest()
        {
            if (requested) FitPrintingLayout();
            if (guestPod == null || !NativeGuestScene.Ready || !CoopSession.IsNativeClient || !CoopSession.NativeCommandsReady) return;
            float now = Time.unscaledTime;
            if (pendingUntil > 0)
            {
                if (now < pendingUntil) return;
                pendingUntil = 0;
            }
            if (now < nextPoll || !requested && now >= observedUntil) return;
            nextPoll = now + .65f;
            NativeManagement.ProxyCommand(PrintRoute() + (requested ? "" : "/status"), guestCell);
        }

        static void OpenGuest(Telepad pod)
        {
            if (!TrackGuest(pod) || pendingUntil > 0) return;
            requested = true;
            nextPoll = 0;
            TickGuest();
        }

        static void ConfirmGuest(ImmigrantScreen screen, bool reject)
        {
            if (!requested || guestRevision == null || pendingUntil > 0 || !CoopSession.NativeCommandsReady) return;
            string choice = "reject";
            if (!reject)
            {
                var selection = (List<ITelepadDeliverable>)Selected.GetValue(screen);
                if (selection == null || selection.Count != 1) return;
                int index = Array.IndexOf(guestOptions, selection[0]);
                if (index < 0) return;
                choice = index.ToString(CultureInfo.InvariantCulture);
                if (selection[0] is MinionStartingStats dupe)
                {
                    string name = dupe.Name == offeredNames[index] ? "-" : EncodeEdit(dupe.Name);
                    if (name != "-" && !NativeManagement.TryDecodeName(name, out _))
                    {
                        NativeGuestFeedback.Show("复制人名字须为非空纯文本，最多 50 个 UTF-8 字节。");
                        return;
                    }
                    string outfit = dupe.overrideOutfitID == offeredOutfits[index] ? "-" : EncodeEdit(dupe.overrideOutfitID);
                    if (name != "-" || outfit != "-") choice += "/" + name + "/" + outfit;
                }
            }
            pendingUntil = Time.unscaledTime + 5;
            NativeManagement.ProxyCommand(PrintRoute() + "/" + guestRevision + "/" + choice, guestCell);
        }

        static string EncodeEdit(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "")).Replace('/', '_');
        static string DecodeEdit(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value.Replace('_', '/')));

        internal static void Receive(string value)
        {
            if (guestTarget <= 0 || !NativeGuestScene.Ready) return;
            var fields = value.Split('/');
            if (fields.Length < 4 || NativeManagement.Number(fields[2]) != guestTarget) return;
            string status = fields[3];
            if (status == "accepted" || status == "rejected")
            {
                pendingUntil = 0;
                guestReady = false;
                CloseGuestScreen();
                return;
            }
            if (status == "changed" || status == "invalid")
            {
                if (pendingUntil <= 0) return;
                pendingUntil = 0;
                if (status == "changed")
                {
                    nextPoll = 0;
                    NativeGuestFeedback.Show("本轮候选已变化，正在读取最新状态。");
                }
                else NativeGuestFeedback.Show("主机拒绝了候选的名字或服装设置。");
                return;
            }
            if (pendingUntil > 0) return;
            if (status == "ready" || status == "cards" || status == "loading") guestReady = true;
            if (status == "cards" && requested && fields.Length == 7)
            {
                try
                {
                    var stock = Array.ConvertAll(fields[6].Split(','), DecodeEdit);
                    if (fields[4] != guestRevision) PresentGuestOffer(fields[4], PrintingCandidates.Read(fields[5]));
                    if (guestOptions != null && stock.Length == guestOptions.Length)
                    {
                        offeredStock = stock;
                        foreach (var card in guestCards.Keys)
                            if (card is CarePackageContainer package && package.Info != null) SetGuestStock(package);
                    }
                }
                catch (Exception e) when (e is FormatException || e is IOException || e is ArgumentException)
                {
                    CloseGuestScreen();
                    NativeGuestFeedback.Show("无法读取主机的打印候选。");
                }
            }
            else if (status == "waiting" || status == "missing")
            {
                guestReady = false;
                guestWait = fields.Length == 5 && int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                    ? seconds : 0;
                if (requested)
                {
                    CloseGuestScreen();
                    NativeGuestFeedback.Show("打印舱目前没有可领取的候选。");
                }
            }
        }

        static void PresentGuestOffer(string revision, ITelepadDeliverable[] options)
        {
            var screen = ImmigrantScreen.instance;
            if (screen == null || guestPod == null) return;
            ClearGuestOffer();
            guestRevision = revision;
            guestOptions = options;
            offeredNames = Array.ConvertAll(options, card => (card as MinionStartingStats)?.Name);
            offeredOutfits = Array.ConvertAll(options, card => (card as MinionStartingStats)?.overrideOutfitID);
            CancelReject.Invoke(screen, null);
            mounting = true;
            try { Initialize.Invoke(screen, new object[] { guestPod }); }
            finally { mounting = false; }
            if (!screen.gameObject.activeSelf) screen.Show();
        }

        static void CloseGuestScreen()
        {
            requested = false;
            var screen = ImmigrantScreen.instance;
            if (screen != null && screen.gameObject.activeSelf)
            {
                CancelReject.Invoke(screen, null);
                screen.Show(false);
            }
            ClearGuestOffer();
        }

        static void SetGuestStock(CarePackageContainer package)
        {
            if (offeredStock == null || !guestCards.TryGetValue(package, out var offer)) return;
            int index = Array.IndexOf(guestOptions, offer);
            if (index >= 0) ((LocText)SupplyCount.GetValue(package)).SetText(offeredStock[index]);
        }

        [HarmonyPatch(typeof(TelepadSideScreen), "Update")]
        static class SideScreen
        {
            static void Postfix(Telepad ___targetTelepad, KButton ___viewImmigrantsBtn, LocText ___viewImmigrantsBtnText,
                GameObject ___immigrantBtnAttentionIcon)
            {
                if (!TrackGuest(___targetTelepad)) return;
                ___viewImmigrantsBtn.isInteractable = guestReady;
                ___immigrantBtnAttentionIcon.SetActive(guestReady);
                ___viewImmigrantsBtnText.text = guestReady ? (string)STRINGS.UI.VIEWDUPLICANTS :
                    string.Format(STRINGS.UI.UISIDESCREENS.TELEPADSIDESCREEN.NEXTPRODUCTION, GameUtil.GetFormattedCycles(guestWait));
            }
        }

        [HarmonyPatch(typeof(ImmigrantScreen), "OnShow")]
        static class GuestScreenVisibility
        {
            static void Postfix(bool show)
            {
                if (show || !NativeGuestScene.Active) return;
                requested = false;
                ResetPrintingLayout();
                ClearGuestOffer();
            }
        }

        [HarmonyPatch(typeof(CharacterSelectionController), "InitializeContainers")]
        static class GuestCardLayout
        {
            static bool Prefix(CharacterSelectionController __instance)
            {
                if (!mounting || !(__instance is ImmigrantScreen screen)) return true;
                DisableProceed.Invoke(screen, null);
                var old = Cards(screen);
                if (old != null)
                {
                    foreach (var card in old)
                    {
                        card.GetGameObject().SetActive(false);
                        UnityEngine.Object.Destroy(card.GetGameObject());
                    }
                }
                screen.OnReplacedEvent = null;
                var cards = new List<ITelepadDeliverableContainer>();
                Containers.SetValue(screen, cards);
                Selected.SetValue(screen, new List<ITelepadDeliverable>());
                var parent = (GameObject)CardParent.GetValue(screen);
                foreach (var offer in guestOptions)
                {
                    ITelepadDeliverableContainer card;
                    if (offer is MinionStartingStats)
                    {
                        var dupe = Util.KInstantiateUI<CharacterContainer>(((CharacterContainer)DupePrefab.GetValue(screen)).gameObject, parent);
                        dupe.SetController(screen);
                        card = dupe;
                    }
                    else
                    {
                        var supply = Util.KInstantiateUI<CarePackageContainer>(((CarePackageContainer)SupplyPrefab.GetValue(screen)).gameObject, parent);
                        supply.SetController(screen);
                        card = supply;
                    }
                    card.GetGameObject().transform.SetAsLastSibling();
                    guestCards.Add(card, offer);
                    cards.Add(card);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(CharacterContainer), nameof(CharacterContainer.GenerateCharacter))]
        static class GuestDupeCard
        {
            static bool Prefix(CharacterContainer __instance, CharacterSelectionController ___controller)
            {
                if (!NativeGuestScene.Active || !(___controller is ImmigrantScreen)) return true;
                if (!guestCards.TryGetValue(__instance, out var offer)) return false;
                var stats = (MinionStartingStats)offer;
                string outfit = stats.overrideOutfitID;
                __instance.SetMinion(stats);
                var outfits = (List<Option<ClothingOutfitTarget>>)Outfits.GetValue(__instance);
                OutfitIndex.SetValue(__instance, outfits.FindIndex(item => item.IsSome() && item.Unwrap().OutfitId == outfit));
                RefreshOutfit.Invoke(__instance, null);
                stats.overrideOutfitID = outfit;
                return false;
            }
        }

        [HarmonyPatch(typeof(CarePackageContainer), "GenerateCharacter")]
        static class GuestSupplyCard
        {
            static bool Prefix(CarePackageContainer __instance, CharacterSelectionController ___controller,
                ref CarePackageInfo ___info, KToggle ___selectButton)
            {
                if (!NativeGuestScene.Active || !(___controller is ImmigrantScreen)) return true;
                if (!guestCards.TryGetValue(__instance, out var offer)) return false;
                var package = (CarePackageContainer.CarePackageInstanceData)offer;
                ___info = package.info;
                __instance.carePackageInstanceData = package;
                SupplyAnimation.Invoke(__instance, null);
                SupplyText.Invoke(__instance, null);
                SetGuestStock(__instance);
                ___selectButton.ClearOnClick();
                ___selectButton.onClick += __instance.SelectDeliverable;
                return false;
            }
        }
    }
}
