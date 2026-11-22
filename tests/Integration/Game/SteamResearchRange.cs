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
        float watchUntil,nextPeripheral;
        int otherTiles;
        bool lostBar;
        static SteamResearchRange watching;
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
        [HarmonyPatch(typeof(NativeWorldMirror),nameof(NativeWorldMirror.Apply))]
        static class CheckBetweenSnapshots
        {
            static void Postfix(ColonyView __0)
            {
                var monitor=watching;
                if(monitor==null) return;
                Grid.CellToXY(monitor.cell,out int x,out int y);
                if((uint)(x-__0.X)>=ColonyView.Width || (uint)(y-__0.Y)>=ColonyView.Height) monitor.otherTiles++;
                if(NativeBuildingWork.GetDisplayed(monitor.stableId)?.gameObject.activeInHierarchy!=true)
                    monitor.lostBar=true;
            }
        }
        static string VisualOrigin(KSelectable target)
        {
            var anim=target.GetComponent<KBatchedAnimController>();
            var m=NativeDupePose.RenderMatrix(anim);
            return FormattableString.Invariant($"{m.m02:R}|{m.m12:R}");
        }

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
                Set(room,"art",VisualOrigin(station.GetComponent<KSelectable>()));
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
            string currentArt=VisualOrigin(target),hostArt=Read(room,"art");
            if(string.IsNullOrEmpty(hostArt)) return false;
            var observedPosition=currentArt.Split('|').Select(value=>float.Parse(value,culture)).ToArray();
            var expectedPosition=hostArt.Split('|').Select(value=>float.Parse(value,culture)).ToArray();
            float artError=Vector2.Distance(new Vector2(observedPosition[0],observedPosition[1]),
                new Vector2(expectedPosition[0],expectedPosition[1]));
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
            if(!text || artError>.005f || visible!=(stage<2) || visible && Math.Abs(bar.PercentFull-fraction)>.01f)
            {
                if(Time.realtimeSinceStartup>=nextLog)
                {
                    log($"RESEARCH_RANGE_WAIT stage={stage} id={stableId} text={text} visible={visible} expected={fraction} shown={bar?.PercentFull} overlay={SimDebugView.Instance?.GetMode()} art={currentArt}/{hostArt} error={artError}");
                    nextLog=Time.realtimeSinceStartup+5;
                }
                return false;
            }
            if(!host && stage==1 && otherTiles<3)
            {
                watching=this;
                if(watchUntil==0) watchUntil=Time.realtimeSinceStartup+1.5f;
                if(Time.realtimeSinceStartup>=nextPeripheral)
                {
                    Grid.CellToXY(cell,out int x,out int y);
                    int far=x+48<Grid.WidthInCells?x+48:Math.Max(0,x-48);
                    CoopSession.RequestPeripheral(far,Math.Max(0,y-8));
                    nextPeripheral=Time.realtimeSinceStartup+.16f;
                }
                return false;
            }
            if(!host && stage==1 && watching==this)
            {
                if(lostBar) throw new InvalidDataException("Research bar disappeared while applying a viewport tile");
                if(Time.realtimeSinceStartup<watchUntil) return false;
                watching=null;
                log("RESEARCH_RANGE_CONTINUOUS unrelatedTiles="+otherTiles+" lostBar=False");
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
            log(FormattableString.Invariant($"RESEARCH_RANGE stage={stage} cell={cell} id={stableId} points={pointsText} barVisible={visible} fraction={bar?.PercentFull} art={currentArt} artError={artError:R}"));
            stage++;photo=null;
            if(stage!=3) return false;
            SelectTool.Instance.Select(null,false);
            camera.SetTargetPos(previousCamera,previousZoom,false);camera.SetPosition(previousCamera);camera.OrthographicSize=previousZoom;
            return true;
        }
    }
}
