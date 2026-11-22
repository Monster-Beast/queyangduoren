using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativePrinting
    {
        const string Reply = "NM1/printing/";
        static readonly FieldInfo Containers = AccessTools.Field(typeof(CharacterSelectionController), "containers");
        static readonly FieldInfo Selected = AccessTools.Field(typeof(CharacterSelectionController), "selectedDeliverables");
        static readonly FieldInfo Pod = AccessTools.Field(typeof(ImmigrantScreen), "telepad");
        static readonly MethodInfo Initialize = AccessTools.Method(typeof(ImmigrantScreen), "Initialize");
        static readonly MethodInfo Proceed = AccessTools.Method(typeof(ImmigrantScreen), "OnProceed");
        static readonly MethodInfo Reject = AccessTools.Method(typeof(ImmigrantScreen), "OnRejectionConfirmed");
        static readonly MethodInfo CancelReject = AccessTools.Method(typeof(ImmigrantScreen), "OnRejectionCancelled");
        static readonly MethodInfo SupplyAvailable = AccessTools.Method(typeof(CarePackageContainer), "GetCurrentQuantity");
        static readonly FieldInfo Outfits = AccessTools.Field(typeof(CharacterContainer), "allAvailableClothingOutfits");

        static List<ITelepadDeliverableContainer> endedContainers;
        static ITelepadDeliverableContainer[] hostCards;
        static ITelepadDeliverable[] hostOptions;
        static string hostPayload, hostRevision;
        static ImmigrantScreen preparing;
        static CanvasGroup hiddenGroup;
        static float savedAlpha;
        static bool savedRaycasts, savedInteractable;

        static bool Available(Telepad pod) => pod != null && Immigration.Instance?.ImmigrantsAvailable == true &&
            pod.GetComponent<Operational>()?.IsOperational == true &&
            (GameFlowManager.Instance == null || !GameFlowManager.Instance.IsGameOver());

        static List<ITelepadDeliverableContainer> Cards(ImmigrantScreen screen) =>
            (List<ITelepadDeliverableContainer>)Containers.GetValue(screen);

        static ITelepadDeliverable Delivery(ITelepadDeliverableContainer card)
        {
            if (card is CharacterContainer dupe) return dupe.Stats;
            return (card as CarePackageContainer)?.carePackageInstanceData;
        }

        static void DiscardEndedCards(ImmigrantScreen screen)
        {
            var cards = Cards(screen);
            if (cards == null || cards != endedContainers) return;
            foreach (var card in cards) UnityEngine.Object.Destroy(card.GetGameObject());
            cards.Clear();
            endedContainers = null;
            CancelReject.Invoke(screen, null);
        }

        static bool ReadCards(ImmigrantScreen screen)
        {
            var cards = Cards(screen);
            if (cards == null || cards == endedContainers || cards.Count == 0) return false;
            var ordered = cards.OrderBy(card => card.GetGameObject().transform.GetSiblingIndex()).ToArray();
            var options = ordered.Select(Delivery).ToArray();
            if (options.Any(option => option == null)) return false;
            string payload = PrintingCandidates.Write(options);
            if (hostOptions == null || !hostOptions.SequenceEqual(options) || hostPayload != payload)
            {
                hostOptions = options;
                hostCards = ordered;
                hostPayload = payload;
                hostRevision = Guid.NewGuid().ToString("N");
            }
            return true;
        }

        static void Prepare(ImmigrantScreen screen, Telepad pod)
        {
            if (preparing != null || screen.gameObject.activeSelf) return;
            DiscardEndedCards(screen);
            var cards = Cards(screen);
            if (cards != null && cards.Count != 0) return;
            Initialize.Invoke(screen, new object[] { pod });
            preparing = screen;
            hiddenGroup = screen.gameObject.FindOrAddUnityComponent<CanvasGroup>();
            savedAlpha = hiddenGroup.alpha;
            savedRaycasts = hiddenGroup.blocksRaycasts;
            savedInteractable = hiddenGroup.interactable;
            hiddenGroup.alpha = 0;
            hiddenGroup.blocksRaycasts = false;
            hiddenGroup.interactable = false;
            // The vanilla card coroutines need an active parent. Avoid Show(), which plays
            // opening audio and makes the room host enter the candidate screen.
            screen.gameObject.SetActive(true);
            global::Game.Instance.StartCoroutine(FinishPreparing(screen, pod));
        }

        static IEnumerator FinishPreparing(ImmigrantScreen screen, Telepad pod)
        {
            float deadline = Time.realtimeSinceStartup + 5f;
            while (screen != null && screen == preparing && Available(pod) && Time.realtimeSinceStartup < deadline)
            {
                if (Cards(screen)?.All(card => Delivery(card) != null) == true)
                {
                    // The duplicant cards populate attribute rows on the next frame.
                    yield return SequenceUtil.WaitForEndOfFrame;
                    break;
                }
                yield return null;
            }
            if (screen == preparing) RestoreHostScreen(true);
        }

        static void RestoreHostScreen(bool hide)
        {
            var screen = preparing;
            preparing = null;
            if (screen != null && hide) screen.gameObject.SetActive(false);
            if (hiddenGroup != null)
            {
                hiddenGroup.alpha = savedAlpha;
                hiddenGroup.blocksRaycasts = savedRaycasts;
                hiddenGroup.interactable = savedInteractable;
            }
            hiddenGroup = null;
        }

        internal static void ResetHost()
        {
            RestoreHostScreen(true);
            hostOptions = null;
            hostCards = null;
            hostRevision = hostPayload = null;
        }

        internal static string Execute(string[] route, int x, int y)
        {
            bool status = route.Length == 4 && route[3] == "status";
            bool decision = route.Length == 5 || route.Length == 7;
            if (route.Length != 3 && !status && !decision) return "ERROR Invalid printing request";
            int target = NativeManagement.Number(route[2]);
            var pod = HostWorld.FindPrintingPod(target, x, y);
            string head = Reply + target.ToString(CultureInfo.InvariantCulture) + "/";
            if (pod == null) return head + "missing";
            if (!Available(pod))
                return head + (decision ? "changed" : "waiting/" +
                    Math.Max(0, Math.Ceiling(pod.GetTimeRemaining())).ToString(CultureInfo.InvariantCulture));
            if (status) return head + "ready";
            var screen = ImmigrantScreen.instance;
            if (screen == null) return head + "loading";
            if (!ReadCards(screen))
            {
                if (decision) return head + "changed";
                Prepare(screen, pod);
                return head + "loading";
            }
            if (route.Length == 3)
            {
                var inventory = ClusterManager.Instance.activeWorld.worldInventory;
                var counts = hostCards.Select(card => card is CarePackageContainer package
                    ? EncodeEdit((string)SupplyAvailable.Invoke(package, new object[] { inventory })) : "");
                return head + "cards/" + hostRevision + "/" + hostPayload + "/" + string.Join(",", counts);
            }
            if (route[3] != hostRevision) return head + "changed";
            bool reject = route[4] == "reject";
            int index = NativeManagement.Number(route[4]);
            if (!reject && (index < 0 || index >= hostOptions.Length)) return head + "changed";

            var option = reject ? null : hostOptions[index];
            if (route.Length == 7 && (!(option is MinionStartingStats dupe) ||
                !ApplyCandidateEdits(dupe, (CharacterContainer)hostCards[index], route[5], route[6])))
                return head + "invalid";
            Pod.SetValue(screen, pod);
            if (reject) Reject.Invoke(screen, null);
            else
            {
                var selection = (List<ITelepadDeliverable>)Selected.GetValue(screen);
                selection.Clear();
                selection.Add(option);
                CancelReject.Invoke(screen, null);
                Proceed.Invoke(screen, null);
            }
            return head + (reject ? "rejected" : "accepted");
        }

        static bool ApplyCandidateEdits(MinionStartingStats dupe, CharacterContainer card, string encodedName, string encodedOutfit)
        {
            string name = dupe.Name;
            string outfit = dupe.overrideOutfitID;
            if (encodedName != "-" && !NativeManagement.TryDecodeName(encodedName, out name)) return false;
            if (encodedOutfit != "-")
            {
                try { outfit = DecodeEdit(encodedOutfit); }
                catch (FormatException) { return false; }
                if (outfit.Length == 0) outfit = null;
                else if (!((List<Option<ClothingOutfitTarget>>)Outfits.GetValue(card))
                    .Any(item => item.IsSome() && item.Unwrap().OutfitId == outfit)) return false;
            }
            dupe.Name = name;
            dupe.overrideOutfitID = outfit;
            return true;
        }

        [HarmonyPatch(typeof(ImmigrantScreen), nameof(ImmigrantScreen.InitializeImmigrantScreen))]
        static class OpenScreen
        {
            static bool Prefix(Telepad __0)
            {
                if (NativeGuestScene.Active)
                {
                    OpenGuest(__0);
                    return false;
                }
                var screen = ImmigrantScreen.instance;
                if (CoopSession.IsHosting && screen != null)
                {
                    RestoreHostScreen(false);
                    DiscardEndedCards(screen);
                }
                return true;
            }
        }

        [HarmonyPatch]
        static class Confirm
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return Proceed;
                yield return Reject;
            }

            static bool Prefix(ImmigrantScreen __instance, MethodBase __originalMethod)
            {
                if (NativeGuestScene.Active)
                {
                    ConfirmGuest(__instance, __originalMethod.Name == "OnRejectionConfirmed");
                    return false;
                }
                if (!CoopSession.IsHosting) return true;
                if (!Available(__instance.Telepad)) return false;
                return __originalMethod.Name != "OnProceed" ||
                    ((List<ITelepadDeliverable>)Selected.GetValue(__instance))?.Count == 1;
            }
        }

        [HarmonyPatch(typeof(Immigration), nameof(Immigration.EndImmigration))]
        static class EndRound
        {
            static void Postfix()
            {
                if (!CoopSession.IsHosting) return;
                hostOptions = null;
                hostCards = null;
                hostRevision = hostPayload = null;
                var screen = ImmigrantScreen.instance;
                if (screen != null) endedContainers = Cards(screen);
            }
        }

        [HarmonyPatch(typeof(ImmigrantScreen), nameof(ImmigrantScreen.DestroyInstance))]
        static class Unload
        {
            static void Postfix()
            {
                ResetHost();
                endedContainers = null;
            }
        }
    }

    public static partial class HostWorld
    {
        internal static Telepad FindPrintingPod(int target, int x, int y)
        {
            if (target <= 0 || World == null || !VisibleCell(World, x, y, out int cell)) return null;
            return Components.Telepads.Items.FirstOrDefault(pod => pod != null && Grid.PosToCell(pod) == cell &&
                TargetKeys.TryGetValue(pod.gameObject, out var key) && key.Value == target);
        }
    }
}
