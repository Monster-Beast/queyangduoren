using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativePrinting
    {
        static readonly FieldInfo CloseButton = AccessTools.Field(typeof(ImmigrantScreen), "closeButton");
        static readonly FieldInfo RejectButton = AccessTools.Field(typeof(ImmigrantScreen), "rejectButton");
        static readonly FieldInfo ProceedButton = AccessTools.Field(typeof(CharacterSelectionController), "proceedButton");
        static readonly FieldInfo ModalBackground = AccessTools.Field(typeof(KModalScreen), "backgroundRectTransform");
        static readonly Vector3[] printingCorners = new Vector3[4];

        static RectTransform fittedPrintingContent, fittedPrintingBackground;
        static Vector3 contentScale, backgroundScale;
        static int fittedWidth, fittedHeight;

        static void FitPrintingLayout()
        {
            var screen = ImmigrantScreen.instance;
            int width = Screen.width, height = Screen.height;
            if (screen == null || !screen.gameObject.activeInHierarchy || guestOptions == null ||
                (fittedWidth == width && fittedHeight == height)) return;
            ResetPrintingLayout();
            Canvas.ForceUpdateCanvases();
            var close = (RectTransform)((KButton)CloseButton.GetValue(screen)).transform;
            var reject = (RectTransform)((KButton)RejectButton.GetValue(screen)).transform;
            var proceed = (RectTransform)((KButton)ProceedButton.GetValue(screen)).transform;
            var cards = ((GameObject)CardParent.GetValue(screen)).GetComponent<RectTransform>();
            var content = cards.parent;
            while (content != screen.transform && (!close.IsChildOf(content) || !reject.IsChildOf(content) || !proceed.IsChildOf(content)))
                content = content.parent;
            var canvas = screen.GetComponentInParent<Canvas>()?.rootCanvas;
            var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var pivot = RectTransformUtility.WorldToScreenPoint(camera, content.position);
            var bounds = new Bounds(pivot, Vector3.zero);
            Include(close);
            Include(reject);
            Include(proceed);
            foreach (var card in Cards(screen)) Include((RectTransform)card.GetGameObject().transform);
            const float padding = 10;
            float fit = Mathf.Min(1,
                (pivot.x - padding) / Mathf.Max(1, pivot.x - bounds.min.x),
                (width - padding - pivot.x) / Mathf.Max(1, bounds.max.x - pivot.x),
                (pivot.y - padding) / Mathf.Max(1, pivot.y - bounds.min.y),
                (height - padding - pivot.y) / Mathf.Max(1, bounds.max.y - pivot.y));
            fittedWidth = width;
            fittedHeight = height;
            if (fit <= 0 || fit >= 1) return;
            fittedPrintingContent = (RectTransform)content;
            contentScale = content.localScale;
            content.localScale = Vector3.Scale(contentScale, new Vector3(fit, fit, 1));
            if (content == screen.transform)
            {
                // Keep the stock full-window modal backdrop and input blocking.
                fittedPrintingBackground = (RectTransform)ModalBackground.GetValue(screen);
                backgroundScale = fittedPrintingBackground.localScale;
                fittedPrintingBackground.localScale = Vector3.Scale(backgroundScale, new Vector3(1 / fit, 1 / fit, 1));
            }

            void Include(RectTransform rect)
            {
                rect.GetWorldCorners(printingCorners);
                foreach (var corner in printingCorners)
                    bounds.Encapsulate(RectTransformUtility.WorldToScreenPoint(camera, corner));
            }
        }

        static void ResetPrintingLayout()
        {
            if (fittedPrintingContent != null) fittedPrintingContent.localScale = contentScale;
            if (fittedPrintingBackground != null) fittedPrintingBackground.localScale = backgroundScale;
            fittedPrintingContent = fittedPrintingBackground = null;
            fittedWidth = fittedHeight = 0;
        }
    }
}
