using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    public sealed class ModEntry : KMod.UserMod2
    {
        internal static string ModId;
        internal static bool PatchesLoaded { get; private set; }
        public override void OnLoad(Harmony harmony)
        {
            ModId = mod.staticID;
            // ChangeList is a const in the game DLL. Read the installed metadata, not an inlined value.
            var buildField = typeof(KleiVersion).GetField("ChangeList");
            if (buildField == null || !buildField.IsLiteral ||
                System.Convert.ToUInt32(buildField.GetRawConstantValue()) != 764025u)
            {
                Debug.LogWarning("[QueyangCoop] Unsupported game build. This package targets 764025.");
                return;
            }
            try { base.OnLoad(harmony); PatchesLoaded = true; }
            catch (System.Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                Debug.LogError("[QueyangCoop] Multiplayer patches could not load: " + error);
            }
        }
    }

    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    internal static class MenuBootstrap
    {
        static CoopPanel panel;
        static void Postfix()
        {
            if (panel != null) return;
            var go = new GameObject("QueyangCoop");
            Object.DontDestroyOnLoad(go);
            panel = go.AddComponent<CoopPanel>();
            Debug.Log("[QueyangCoop] menu ready");
        }
    }

    // Joining uses ONI's normal serializer without generating a new entry in the player's
    // colony gallery or scheduling an asynchronous preview for a temporary network file.
    [HarmonyPatch(typeof(RetireColonyUtility), "SaveColonySummaryData")]
    internal static class SkipJoinSummary
    {
        static bool Prefix() => !CoopSession.CapturingJoinSave;
    }
    [HarmonyPatch(typeof(Timelapser), "SaveColonyPreview")]
    internal static class SkipJoinPreview
    {
        static bool Prefix() => !CoopSession.CapturingJoinSave;
    }
}
