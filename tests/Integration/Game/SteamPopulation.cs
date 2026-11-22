using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Compare actual HUD text and live body renderers. The baseline count and the visual proxy
    // count alone both miss one half of this regression.
    internal sealed class SteamPopulation
    {
        string last;
        bool startedDeath,wasPaused;
        float nextCheck,nextDiagnostic;
        FixtureFrame screenshot;
        static readonly AccessTools.FieldRef<MeterScreen,LocText> counter=AccessTools.FieldRefAccess<MeterScreen,LocText>("currentMinions");
        static string Ids() => string.Join(",",Components.LiveMinionIdentities.Items.Where(m=>m!=null &&
            ClusterUtil.GetMyWorldId(m)==ClusterManager.Instance.activeWorldId).Select(m=>m.GetComponent<KPrefabID>().InstanceID).OrderBy(id=>id));

        internal bool Check(CSteamID lobby,bool host,string stage,int born,Action<string> log)
        {
            if (last==stage) return true;
            if (screenshot!=null)
            {
                if (!screenshot.Complete) return false;
                screenshot=null;
                last=stage;
                return true;
            }
            if (Time.unscaledTime<nextCheck) return false;
            nextCheck=Time.unscaledTime+.25f;
            string key="qy_census_"+stage;
            string expected=SteamMatchmaking.GetLobbyData(lobby,key);
            if (host && string.IsNullOrEmpty(expected))
            {
                if (stage=="death" && Components.LiveMinionIdentities.Items.Any(m=>m!=null && m.GetComponent<KPrefabID>().InstanceID==born)) return false;
                if (!SteamMatchmaking.SetLobbyData(lobby,key,Ids())) throw new IOException("Cannot publish expected roster");
                return false;
            }
            if (string.IsNullOrEmpty(expected) || MeterScreen.Instance==null) return false;
            var ids=expected.Split(',').Select(int.Parse).OrderBy(i=>i).ToArray();
            if ((stage!="death")!=ids.Contains(born)) throw new InvalidDataException("Printed ID inconsistent with authoritative roster");
            string label=counter(MeterScreen.Instance).text;
            var bodies=UnityEngine.Object.FindObjectsByType<KBatchedAnimController>(FindObjectsSortMode.None)
                .Where(a=>a.IsVisible() && a.GetComponent<KSelectable>()?.IsSelectable==true)
                .Select(a=>new {proxy=a.GetComponent<NativeDupeIdentityMarker>(),native=a.GetComponent<MinionIdentity>()})
                .Where(v=>v.proxy!=null || v.native!=null && Components.LiveMinionIdentities.Items.Contains(v.native))
                .Select(v=>v.proxy!=null ? v.proxy.HostId : v.native.GetComponent<KPrefabID>().InstanceID)
                .OrderBy(i=>i).ToArray();
            if (label!=ids.Length.ToString() || !ids.SequenceEqual(bodies))
            {
                if (Time.unscaledTime>=nextDiagnostic)
                {
                    nextDiagnostic=Time.unscaledTime+5f;
                    log("DUAL_POPULATION_WAIT stage="+stage+" hud="+label+" expected="+expected+" bodies="+string.Join(",",bodies));
                }
                return false;
            }
            log("DUAL_POPULATION stage="+stage+" role="+(host?"host":"guest")+" hud="+label+" liveIds="+expected+" visibleBodies="+string.Join(",",bodies));
            string name="dual-"+(host?"host":"guest")+"-population-"+stage+".png";
            screenshot=new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),name));
            screenshot.Capture();
            return false;
        }

        internal bool Death(CSteamID lobby,bool host,GameObject subject,int born,Action<string> log)
        {
            if (host && !startedDeath)
            {
                if (subject==null || subject.GetComponent<KPrefabID>().InstanceID!=born || !CoopSession.IsHosting ||
                    Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Census death fixture requires its own printed person and isolated host");
                wasPaused=SpeedControlScreen.Instance.IsPaused;
                subject.GetSMI<DeathMonitor.Instance>().Kill(Db.Get().Deaths.Slain);
                if (wasPaused) SpeedControlScreen.Instance.Unpause(false);
                startedDeath=true;
            }
            if (!Check(lobby,host,"death",born,log)) return false;
            if (host && wasPaused) SpeedControlScreen.Instance.Pause(false);
            return true;
        }
    }
}
