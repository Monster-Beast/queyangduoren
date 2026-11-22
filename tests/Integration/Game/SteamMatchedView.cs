using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Compare the same paused scene without the host and guest having different
    // active tools, overlays, camera zoom or management windows.
    internal sealed class SteamMatchedView
    {
        bool configured,published,complete;
        float nextStatus;
        FixtureFrame frame;
        int blueprintCell,blueprintId;
        string tint;

        internal bool Check(CSteamID lobby,CSteamID friend,int digCell,bool host,Action<string> log)
        {
            if (complete) return true;
            var cam=CameraController.Instance;
            var p=Grid.CellToPosCCC(digCell,Grid.SceneLayer.Move);
            if (!configured)
            {
                if (ManagementMenu.Instance!=null) ManagementMenu.Instance.CloseAll();
                SelectTool.Instance.Select(null,false);
                if (OverlayScreen.Instance.GetMode()!=OverlayModes.None.ID) OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID,true);
                PlayerController.Instance.ActivateTool(DigTool.Instance);
                cam.SetTargetPos(p,10f,false);
                frame=new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),
                    host?"matched-host.png":"matched-guest.png"));
                configured=true;
            }
            // Use the public native camera controls for an exact paired screenshot.
            // SetTargetPos by itself interpolates and can stop short of the requested zoom.
            cam.SetPosition(new Vector3(p.x,p.y,cam.transform.position.z));
            cam.OrthographicSize=10f;
            Vector3 vp=cam.baseCamera.WorldToViewportPoint(p);
            if (Time.realtimeSinceStartup>=nextStatus)
            {
                nextStatus=Time.realtimeSinceStartup+8;
                log("DUAL_MATCHED_PROGRESS host="+host+" viewport="+vp+" zoom="+cam.baseCamera.orthographicSize+" blueprintPublished="+published+" frameRequested="+frame.Requested);
            }
            if (Mathf.Abs(vp.x-.5f)>.0005f || Mathf.Abs(vp.y-.5f)>.0005f || Mathf.Abs(cam.baseCamera.orthographicSize-10f)>.01f) return false;
            if (PlayerController.Instance.ActiveTool!=DigTool.Instance || OverlayScreen.Instance.GetMode()!=OverlayModes.None.ID)
                throw new InvalidDataException("Matched scene requires DigTool with the normal overlay");
            if (host)
            {
                if (!published)
                {
                    var chosen=UnityEngine.Object.FindObjectsByType<Constructable>(FindObjectsSortMode.None)
                        .Where(c=>c!=null && c.GetComponent<Building>()!=null && c.GetComponent<KBatchedAnimController>()!=null)
                        .Select(c=>new {item=c,cell=c.GetComponent<Building>().GetCell(),pos=cam.baseCamera.WorldToViewportPoint(c.transform.position)})
                        .Where(c=>Grid.WorldIdx[c.cell]==Grid.WorldIdx[digCell] && c.pos.x>.1f && c.pos.x<.9f && c.pos.y>.1f && c.pos.y<.9f)
                        .OrderBy(c=>Grid.GetCellDistance(digCell,c.cell)).FirstOrDefault();
                    if (chosen==null) throw new InvalidDataException("The paired render needs a visible actual construction blueprint");
                    blueprintCell=chosen.cell; blueprintId=chosen.item.GetComponent<KPrefabID>().InstanceID;
                    tint=ColorUtility.ToHtmlStringRGBA(chosen.item.GetComponent<KBatchedAnimController>().TintColour);
                    if (!SteamMatchmaking.SetLobbyData(lobby,"qy_matched_blueprint",blueprintCell+"|"+blueprintId+"|"+tint))
                        throw new IOException("Could not publish blueprint comparison metadata");
                    published=true;
                }
                if (SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_matched_scene")!="ready") return false;
            }
            else
            {
                var raw=SteamMatchmaking.GetLobbyData(lobby,"qy_matched_blueprint");
                if (string.IsNullOrEmpty(raw)) return false;
                var parts=raw.Split('|');
                if (parts.Length!=3 || !int.TryParse(parts[0],out blueprintCell) || !int.TryParse(parts[1],out blueprintId))
                    throw new InvalidDataException("Bad blueprint comparison metadata");
                var anim=NativeWorldMirror.GetBuildingSelectable(blueprintCell,blueprintId)?.GetComponent<KBatchedAnimController>();
                if (anim==null || !anim.gameObject.activeInHierarchy) return false;
                tint=ColorUtility.ToHtmlStringRGBA(anim.TintColour);
                if (tint!=parts[2]) throw new InvalidDataException("Blueprint renderer tint mismatch: host="+parts[2]+" guest="+tint);
                SteamMatchmaking.SetLobbyMemberData(lobby,"qy_matched_scene","ready");
            }
            string pair=SteamMatchmaking.GetLobbyData(lobby,"qy_native_dig_tints");
            if (host && string.IsNullOrEmpty(pair))
            {
                var digs=UnityEngine.Object.FindObjectsByType<Diggable>(FindObjectsSortMode.None)
                    .Where(d=>d!=null && d.materialDisplay!=null && d.materialDisplay.enabled)
                    .Select(d=>new {dig=d,cell=Grid.PosToCell(d),vp=cam.baseCamera.WorldToViewportPoint(d.transform.position)})
                    .Where(d=>Grid.IsValidCell(d.cell) && Grid.Visible[d.cell]>0 && d.vp.x>.05f && d.vp.x<.95f && d.vp.y>.05f && d.vp.y<.95f)
                    .OrderBy(d=>Grid.GetCellDistance(digCell,d.cell)).ToList();
                var yes=digs.FirstOrDefault(d=>d.dig.Reachable);
                if (yes==null) return false;
                string yesAsset=DigMarkerGeometry.Asset(yes.dig.materialDisplay);
                var no=digs.FirstOrDefault(d=>!d.dig.Reachable && DigMarkerGeometry.Asset(d.dig.materialDisplay).Split('=').Last()!=yesAsset.Split('=').Last());
                if (no==null) return false;
                pair=yes.cell+","+yesAsset.Split('=').Last()+","+no.cell+","+DigMarkerGeometry.Asset(no.dig.materialDisplay).Split('=').Last();
                if (!SteamMatchmaking.SetLobbyData(lobby,"qy_native_dig_tints",pair)) throw new IOException("Could not publish native order colors");
            }
            if (string.IsNullOrEmpty(pair)) return false;
            var tints=pair.Split(',');
            if (tints.Length!=4) throw new InvalidDataException("Bad native order colors");
            if (!host)
                for (int i=0;i<=2;i+=2)
                {
                    var renderer=NativeWorldMirror.GetDigRenderer(int.Parse(tints[i],CultureInfo.InvariantCulture));
                    if (renderer==null || !renderer.enabled || DigMarkerGeometry.Asset(renderer).Split('=').Last()!=tints[i+1]) return false;
                }
            if (!frame.Requested)
            {
                log("DUAL_DIG_TINTS reachableCell="+tints[0]+" rgba="+tints[1]+" unreachableCell="+tints[2]+" rgba="+tints[3]);
                frame.Capture(); return false;
            }
            if (!frame.Complete) return false;
            log(string.Format(CultureInfo.InvariantCulture,"DUAL_MATCHED_RENDER dig={0} blueprint={1} tint={2} viewport={3:F6},{4:F6} zoom={5:F3} tool=DigTool overlay=None",
                digCell,blueprintCell,tint,vp.x,vp.y,cam.baseCamera.orthographicSize));
            complete=true;
            return true;
        }
    }
}
