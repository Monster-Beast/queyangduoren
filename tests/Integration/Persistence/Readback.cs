using System;
using System.IO;
using System.Collections;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Integration.Persistence
{
    // Installed alone in a fresh, temporary dev profile. This assembly has no reference to either co-op DLL.
    public sealed class ReadbackMod : KMod.UserMod2 { }

    [HarmonyPatch(typeof(MainMenu), "OnSpawn")]
    internal static class Bootstrap
    {
        private static ReadbackDriver driver;
        private static void Postfix()
        {
            string root = Environment.GetEnvironmentVariable("QY_SMOKE_ROOT");
            if (string.IsNullOrEmpty(root) || root != Util.GetKleiRootPath() ||
                Environment.GetEnvironmentVariable("QY_SMOKE_ROLE") != "readback")
                throw new InvalidOperationException("Readback requires its isolated profile.");
            if (driver != null) return;
            var go = new GameObject("Native save QA");
            UnityEngine.Object.DontDestroyOnLoad(go);
            driver = go.AddComponent<ReadbackDriver>();
        }
    }

    public sealed class ReadbackDriver : MonoBehaviour
    {
        private bool loading, done;
        private bool withCoop;
        private string report, expectedMarker, save;
        private void Awake()
        {
            Application.runInBackground = true;
            withCoop=Environment.GetEnvironmentVariable("QY_READBACK_COOP")=="1";
            report = Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
            save = Environment.GetEnvironmentVariable("QY_SMOKE_SAVE");
            expectedMarker = File.ReadAllText(Path.Combine(Path.GetDirectoryName(save), "marker.txt")).Trim();
        }
        private void Update()
        {
            if (done) return;
            try
            {
                var assemblies=AppDomain.CurrentDomain.GetAssemblies();
                if (assemblies.Any(a=>a.GetName().Name=="QueyangCoop")!=withCoop ||
                    assemblies.Any(a=>a.GetName().Name=="Queyang.Core")!=withCoop)
                    throw new Exception("Unexpected co-op assembly state for readback");
                if (!loading)
                {
                    loading = true;
                    KPlayerPrefs.SetString("SavesDefaultToCloud", "Disabled");
                    SaveLoader.SetActiveSaveFilePath(save);
                    File.AppendAllText(report, "REOPEN native load coopInstalled="+withCoop+"\n");
                    LoadingOverlay.Load(SaveLoader.LoadScene);
                    return;
                }
                if (Game.Instance == null || ClusterManager.Instance?.activeWorld == null ||
                    SaveLoader.Instance == null || !SaveLoader.Instance.loadedFromSave ||
                    Components.LiveMinionIdentities.Count == 0 || ScheduleManager.Instance == null) return;
                if (!ScheduleManager.Instance.GetSchedules().Any(s => s.name == expectedMarker))
                    throw new Exception("Host-generated marker missing after re-open.");
                string manifest=Path.Combine(Path.GetDirectoryName(save),"owners.txt");
                if (File.Exists(manifest)) VerifyOrders(manifest);
                done = true;
                string result=withCoop ? "PASS native host save re-opened; co-op ownership verified; schedule marker verified" :
                    "PASS native host save re-opened; production co-op assemblies absent; schedule marker verified";
                File.AppendAllText(report,result+"\n");
            }
            catch (Exception e) { done = true; File.AppendAllText(report, "FAIL " + e + "\n"); }
        }
        void VerifyOrders(string manifest)
        {
            var cells=File.ReadAllLines(manifest).Select(int.Parse).ToArray();
            if (cells.Length!=4 || cells.Distinct().Count()!=4 || !cells.All(Grid.IsValidCell))
                throw new InvalidDataException("Invalid order coordinate manifest");
            var host=Diggable.GetDiggable(cells[0]);
            var guest=Diggable.GetDiggable(cells[1]);
            var legacy=Diggable.GetDiggable(cells[3]);
            if (host==null || guest==null || legacy==null || Grid.Solid[cells[2]] || Diggable.GetDiggable(cells[2])!=null)
                throw new InvalidDataException("A native pending, completed or legacy dig differed after loading");
            if (withCoop)
            {
                var book=SaveGame.Instance.GetComponents<KMonoBehaviour>().Single(c=>c!=null && c.GetType().FullName=="Queyang.Game.ColonyOrderBook");
                var orders=(IDictionary)book.GetType().GetField("Orders",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(book);
                long Key(Diggable d) => (long)d.GetComponent<KPrefabID>().InstanceID<<3; // OrderKind.Dig is zero.
                if (!Equals(orders[Key(host)],1) || !Equals(orders[Key(guest)],2) || orders.Contains(Key(legacy)))
                    throw new InvalidDataException("Persisted order author differs after native save readback");
            }
            File.AppendAllText(report,"DUAL_SAVED_ORDERS hostDig=True guestDig=True originalDig=True completedAbsent=True ownersVerified="+withCoop+"\n");
        }
    }
}
