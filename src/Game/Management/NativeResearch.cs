using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static readonly FieldInfo ActiveTech = AccessTools.DeclaredField(typeof(Research), "activeResearch");
        static readonly FieldInfo QueuedTech = AccessTools.DeclaredField(typeof(Research), "queuedTech");
        static readonly FieldInfo Completed = AccessTools.DeclaredField(typeof(TechInstance), "complete");
        static readonly FieldInfo Inventory = AccessTools.DeclaredField(typeof(TechInstance), "progressInventory");
        static readonly MethodInfo RefreshResearch = AccessTools.DeclaredMethod(typeof(ResearchScreen), "OnActiveResearchChanged");
        static readonly MethodInfo RefreshUnlock = AccessTools.DeclaredMethod(typeof(PlanScreen), "OnResearchComplete");
        static string QueueVersion() => Digest(string.Join(",", Research.Instance.GetResearchQueue().Select(q => q.tech.Id)));
        static string ResearchRequest(string[] p)
        {
            var r = Research.Instance;
            if (r == null) return Conflict;
            var techs = Db.Get().Techs.resources;
            if (p[1] == "tech")
            {
                if (p.Length != 5 || p[4] != QueueVersion()) return Conflict;
                var tech = techs.FirstOrDefault(t => t.Id == p[3]);
                if (tech == null || tech.Disabled || !tech.FoundNode || tech.IsComplete()) return Conflict;
                if (p[2] == "select") r.SetActiveResearch(tech, true);
                else if (p[2] == "cancel" && r.GetResearchQueue().Any(q => q.tech == tech)) r.CancelResearch(tech, true);
                else return Conflict;
            }
            else if (p.Length != 2) return Conflict;

            // Both peers validated the same build and mod fingerprint. The tech database has
            // a stable ordered list on each side. Sort explicitly to avoid initialization order.
            var all = techs.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            using (var s = new MemoryStream())
            using (var w = new BinaryWriter(s))
            {
                w.Write((ushort)all.Length);
                foreach (var t in all)
                {
                    var instance = r.Get(t);
                    bool complete = instance != null && instance.IsComplete();
                    w.Write(complete);
                    if (complete) continue;
                    var inv = instance == null ? null : (ResearchPointInventory)Inventory.GetValue(instance);
                    foreach (var id in t.costsByResearchTypeID.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        float n = 0;
                        inv?.PointsByTypeID.TryGetValue(id, out n);
                        w.Write(n);
                    }
                }
                var queue = r.GetResearchQueue();
                w.Write((ushort)queue.Count);
                foreach (var q in queue) w.Write((ushort)Array.IndexOf(all, q.tech));
                w.Write((short)Array.IndexOf(all, r.GetActiveResearch()?.tech));
                if (s.Length > 5900) return "ERROR Research state exceeds message limit";
                return Prefix + "r/" + Convert.ToBase64String(s.ToArray()).Replace('/', '_');
            }
        }

        static void ApplyResearch(string data)
        {
            if (data == lastResearch) return;
            var all = Db.Get().Techs.resources.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            var r = Research.Instance;
            using (var s = new MemoryStream(Convert.FromBase64String(data.Replace('_', '/'))))
            using (var reader = new BinaryReader(s))
            {
                if (reader.ReadUInt16() != all.Length) return;
                var unlocks=new List<Tech>();
                foreach (var tech in all)
                {
                    bool done = reader.ReadBoolean();
                    var instance = r.Get(tech);
                    if (done && (instance==null || !instance.IsComplete())) unlocks.Add(tech);
                    if (done && instance == null) instance = r.GetOrAdd(tech);
                    if (instance != null) Completed.SetValue(instance, done);
                    foreach (var id in tech.costsByResearchTypeID.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        float n = done ? tech.costsByResearchTypeID[id] : reader.ReadSingle();
                        if (instance == null && n == 0) continue;
                        if (instance == null) instance = r.GetOrAdd(tech);
                        var inv = (ResearchPointInventory)Inventory.GetValue(instance);
                        if (inv != null) inv.PointsByTypeID[id] = n;
                    }
                }
                int count = reader.ReadUInt16();
                if (count > all.Length) throw new InvalidDataException("Invalid research queue size");
                // GetResearchQueue returns a defensive copy. Update the disposable scene's backing
                // list directly so opening the stock screen later sees the same order.
                var queue = (List<TechInstance>)QueuedTech.GetValue(r);
                queue.Clear();
                for (int i = 0; i < count; ++i) queue.Add(r.GetOrAdd(all[reader.ReadUInt16()]));
                int active = reader.ReadInt16();
                ActiveTech.SetValue(r, active < 0 ? null : r.GetOrAdd(all[active]));
                if (s.Position != s.Length) throw new InvalidDataException("Invalid research state");
                lastResearch = data;
                if (PlanScreen.Instance!=null)
                    foreach (var tech in unlocks) RefreshUnlock.Invoke(PlanScreen.Instance,new object[]{tech});
                var screen = CurrentResearchScreen;
                if (screen != null && screen.IsScreenActive()) RefreshResearch.Invoke(screen, new object[] { queue });
            }
        }
        [HarmonyPatch]
        static class ResearchClick
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.DeclaredMethod(typeof(ResearchEntry), "OnResearchClicked");
                yield return AccessTools.DeclaredMethod(typeof(ResearchEntry), "OnResearchCanceled");
            }
            static bool Prefix(MethodBase __originalMethod, Tech ___targetTech)
            {
                if (!Guard) return true;
                if (lastResearch != null && ___targetTech != null)
                    Send("tech/" + (__originalMethod.Name == "OnResearchClicked" ? "select/" : "cancel/") + ___targetTech.Id + "/" + QueueVersion());
                return false;
            }
        }
    }
}
