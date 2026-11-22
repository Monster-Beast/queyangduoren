using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Integration
{
    internal static class SteamSceneLight
    {
        static readonly string[] maps={"GasPressure","GasColour","FogOfWar","WorldLight","SolidLiquidGasMassForLight","Flow","MaterialData","ExposedToSunlight"};
        static string Raw(string field,int cell,int size)
        {
            var ptr=(IntPtr)AccessTools.DeclaredField(typeof(PropertyTextures),field).GetValue(null);
            if (ptr==IntPtr.Zero) return "-";
            var bytes=new byte[size]; Marshal.Copy(IntPtr.Add(ptr,checked(cell*size)),bytes,0,size);
            return BitConverter.ToString(bytes).Replace("-","");
        }
        static Color32 Pixel(Texture texture,int x,int y)
        {
            var old=RenderTexture.active;
            var rt=RenderTexture.GetTemporary(1,1,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var tex=new Texture2D(1,1,TextureFormat.RGBA32,false,true);
            try
            {
                var uv=new Vector2((x+.5f)/Grid.WidthInCells,(y+.5f)/Grid.HeightInCells);
                Graphics.Blit(texture,rt,Vector2.zero,uv);
                RenderTexture.active=rt;
                tex.ReadPixels(new Rect(0,0,1,1),0,0); tex.Apply();
                return tex.GetPixels32()[0];
            }
            finally { RenderTexture.active=old; RenderTexture.ReleaseTemporary(rt);UnityEngine.Object.Destroy(tex); }
        }
        internal static void Record(int round,string role,Action<string> log)
        {
            var camera=CameraController.Instance.baseCamera;
            var pt=PropertyTextures.instance;
            foreach (var flow in UnityEngine.Object.FindObjectsByType<FlowOffsetRenderer>(FindObjectsSortMode.None))
            {
                string name=(string)AccessTools.Field(typeof(FlowOffsetRenderer),"OffsetTextureName").GetValue(flow);
                var offset=Shader.GetGlobalTexture(name);
                var phase=AccessTools.Field(typeof(FlowOffsetRenderer),"CurrentTime").GetValue(flow);
                log("DUAL_SCENE_FLOW "+role+" map="+name+" phase="+phase+" active="+flow.isActiveAndEnabled+
                    " phaseVector="+Shader.GetGlobalVector((string)AccessTools.Field(typeof(FlowOffsetRenderer),"ParametersName").GetValue(flow))+
                    " offsetSize="+(offset==null ? "-" : offset.width+"x"+offset.height)+" gasOpacity="+Shader.GetGlobalVector("_GasOpacity"));
            }
            var methods=AccessTools.DeclaredField(typeof(PropertyTextures),"allTextureProperties");
            var props=(System.Collections.IEnumerable)methods.GetValue(pt);
            foreach (var uv in new[]{new Vector2(.28f,.74f),new Vector2(.73f,.58f),new Vector2(.49f,.75f)})
            {
                var w=camera.ViewportToWorldPoint(new Vector3(uv.x,uv.y,0));
                int cell=Grid.PosToCell(w); if (!Grid.IsValidCell(cell)) continue;
                Grid.CellToXY(cell,out int x,out int y);
                var line=new StringBuilder("DUAL_SCENE_LIGHT "+role+" round="+round+" cell="+x+","+y+
                    " element="+Grid.Element[cell].id+" mass="+Grid.Mass[cell].ToString("F3",CultureInfo.InvariantCulture)+
                    " visible="+Grid.Visible[cell]+" lightCount="+Grid.LightCount[cell]+" lux="+Grid.LightIntensity[cell]+
                    " timeScale="+Time.timeScale+" cycle="+GameClock.Instance.GetCurrentCycleAsPercentage().ToString("F4",CultureInfo.InvariantCulture));
                foreach (var prop in props)
                {
                    Type t=prop.GetType();
                    string name=AccessTools.Field(t,"simProperty").GetValue(prop).ToString();
                    if (!maps.Contains(name)) continue;
                    string shader=(string)AccessTools.Field(t,"texturePropertyName").GetValue(prop);
                    var texture=Shader.GetGlobalTexture(shader); if (texture==null) continue;
                    var c=Pixel(texture,x,y);
                    line.Append(" ").Append(name).Append("=").Append(ColorUtility.ToHtmlStringRGBA(c));
                }
                line.Append(" rawFlow=").Append(Raw("externalFlowTex",cell,8)).Append(" rawMatter=").Append(Raw("externalMaterialDataTex",cell,4));
                log(line.ToString());
            }
        }
    }
}
