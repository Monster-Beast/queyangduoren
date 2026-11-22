using System;
using System.IO;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Exercise ONI's real lighting inputs and the actual rendered light buffer.
    // A source created after the join cannot accidentally pass from the guest save.
    internal sealed class SteamDualLighting
    {
        int cell=-1,step,changed=-1;
        float stable,timeout,readAt,reportAt;
        Light2D lamp;
        FixtureFrame shot;
        Color32 dark;
        bool verified;
        static string Key(int step,string field)=>"qy_light_"+step+"_"+field;
        static void Set(CSteamID room,string key,string value)
        { if(!SteamMatchmaking.SetLobbyData(room,key,value)) throw new IOException("Lighting metadata unavailable"); }
        static int Level(Color32 c)=>c.r+c.g+c.b;
        static int PickCell()
        {
            var dupe=Components.LiveMinionIdentities.Items[0];
            var origin=Grid.CellToXY(Grid.PosToCell(dupe));
            for(int r=2;r<28;r++) for(int dy=-r;dy<=r;dy++) for(int dx=-r;dx<=r;dx++)
            {
                if(Math.Abs(dx)!=r && Math.Abs(dy)!=r) continue;
                int x=origin.x+dx,y=origin.y+dy;
                if(x<2 || y<2 || x>=Grid.WidthInCells-2 || y>=Grid.HeightInCells-2) continue;
                int c=Grid.XYToCell(x,y);
                if(Grid.WorldIdx[c]==ClusterManager.Instance.activeWorldId && Grid.Visible[c]==255 &&
                    Grid.Element[c].id==SimHashes.Oxygen && Grid.LightCount[c]==0 && Grid.LightIntensity[c]==0 &&
                    Grid.Objects[c,(int)ObjectLayer.Building]==null) return c;
            }
            throw new InvalidDataException("No unlit oxygen cell near the test colony");
        }
        void SwitchOn(Vector3 pos)
        {
            var obj=new GameObject("Fixture light");
            obj.SetActive(false); obj.transform.position=pos;
            obj.AddComponent<KPrefabID>().PrefabTag=new Tag("FixtureLight");
            lamp=obj.AddComponent<Light2D>();
            lamp.autoRespondToOperational=false; lamp.Color=new Color(1f,.85f,.6f);
            lamp.Offset=Vector2.zero; lamp.Range=5f; lamp.Lux=2400;lamp.shape=LightShape.Circle;
            obj.SetActive(true);
        }
        internal bool Check(CSteamID room,CSteamID peer,bool host,int round,Action<string> log)
        {
            if(Environment.GetEnvironmentVariable("QY_CHECK_LIGHTING")!="1" || round!=1 || step>=3) return true;
            if(timeout==0) timeout=Time.realtimeSinceStartup+90;
            if(Time.realtimeSinceStartup>timeout) throw new TimeoutException("Lighting stage "+step+" cell="+cell);
            if(cell<0)
            {
                if(host)
                {
                    if(!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                        throw new InvalidOperationException("Lighting fixture requires isolated save");
                    cell=PickCell(); Set(room,"qy_light_cell",cell.ToString());
                }
                else if(!int.TryParse(SteamMatchmaking.GetLobbyData(room,"qy_light_cell"),out int value)) return false;
                else cell=value;
            }
            Grid.CellToXY(cell,out int x,out int y);
            var pos=Grid.CellToPosCCC(cell,Grid.SceneLayer.Move);
            var camera=CameraController.Instance;
            var point=new Vector3(pos.x,pos.y,camera.transform.position.z);
            camera.SetTargetPos(point,6f,false);camera.SetPosition(point);camera.OrthographicSize=6f;
            if(host && changed!=step)
            {
                if(step==1) SwitchOn(pos);
                if(step==2) lamp.gameObject.SetActive(false);
                changed=step;
            }
            var buffer=LightBuffer.Instance;
            if(buffer==null || buffer.Texture==null || Time.realtimeSinceStartup<readAt) return false;
            readAt=Time.realtimeSinceStartup+.1f;
            var mask=buffer.Material.GetTexture("_PropertyWorldLight");
            if(mask==null)
            {
                if(Time.realtimeSinceStartup>=reportAt) {log("DUAL_LIGHT_WAIT missingMaterialMask step="+step);reportAt=Time.realtimeSinceStartup+4;}
                return false;
            }
            var vp=camera.baseCamera.WorldToViewportPoint(pos);
            if(Math.Abs(vp.x-.5f)>.02f || Math.Abs(vp.y-.5f)>.02f) return false;
            var colour=SteamSceneLight.PixelAt(mask,new Vector2((x+.5f)/Grid.WidthInCells,(y+.5f)/Grid.HeightInCells));
            var light=SteamSceneLight.PixelAt(buffer.Texture,new Vector2(vp.x,vp.y));
            bool lit=step==1;
            bool ok=(Grid.LightIntensity[cell]>0)==lit && (Level(colour)>0)==lit;
            if(lit) ok &= Level(light)>Level(dark)+12;
            if(step==2) ok &= Math.Abs(Level(light)-Level(dark))<=12;
            if(Time.realtimeSinceStartup>=reportAt)
            {
                log("DUAL_LIGHT_SAMPLE step="+step+" cell="+cell+" lux="+Grid.LightIntensity[cell]+
                    " mask="+ColorUtility.ToHtmlStringRGBA(colour)+" buffer="+ColorUtility.ToHtmlStringRGBA(light)+
                    " host="+SteamMatchmaking.GetLobbyData(room,Key(step,"render")));
                reportAt=Time.realtimeSinceStartup+4;
            }
            if(!ok) {stable=0;return false;}
            if(stable==0) {stable=Time.realtimeSinceStartup;return false;}
            if(Time.realtimeSinceStartup-stable<.45f) return false;
            if(step==0) dark=light;
            string signature=ColorUtility.ToHtmlStringRGBA(colour)+"-"+ColorUtility.ToHtmlStringRGBA(light);
            if(host && !verified) {Set(room,Key(step,"render"),signature);verified=true;}
            if(!host && SteamMatchmaking.GetLobbyData(room,Key(step,"render"))!=signature) return false;
            if(shot==null)
            {
                string dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                shot=new FixtureFrame(Path.Combine(dir,"light-"+(host?"host":"guest")+"-"+step+".png")); shot.Capture();return false;
            }
            if(!shot.Complete) return false;
            if(host && SteamMatchmaking.GetLobbyMemberData(room,peer,Key(step,"seen"))!="1") return false;
            if(!host) SteamMatchmaking.SetLobbyMemberData(room,Key(step,"seen"),"1");
            log("DUAL_LIGHT_RENDER step="+step+" cell="+cell+" lux="+Grid.LightIntensity[cell]+" maskAndLightBuffer="+signature);
            step++; shot=null;stable=0;verified=false;
            if(step==3 && host) UnityEngine.Object.Destroy(lamp.gameObject);
            return step==3;
        }
    }
}
