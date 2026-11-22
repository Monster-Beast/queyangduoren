using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Drive the real research inventory in a disposable host colony to hold
    // precisely reproducible points and visible/hidden native work bars.
    internal sealed class SteamResearchRange
    {
        readonly static CultureInfo culture=CultureInfo.InvariantCulture;
        readonly static AccessTools.FieldRef<Workable,ProgressBar> source=
            AccessTools.FieldRefAccess<Workable,ProgressBar>("progressBar");
        int stage,prepared=-1,stableId,cell=-1;
        float deadline,nextLog;
        ResearchCenter station;
        Tech tech;
        float required;
        FixtureFrame photo;
        Vector3 previousCamera;
        float previousZoom;
        string Key(string field)=>"qy_research_range_"+stage+"_"+field;
        void Set(CSteamID room,string field,string value)
        { if(!SteamMatchmaking.SetLobbyData(room,Key(field),value)) throw new IOException("Could not publish research reading"); }
        string Read(CSteamID room,string field)=>SteamMatchmaking.GetLobbyData(room,Key(field));

        void SetUp()
        {
            if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                throw new InvalidOperationException("Research range requires an isolated host");
            int world=ClusterManager.Instance.activeWorldId;
            station=NativeFacilityChecks.Build(ResearchCenterConfig.ID,world).GetComponent<ResearchCenter>();
            cell=station.GetComponent<Building>().GetCell();
            stableId=station.GetComponent<KPrefabID>().InstanceID;
            string type=station.GetResearchType();
            tech=Db.Get().Techs.resources.First(t=>!t.Disabled && t.FoundNode && !t.IsComplete() && t.ArePrerequisitesComplete() &&
                t.costsByResearchTypeID.TryGetValue(type,out float cost) && cost>=4 && t.costsByResearchTypeID.Values.Count(n=>n>0)==1);
            required=tech.costsByResearchTypeID[type];
        }

        void Change()
        {
            var research=Research.Instance;
            string type=station.GetResearchType();
            if(stage==0) research.SetActiveResearch(tech,true);
            if(stage<2)
            {
                var active=research.GetActiveResearch();
                if(active?.tech!=tech) throw new InvalidDataException("Research fixture did not select its native technology");
                active.progressInventory.PointsByTypeID.TryGetValue(type,out float points);
                float desired=Mathf.Floor(required*(stage==0?.25f:.65f));
                research.AddResearchPoints(type,desired-points);
                station.ShowProgressBar(true);
            }
            else station.ShowProgressBar(false);
        }

        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,Action<string> log)
        {
            if(Environment.GetEnvironmentVariable("QY_RESEARCH_RANGE")!="1" || round!=1 || stage==3) return true;
            var camera=CameraController.Instance;
            if(deadline==0)
            {
                deadline=Time.realtimeSinceStartup+95;
                previousCamera=camera.transform.position; previousZoom=camera.OrthographicSize;
            }
            if(Time.realtimeSinceStartup>deadline) throw new TimeoutException($"Research range stage {stage}, cell {cell}");
            if(host && station==null) SetUp();
            if(host && prepared!=stage)
            {
                if(!station.GetComponent<KPrefabID>().isSpawned) return false;
                Change();
                int points=Mathf.RoundToInt(station.GetPercentComplete()*required);
                Set(room,"station",$"{stableId}|{cell}|{points}|{required.ToString(culture)}");
                prepared=stage;
            }
            var record=Read(room,"station").Split('|');
            if(record.Length!=4) return false;
            stableId=int.Parse(record[0],culture);cell=int.Parse(record[1],culture);
            float fraction=int.Parse(record[2],culture)/float.Parse(record[3],culture);
            var target=host?station.GetComponent<KSelectable>():NativeWorldMirror.GetBuildingSelectable(cell,stableId);
            var center=Grid.CellToPosCCC(cell,Grid.SceneLayer.Building);
            center.y+=1;center.z=camera.transform.position.z;
            camera.SetTargetPos(center,6f,false);camera.SetPosition(center);camera.OrthographicSize=6f;
            if(target==null) return false;
            if(DetailsScreen.Instance?.target!=target.gameObject)
            {
                SelectTool.Instance.Select(target,false);
                return false;
            }
            var bar=host?source(station):NativeBuildingWork.GetDisplayed(stableId);
            bool visible=bar!=null && bar.gameObject.activeInHierarchy && bar.bar.isActiveAndEnabled;
            string pointsText=record[2]+"/"+record[3];
            bool text=host?NativeManagement.ResearchBuildingReading(station).Contains(pointsText):
                UnityEngine.Object.FindObjectsByType<SimpleInfoScreen>(FindObjectsSortMode.None)
                    .Where(info=>info.isActiveAndEnabled).SelectMany(info=>info.GetComponentsInChildren<LocText>(false))
                    .Any(label=>label.text.Contains(pointsText));
            if(!text || visible!=(stage<2) || visible && Math.Abs(bar.PercentFull-fraction)>.01f)
            {
                if(Time.realtimeSinceStartup>=nextLog)
                {
                    log($"RESEARCH_RANGE_WAIT stage={stage} id={stableId} text={text} visible={visible} expected={fraction} shown={bar?.PercentFull}");
                    nextLog=Time.realtimeSinceStartup+5;
                }
                return false;
            }
            if(photo==null)
            {
                var dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                photo=new FixtureFrame(Path.Combine(dir,$"research-{(host?"host":"guest")}-{stage}.png"));
                photo.Capture();return false;
            }
            if(!photo.Complete) return false;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,Key("ok"))!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,Key("ok"),"1");
            log(FormattableString.Invariant($"RESEARCH_RANGE stage={stage} cell={cell} id={stableId} points={pointsText} barVisible={visible} fraction={bar?.PercentFull}"));
            stage++;photo=null;
            if(stage!=3) return false;
            SelectTool.Instance.Select(null,false);
            camera.SetTargetPos(previousCamera,previousZoom,false);camera.SetPosition(previousCamera);camera.OrthographicSize=previousZoom;
            return true;
        }
    }
}
