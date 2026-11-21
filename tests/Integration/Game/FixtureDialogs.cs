using System;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class FixtureDialogs
    {
        readonly Action<string> log;
        bool restarting;
        internal bool Restarting => restarting;
        internal FixtureDialogs(Action<string> log) { this.log = log; }

        // ConfirmDialogScreen stores these LocText references privately. Read the
        // rendered text so a restart request or mixed mod errors cannot match.
        static string DialogText(ConfirmDialogScreen dialog, string field)
        {
            object label = AccessTools.Field(typeof(ConfirmDialogScreen), field).GetValue(dialog);
            return ((string)AccessTools.Property(label.GetType(), "text").GetValue(label, null)).Trim();
        }

        internal bool Check(bool loadedVanillaHost = false)
        {
            if (restarting) return true;
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
                    bool installs = title == STRINGS.UI.FRONTEND.MOD_DIALOGS.MOD_EVENTS.TITLE.text && lines.Length > 0 &&
                        lines.All(s => s.StartsWith(installed, StringComparison.Ordinal) && s.Length > installed.Length);
                    string difference = "\n" + STRINGS.UI.FRONTEND.MOD_EVENTS.EXPECTED_DISABLED + ": " + fixture + "\n";
                    bool enabledFixture = loadedVanillaHost && title == STRINGS.UI.FRONTEND.MOD_DIALOGS.SAVE_GAME_MODS_DIFFER.TITLE.text &&
                        normalized == string.Format(STRINGS.UI.FRONTEND.MOD_DIALOGS.SAVE_GAME_MODS_DIFFER.MESSAGE.text, difference).Trim();
                    if ((!installs && !enabledFixture) || buttons.Length != 1 || buttons[0] != buttonObject.GetComponent<KButton>())
                        throw new Exception("Unexpected confirmation dialog: " + title + " / " + message);
                    buttons[0].SignalClick(KKeyCode.Mouse0);
                    log(enabledFixture ? "NATIVE_VANILLA_SAVE_MOD_DIFFERENCE_CONFIRMED" : "NATIVE_INSTALL_NOTIFICATION_CONFIRMED count=" + lines.Length);
                    return true;
                }
                if (screen is CustomizableDialogScreen || screen is InfoDialogScreen)
                    throw new Exception("Unexpected game dialog: " + screen.GetType().Name + " / " + screen.name);
            }
            return false;
        }

    }
}
