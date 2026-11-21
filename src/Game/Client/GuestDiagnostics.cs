using System.IO;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class GuestDiagnostics
    {
        private static volatile bool active;
        private static int steps, decisions, chores, saves, views, nativeWorlds;

        public static void Start()
        {
            if (active) return;
            steps = decisions = chores = saves = views = nativeWorlds = 0;
            active = true;
        }

        public static string Summary => "simStep=" + steps + " findChore=" + decisions + " setChore=" + chores +
            " saveAttempts=" + saves + " worldStarts=" + nativeWorlds + " views=" + views;

        public static void End()
        {
            if (!active) return;
            active = false;
            Debug.Log("[QueyangCoop] guest-closed " + Summary);
        }

        public static void AppliedView() { if (active) views++; }

        // Stop guest mode before a newly requested local game can initialize. Its own
        // simulation and Save flow then follow the ordinary single-player lifecycle.
        [HarmonyPatch(typeof(global::Game), "OnPrefabInit")]
        private static class EnterWorld
        {
            private static void Prefix()
            {
                if (!active) return;
                nativeWorlds++;
                CoopSession.Disconnect();
            }
        }

        // StepTheSim returns a native pointer. An observer preserves its return contract.
        [HarmonyPatch(typeof(global::Game), "StepTheSim", typeof(float))]
        private static class SimStep
        {
            private static void Prefix() { if (active) Interlocked.Increment(ref steps); }
        }

        [HarmonyPatch(typeof(ChoreConsumer), "FindNextChore", new[] { typeof(Chore.Precondition.Context) }, new[] { ArgumentType.Out })]
        private static class FindChore
        {
            private static void Prefix() { if (active) Interlocked.Increment(ref decisions); }
        }

        [HarmonyPatch(typeof(ChoreDriver), "SetChore", typeof(Chore.Precondition.Context))]
        private static class SetChore
        {
            private static void Prefix() { if (active) Interlocked.Increment(ref chores); }
        }

        [HarmonyPatch(typeof(global::Game), "Save", typeof(BinaryWriter))]
        private static class Save
        {
            private static bool Prefix()
            {
                if (!active) return true;
                Interlocked.Increment(ref saves);
                return false;
            }
        }
    }
}
