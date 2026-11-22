using System;
using System.Globalization;
using System.IO;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Compare real rendered geometry across processes. Each client has an independent camera.
    internal static class DigMarkerGeometry
    {
        static string Key(int round) => "qy_dual_dig_mesh_"+round;
        internal static string Asset(MeshRenderer r)
        {
            var mesh=r.GetComponent<MeshFilter>()?.sharedMesh;
            var mat=r.sharedMaterial;
            if (mesh==null || mat==null) throw new InvalidDataException("Dig icon has no mesh or material");
            var texture=mat.HasProperty("_MainTex") ? mat.mainTexture : null;
            var block=new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            int id=Shader.PropertyToID("_Color");
            Color color=block.HasColor(id) ? block.GetColor(id) : mat.color;
            return mesh.name+"|"+mesh.vertexCount+"|"+mat.shader.name+"|"+(texture==null ? "" : texture.name)
                +"|rgba="+ColorUtility.ToHtmlStringRGBA(color);
        }
        static float[] Shape(MeshRenderer r)
        {
            var pivot=r.transform.position; var bounds=r.bounds;
            var center=bounds.center; var size=bounds.size;
            return new[]{pivot.x,pivot.y,pivot.z,center.x,center.y,center.z,size.x,size.y,size.z};
        }
        static string Position(MeshRenderer r)
        {
            var p=r.bounds.center;
            var v=CameraController.Instance.baseCamera.WorldToViewportPoint(p);
            if (v.z<=0 || v.x<=0 || v.x>=1 || v.y<=0 || v.y>=1)
                throw new InvalidDataException("Dig marker outside screenshot viewport: "+v);
            return string.Format(CultureInfo.InvariantCulture,"worldCenter=({0:F4},{1:F4},{2:F4}) viewport=({3:F4},{4:F4})",
                p.x,p.y,p.z,v.x,v.y);
        }
        internal static bool Publish(CSteamID lobby,int round,int cell,Action<string> log)
        {
            var dig=Diggable.GetDiggable(cell);
            var renderer=dig==null ? null : dig.materialDisplay;
            if (renderer==null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return false;
            var easing=renderer.GetComponent<EasingAnimations>();
            if (easing!=null && easing.IsPlaying) return false;
            var asset=Asset(renderer); var shape=Shape(renderer);
            using(var buffer=new MemoryStream())
            using(var w=new BinaryWriter(buffer))
            {
                w.Write(cell); w.Write(asset); foreach (float f in shape) w.Write(f);
                if (!SteamMatchmaking.SetLobbyData(lobby,Key(round),Convert.ToBase64String(buffer.ToArray())))
                    throw new IOException("Steam rejected the native dig geometry");
            }
            log("DUAL_HOST_DIG_GEOMETRY round="+round+" cell="+cell+" asset="+asset+" "+Position(renderer));
            return true;
        }
        internal static bool Verify(CSteamID lobby,int round,int cell,Action<string> log)
        {
            var packed=SteamMatchmaking.GetLobbyData(lobby,Key(round));
            if (string.IsNullOrEmpty(packed)) return false;
            if (packed.Length>4096) throw new InvalidDataException("Oversized dig geometry");
            var r=NativeWorldMirror.GetDigRenderer(cell);
            if (r==null || !NativeWorldMirror.IsDigVisible(cell)) return false;
            if (Diggable.GetDiggable(cell)!=null) throw new InvalidDataException("Fresh guest order unexpectedly created an ONI Diggable");
            float maxError=0;
            var asset=Asset(r); var shape=Shape(r);
            using(var input=new BinaryReader(new MemoryStream(Convert.FromBase64String(packed))))
            {
                if(input.ReadInt32()!=cell) throw new InvalidDataException("Measured a different dig cell");
                string hostAsset=input.ReadString();
                if (hostAsset!=asset) return false; // Wait for the authoritative color update.
                foreach(float actual in shape)
                {
                    float host=input.ReadSingle();
                    if (float.IsNaN(host) || float.IsInfinity(host) || float.IsNaN(actual) || float.IsInfinity(actual))
                        throw new InvalidDataException("Non-finite dig icon geometry");
                    maxError=Math.Max(maxError,Math.Abs(actual-host));
                }
                if(input.BaseStream.Position!=input.BaseStream.Length) throw new InvalidDataException("Extra dig geometry");
            }
            if (maxError>.005f) throw new InvalidDataException("Dig icon differs from native pivot/bounds by "+maxError+" world units, "+Position(r));
            log("DUAL_GUEST_DIG_GEOMETRY round="+round+" cell="+cell+" maxError="+maxError.ToString("R",CultureInfo.InvariantCulture)+
                " tolerance=0.005 asset="+asset+" "+Position(r));
            return true;
        }
    }
}
