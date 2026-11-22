using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static readonly UTF8Encoding RenameEncoding = new UTF8Encoding(false, true);
        static readonly AccessTools.FieldRef<SpeedControlScreen, int> PauseCount =
            AccessTools.FieldRefAccess<SpeedControlScreen, int>("pauseCount");

        internal static void RequestClock(bool pause, int speed = 0, bool relative = false, bool cycle = false)
        {
            var view = CoopSession.NativeView;
            var controls = SpeedControlScreen.Instance;
            if (view == null || controls == null) return;
            if (pause)
            {
                Send("clock/pause/" + (view.Paused ? "1/0" : "0/1"));
                return;
            }
            int current = view.Speed == Mathf.RoundToInt(controls.normalSpeed) ? 0 :
                view.Speed == Mathf.RoundToInt(controls.fastSpeed) ? 1 : 2;
            int next = cycle ? (current + 1) % 3 : relative ? Mathf.Clamp(current + speed, 0, 2) : speed;
            if (next >= 0 && next <= 2 && next != current) Send($"clock/speed/{current}/{next}");
        }

        static string ApplyClock(string[] fields)
        {
            if (!CoopSession.IsHosting || fields.Length != 5) return Conflict;
            var controls = SpeedControlScreen.Instance;
            if (controls == null) return Conflict;
            int expected = Number(fields[3]), next = Number(fields[4]);
            switch (fields[2])
            {
                case "speed":
                    if (next < 0 || next > 2 || expected != controls.GetSpeed()) return Conflict;
                    controls.SetSpeed(next);
                    break;
                case "pause":
                    if (next < 0 || next > 1 || expected != (controls.IsPaused ? 1 : 0)) return Conflict;
                    if (next == 0 && PauseCount(controls) > 1) return "ERROR 房主还有其他暂停来源";
                    if (next != expected) controls.TogglePause();
                    break;
                default:
                    return Conflict;
            }
            return "OK";
        }

        internal static bool RequestDupeName(GameObject target, string name)
        {
            int id = SaveId(target);
            if (id <= 0) return false;
            if (!ValidDupeName(name))
            {
                NativeGuestFeedback.Show("名字须为非空纯文本，最多 58 个 UTF-8 字节。");
                return true;
            }
            var oldName = target.GetComponent<KSelectable>()?.GetProperName();
            string encoded = Convert.ToBase64String(RenameEncoding.GetBytes(name)).Replace('/', '_');
            Send($"name/{id}/{Digest(oldName ?? "")}/{encoded}");
            return true;
        }

        [HarmonyPatch(typeof(DetailsScreen), nameof(DetailsScreen.UpdateTitle))]
        static class ProjectedDupeRename
        {
            static void Postfix(DetailsScreen __instance, EditableTitleBar ___TabTitle)
            {
                if (NativeGuestScene.Ready && __instance.target != null &&
                    __instance.target.GetComponent<NativeDupeIdentityMarker>() != null)
                    ___TabTitle.SetUserEditable(true);
            }
        }

        static bool ValidDupeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsControl(c) || c == '<' || c == '>')) return false;
            try { return RenameEncoding.GetByteCount(name) <= 58; }
            catch (EncoderFallbackException) { return false; }
        }

        static string ApplyDupeName(string[] fields)
        {
            if (!CoopSession.IsHosting || fields.Length != 5) return Conflict;
            var dupe = FindDupe(Number(fields[2]));
            if (dupe == null || ClusterUtil.GetMyWorldId(dupe) != ClusterManager.Instance.activeWorldId ||
                Digest(dupe.GetProperName() ?? "") != fields[3]) return Conflict;
            string name;
            try { name = RenameEncoding.GetString(Convert.FromBase64String(fields[4].Replace('_', '/'))); }
            catch (FormatException) { return Conflict; }
            catch (DecoderFallbackException) { return Conflict; }
            if (!ValidDupeName(name)) return Conflict;
            dupe.SetName(name);
            ScheduleScreen.Instance?.Trigger(1980521255);
            return "OK";
        }
    }
}
