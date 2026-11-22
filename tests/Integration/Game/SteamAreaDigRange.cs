using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamAreaDigRange
    {
        const int Width=3,Height=2;
        int start=-1,stage,tracked=-1,changes;
        int[] cells;
        float limit,last,min=1,max,nextLog;
        bool issued;
        FixtureFrame frame;
        Vector3 oldCamera;
        float oldZoom;
        static bool Enabled=>Environment.GetEnvironmentVariable("QY_AREA_DIG_RANGE")=="1";
        internal bool Busy=>Enabled && stage<3;

        static int[] Rectangle(int origin)=>Enumerable.Range(0,Width*Height)
            .Select(i=>origin+i%Width+i/Width*Grid.WidthInCells).ToArray();
        static int Find(int reserved)
        {
            int world=ClusterManager.Instance.activeWorldId;
            var workers=Components.LiveMinionIdentities.Items.Where(p=>p!=null && ClusterUtil.GetMyWorldId(p)==world).ToArray();
            var nav=workers.Select(p=>p.GetComponent<Navigator>()).Where(n=>n!=null).ToArray();
            var placement=Assets.GetPrefab(DigPlacerConfig.ID).GetComponent<Diggable>();
            Grid.CellToXY(reserved,out int ox,out int oy);
            for(int radius=2;radius<=16;radius++)
            for(int dy=-radius;dy<=radius;dy++)
            for(int dx=-radius;dx<=radius;dx++)
            {
                if(Math.Abs(dx)!=radius && Math.Abs(dy)!=radius) continue;
                int x=ox+dx,y=oy+dy;
                if(x<2 || y<2 || x+Width>=Grid.WidthInCells-1 || y+Height>=Grid.HeightInCells-1) continue;
                int cell=Grid.XYToCell(x,y);
                var row=Rectangle(cell);
                if(row.Any(c=>c==reserved || Grid.WorldIdx[c]!=world || Grid.Visible[c]==0 || !Grid.Solid[c] ||
                    !Diggable.IsDiggable(c) || Diggable.Undiggable(Grid.Element[c]) || Grid.Element[c].hardness>=50 ||
                    Diggable.GetDiggable(c)!=null || HostWorld.FindBuilding(c)!=null)) continue;
                if(row.Any(c=>nav.Any(n=>n.CanReach(c,placement.GetOffsets(c))))) return cell;
            }
            throw new InvalidDataException("No reachable visible 3x2 native excavation rectangle");
        }

        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,int reserved,Action<string> log)
        {
            if(!Busy || round!=1) return true;
            var camera=CameraController.Instance;
            if(limit==0)
            {
                limit=Time.realtimeSinceStartup+135;
                oldCamera=camera.transform.position;oldZoom=camera.OrthographicSize;
            }
            if(Time.realtimeSinceStartup>limit) throw new TimeoutException($"Area excavation stage={stage} start={start} samples={changes}");
            if(start<0)
            {
                if(host)
                {
                    if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                        throw new InvalidOperationException("Area excavation requires an isolated host");
                    start=Find(reserved);
                    SteamMatchmaking.SetLobbyData(room,"qy_area_start",start.ToString());
                }
                else if(!int.TryParse(SteamMatchmaking.GetLobbyData(room,"qy_area_start"),out start))
                {start=-1;return false;}
                cells=Rectangle(start);
            }
            var point=Grid.CellToPosCCC(start,Grid.SceneLayer.Move);
            var focus=new Vector3(point.x+1,point.y+.5f,camera.transform.position.z);
            camera.SetTargetPos(focus,7,false);camera.SetPosition(focus);camera.OrthographicSize=7;
            if(stage==0 && !host && !issued)
            {
                if(cells.Any(c=>!Grid.Solid[c] || !Grid.IsVisible(c))) return false;
                PlayerController.Instance.ActivateTool(DigTool.Instance);
                DigTool.Instance.OnLeftClickDown(point);
                DigTool.Instance.OnLeftClickUp(Grid.CellToPosCCC(cells.Last(),Grid.SceneLayer.Move));
                issued=true;
                log($"AREA_DIG_DRAG oneGuest=True width={Width} height={Height} origin={start}");
            }
            if(stage==0)
            {
                if(!cells.All(c=>(host ? Diggable.GetDiggable(c)!=null : NativeWorldMirror.IsDigVisible(c)) &&
                    OrderOwnership.At(c).Count(mark=>mark.Kind==OrderKind.Dig && mark.Author==2)==1 &&
                    OrderOwnership.At(c).Count(mark=>mark.Kind==OrderKind.Dig)==1)) return false;
                if(!Picture(host,"orders")) return false;
                if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,"qy_area_ready")!="1") return false;
                if(!host) SteamMatchmaking.SetLobbyMemberData(room,"qy_area_ready","1");
                else foreach(int c in cells)
                    Diggable.GetDiggable(c).GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority,9));
                log($"AREA_DIG_ORDERS role={(host?"host":"guest")} count={cells.Length} author=2 cells={string.Join(",",cells)}");
                stage++;frame=null;
            }
            if(stage==1)
            {
                if(host && changes<2 && SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
                if(tracked<0)
                {
                    if(host)
                    {
                        tracked=Array.Find(cells,c=>Reading(c,true) is ProgressBar bar && bar.PercentFull>.015f && bar.PercentFull<.75f);
                        if(tracked==0) {tracked=-1;return false;}
                        SteamMatchmaking.SetLobbyData(room,"qy_area_work",tracked.ToString());
                    }
                    else if(!int.TryParse(SteamMatchmaking.GetLobbyData(room,"qy_area_work"),out tracked))
                    {tracked=-1;return false;}
                }
                var working=Reading(tracked,host);
                if(working!=null && working.gameObject.activeInHierarchy && working.bar.isActiveAndEnabled)
                {
                    float amount=working.PercentFull;
                    if(amount>0 && amount<1 && (changes==0 || amount-last>.015f))
                    { changes++;last=amount;min=Math.Min(min,amount);max=Math.Max(max,amount); }
                }
                if(Time.realtimeSinceStartup>=nextLog && changes<2)
                {
                    nextLog=Time.realtimeSinceStartup+4;
                    log($"AREA_DIG_WORK_WAIT cell={tracked} changes={changes} bar={working?.PercentFull}");
                }
                if(changes<2 || !Picture(host,"work")) return false;
                log(FormattableString.Invariant($"AREA_DIG_BAR cell={tracked} changes={changes} min={min:R} max={max:R}"));
                stage++;frame=null;
            }
            if(Grid.Solid[tracked] || Reading(tracked,host)!=null) return false;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,"qy_area_finished")!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,"qy_area_finished","1");
            else SpeedControlScreen.Instance.Pause(false);
            log($"AREA_DIG_BAR_CLEARED cell={tracked} excavated={cells.Count(c=>!Grid.Solid[c])}/{cells.Length}");
            stage++;
            camera.SetTargetPos(oldCamera,oldZoom,false);camera.SetPosition(oldCamera);camera.OrthographicSize=oldZoom;
            return true;
        }

        static ProgressBar Reading(int cell,bool host)
        {
            if(!host) return NativeBuildingWork.GetDigDisplayed(cell);
            var dig=Diggable.GetDiggable(cell);
            return dig==null?null:NativeConstructionProgress.Source(dig);
        }
        bool Picture(bool host,string step)
        {
            if(frame!=null) return frame.Complete;
            var dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
            frame=new FixtureFrame(Path.Combine(dir,$"area-{(host?"host":"guest")}-{step}.png"));
            frame.Capture();
            return false;
        }
    }
}
