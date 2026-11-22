using System;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    [HarmonyPatch(typeof(PropertyTextures), "LateUpdate", new Type[0])]
    internal static class GuestGasTextures
    {
        static readonly int pressureId=Shader.PropertyToID("_GasPressureTex"), colourId=Shader.PropertyToID("_GasColourTex");
        static Texture2D pressure, colour;
        static float nextScan;

        internal static void Release()
        {
            if (pressure!=null) UnityEngine.Object.Destroy(pressure);
            if (colour!=null) UnityEngine.Object.Destroy(colour);
            pressure=colour=null;
            nextScan=0;
        }

        static Texture2D Make(string name)
        {
            return new Texture2D(Grid.WidthInCells,Grid.HeightInCells,TextureFormat.RGBA32,false,true)
            { name=name, filterMode=FilterMode.Bilinear, wrapMode=TextureWrapMode.Clamp };
        }

        static void Postfix(Vector2 ___PressureRange,float ___MinPressureVisibility)
        {
            if (!NativeGuestScene.Ready || !CoopSession.IsNativeClient || !Grid.IsInitialized() ||
                global::Game.Instance==null || global::Game.Instance.IsLoading() || ClusterManager.Instance==null) return;
            if (colour==null || colour.width!=Grid.WidthInCells || colour.height!=Grid.HeightInCells)
            {
                Release();
                colour=Make("Guest gas colour");
                pressure=Make("Guest gas pressure");
            }
            if (Time.unscaledTime>=nextScan)
            {
                nextScan=Time.unscaledTime+.1f;
                Refresh(___PressureRange,___MinPressureVisibility);
            }
            Shader.SetGlobalTexture(pressureId,pressure);
            Shader.SetGlobalTexture(colourId,colour);
        }

        // Same gas/liquid and pressure mapping as PropertyTextures.UpdateGasColour and UpdatePressure.
        // Read the latest replicated cells even while the colony clock is paused.
        static void Refresh(Vector2 range,float minVisibility)
        {
            Grid.GetVisibleExtents(out int left,out int bottom,out int right,out int top);
            left=Math.Max(0,left-2); bottom=Math.Max(0,bottom-2);
            right=Math.Min(Grid.WidthInCells-1,right+2); top=Math.Min(Grid.HeightInCells-1,top+2);
            var colours=colour.GetRawTextureData<Color32>();
            var pressures=pressure.GetRawTextureData<Color32>();
            bool changed=false;
            for (int y=bottom;y<=top;y++)
            {
                for (int x=left;x<=right;x++)
                {
                    int cell=Grid.XYToCell(x,y);
                    var element=Grid.Element[cell];
                    var pixel=default(Color32);
                    byte alpha=0;
                    if (element!=null && Grid.IsActiveWorld(cell))
                    {
                        int above=element.IsLiquid ? Grid.CellAbove(cell) : cell;
                        if (element.IsGas || element.IsLiquid && Grid.IsValidCell(above))
                        {
                            pixel=element.substance.colour;
                            pixel.a=255;
                            if (Grid.Element[above]?.IsGas==true)
                            {
                                float p=Grid.Pressure[above];
                                float level=Mathf.Max(p>0 ? minVisibility : 0f,Mathf.Clamp01((p-range.x)/(range.y-range.x)));
                                alpha=(byte)(level*255f);
                            }
                        }
                    }
                    var old=colours[cell];
                    if (old.r==pixel.r && old.g==pixel.g && old.b==pixel.b && old.a==pixel.a && pressures[cell].a==alpha) continue;
                    colours[cell]=pixel;
                    pressures[cell]=new Color32(0,0,0,alpha);
                    changed=true;
                }
            }
            if (!changed) return;
            colour.Apply(false,false);
            pressure.Apply(false,false);
        }
    }
}
