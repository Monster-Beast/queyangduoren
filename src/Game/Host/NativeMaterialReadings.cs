using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Queyang.Game
{
    internal static class NativeMaterialReadings
    {
        sealed class Germs
        {
            public int Count;
            public byte Type;
        }

        static ConditionalWeakTable<PrimaryElement, Germs> observed = new ConditionalWeakTable<PrimaryElement, Germs>();

        internal static MaterialSample Capture(PrimaryElement element)
        {
            int count = element.DiseaseCount;
            return new MaterialSample { Element = (int)element.ElementID, Mass = element.Mass,
                Temperature = element.Temperature, Germs = count, GermType = count > 0 ? element.DiseaseIdx : (byte)0 };
        }

        internal static void Apply(PrimaryElement element, MaterialSample material)
        {
            if (element == null || (int)element.ElementID != material.Element) return;
            var reading = observed.GetOrCreateValue(element);
            reading.Count = material.Germs;
            reading.Type = material.Germs == 0 ? byte.MaxValue : material.GermType;
        }

        // These two properties feed the native tooltip. Do not enqueue disease
        // simulation messages on the frozen guest when replacing a reading.
        [HarmonyPatch(typeof(PrimaryElement), "get_DiseaseCount")]
        static class Count
        {
            static bool Prefix(PrimaryElement __instance, ref int __result)
            {
                if (!CoopSession.IsNativeClient || !observed.TryGetValue(__instance, out var value)) return true;
                __result = value.Count;
                return false;
            }
        }

        [HarmonyPatch(typeof(PrimaryElement), "get_DiseaseIdx")]
        static class Type
        {
            static bool Prefix(PrimaryElement __instance, ref byte __result)
            {
                if (!CoopSession.IsNativeClient || !observed.TryGetValue(__instance, out var value)) return true;
                __result = value.Type;
                return false;
            }
        }

        internal static void Reset() => observed = new ConditionalWeakTable<PrimaryElement, Germs>();
    }
}
