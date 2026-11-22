using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        const int MaxRenameBytes = 50;
        static readonly UTF8Encoding RenameEncoding = new UTF8Encoding(false, true);
        static readonly AccessTools.FieldRef<SpeedControlScreen, int> PauseCount =
            AccessTools.FieldRefAccess<SpeedControlScreen, int>("pauseCount");
        static GameObject buildingNameTarget;
        static string buildingNameVersion;

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

        internal static bool RequestObjectName(GameObject target, string name)
        {
            int id = SaveId(target);
            int cell = Grid.InvalidCell;
            bool dupe = id > 0;
            if (!dupe && !TryBuildingNameTarget(target,out cell,out id)) return false;
            if (!ValidName(name))
            {
                NativeGuestFeedback.Show($"名字须为非空纯文本，最多 {MaxRenameBytes} 个 UTF-8 字节。");
                return true;
            }
            string encoded = Convert.ToBase64String(RenameEncoding.GetBytes(name)).Replace('/', '_');
            string visibleName = target.GetComponent<KSelectable>()?.GetProperName() ?? "";
            if (dupe)
                Send($"name/{id}/{Digest(visibleName)}/{encoded}");
            else
            {
                Grid.CellToXY(cell,out int x,out int y);
                string revision = target == buildingNameTarget ? buildingNameVersion : null;
                Send($"buildingname/{id}/{revision ?? Digest(visibleName)}/{encoded}",x,y);
            }
            return true;
        }

        [HarmonyPatch(typeof(DetailsScreen), nameof(DetailsScreen.UpdateTitle))]
        static class EditableGuestTitle
        {
            static void Postfix(DetailsScreen __instance, EditableTitleBar ___TabTitle)
            {
                if (NativeGuestScene.Ready && __instance.target != null &&
                    (__instance.target.GetComponent<NativeDupeIdentityMarker>() != null ||
                     TryBuildingNameTarget(__instance.target,out _,out _)))
                    ___TabTitle.SetUserEditable(true);
            }
        }

        internal static bool NameableBuilding(GameObject target) => target != null &&
            target.GetComponent<UserNameable>() != null &&
            target.GetComponent<CommandModule>() == null && target.GetComponent<ClustercraftExteriorDoor>() == null;

        static bool TryBuildingNameTarget(GameObject target, out int cell, out int id)
        {
            var proxy = target?.GetComponent<NativeBuildingIdentity>();
            cell = proxy == null ? target?.GetComponent<BuildingComplete>()?.GetCell() ?? Grid.InvalidCell : proxy.Cell;
            id = proxy == null ? target?.GetComponent<KPrefabID>()?.InstanceID ?? 0 : proxy.SaveId;
            if (!Grid.IsValidCell(cell) || id <= 0) return false;
            if (proxy == null) return NameableBuilding(target);
            if (!proxy.Complete) return false;
            var def = Assets.GetBuildingDef(target.PrefabID().Name);
            return NameableBuilding(def?.BuildingComplete) && NativeWorldMirror.GetBuildingSelectable(cell,id)?.gameObject == target;
        }

        static void PollBuildingName(KSelectable selected)
        {
            var target = selected == null ? null : selected.gameObject;
            if (target != buildingNameTarget)
            {
                buildingNameTarget = target;
                buildingNameVersion = null;
            }
            if (!TryBuildingNameTarget(target,out int cell,out int id)) return;
            Grid.CellToXY(cell,out int x,out int y);
            Send("buildingname/"+id,x,y);
        }

        internal static string ApplyBuildingName(GameObject target, int cell, string[] fields)
        {
            if (!NameableBuilding(target) || fields.Length != 3 && fields.Length != 5) return Conflict;
            var nameable = target.GetComponent<UserNameable>();
            string name = target.GetComponent<KSelectable>().GetProperName() ?? "";
            if (fields.Length == 5)
            {
                if (fields[3] != Digest(name) || !TryDecodeName(fields[4],out name)) return Conflict;
                nameable.SetName(name);
            }
            // Send the full revision even when the viewport compacts long display names.
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(name)).Replace('/','_');
            return encoded.Length <= 2048 ? Prefix+"bn/"+cell+"/"+fields[2]+"/"+Digest(name)+"/"+encoded : Conflict;
        }

        static void ReceiveBuildingName(string[] fields)
        {
            if (fields.Length != 6) return;
            var selectable = NativeWorldMirror.GetBuildingSelectable(Number(fields[2]),Number(fields[3]));
            if (selectable == null) return;
            string name = RenameEncoding.GetString(Convert.FromBase64String(fields[5].Replace('_','/')));
            if (selectable.gameObject == buildingNameTarget) buildingNameVersion = fields[4];
            if (selectable.GetProperName() == name) return;
            selectable.SetName(name);
            if (DetailsScreen.Instance?.target == selectable.gameObject) DetailsScreen.Instance.UpdateTitle();
        }

        static bool ValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsControl(c) || c == '<' || c == '>')) return false;
            try { return RenameEncoding.GetByteCount(name) <= MaxRenameBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        static string ApplyDupeName(string[] fields)
        {
            if (!CoopSession.IsHosting || fields.Length != 5) return Conflict;
            var dupe = FindDupe(Number(fields[2]));
            if (dupe == null || ClusterUtil.GetMyWorldId(dupe) != ClusterManager.Instance.activeWorldId ||
                Digest(dupe.GetProperName() ?? "") != fields[3]) return Conflict;
            if (!TryDecodeName(fields[4],out string name)) return Conflict;
            dupe.SetName(name);
            ScheduleScreen.Instance?.Trigger(1980521255);
            return "OK";
        }

        static bool TryDecodeName(string value, out string name)
        {
            name = null;
            try { name = RenameEncoding.GetString(Convert.FromBase64String(value.Replace('_','/'))); }
            catch (FormatException) { return false; }
            catch (DecoderFallbackException) { return false; }
            return ValidName(name);
        }
    }
}
