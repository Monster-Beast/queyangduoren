using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeColonyLighting
    {
        sealed class Source
        {
            internal Light2D Light;
            internal int Order;
        }
        static readonly Dictionary<int,Source> sources=new Dictionary<int,Source>();
        static readonly List<Light2D> renderers=new List<Light2D>();
        static readonly List<int> removed=new List<int>();
        static readonly List<ColonyLight> captured=new List<ColonyLight>();
        static byte[] cells;
        static int sceneWorld=-1;
        static Texture2D texture;
        internal static void Capture(ColonyView view,WorldContainer world,int sample)
        {
            view.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            for(int i=0;i<view.Cells.Length;i++)
            {
                if ((view.Cells[i].Flags&1)==0) continue;
                int cell=Grid.XYToCell(view.X+i%ColonyView.Width,view.Y+i/ColonyView.Width);
                if (Grid.LightCount[cell]>0) view.LightMask[i/8]|=(byte)(1<<(i%8));
            }
            captured.Clear();
            var all=Components.Light2Ds.Items;
            for(int i=0;i<all.Count;i++)
            {
                var l=all[i]; if(l==null || !Grid.IsValidCell(l.cachedCell) || Grid.WorldIdx[l.cachedCell]!=world.id) continue;
                var p=l.transform.position;var tint=l.Color*l.IntensityAnimation;Color32 overlay=l.overlayColour;
                captured.Add(new ColonyLight{Id=l.GetInstanceID(),Order=i,Shape=(byte)l.shape,X=p.x,Y=p.y,Z=p.z,
                    OffsetX=l.Offset.x,OffsetY=l.Offset.y,Range=l.Range,DirectionX=l.Direction.x,DirectionY=l.Direction.y,
                    Angle=l.Angle,Red=tint.r,Green=tint.g,Blue=tint.b,DrawOverlay=l.drawOverlay,
                    Overlay=(uint)(overlay.r|overlay.g<<8|overlay.b<<16|overlay.a<<24)});
            }
            captured.Sort((a,b)=>a.Id.CompareTo(b.Id));
            int pages=Math.Max(1,(captured.Count+ColonyView.MaxLights-1)/ColonyView.MaxLights);
            int start=(sample<0 ? Time.frameCount : sample)%pages*ColonyView.MaxLights;
            int end=Math.Min(start+ColonyView.MaxLights,captured.Count);
            view.LightFrom=start==0?int.MinValue:captured[start-1].Id+1;
            view.LightThrough=end==captured.Count?int.MaxValue:captured[end-1].Id;
            view.Lights=captured.GetRange(start,end-start).ToArray();
        }
        internal static void Receive(ColonyView v,int world)
        {
            if (v.LightMask==null) return;
            if (world!=sceneWorld || cells==null || cells.Length!=Grid.CellCount)
            {
                Release();sceneWorld=world;cells=new byte[Grid.CellCount];
            }
            for(int i=0;i<v.Cells.Length;i++)
            {
                int x=v.X+i%ColonyView.Width,y=v.Y+i/ColonyView.Width;
                if((v.Cells[i].Flags&1)==0 || x>=Grid.WidthInCells || y>=Grid.HeightInCells) continue;
                int c=Grid.XYToCell(x,y);
                if(Grid.WorldIdx[c]==world) cells[c]=(byte)((v.LightMask[i/8]&(1<<(i%8)))!=0 ? 2 : 1);
            }
            removed.Clear();
            foreach(int id in sources.Keys) if(id>=v.LightFrom && id<=v.LightThrough) removed.Add(id);
            foreach(var s in v.Lights)
            {
                removed.Remove(s.Id);
                if(!sources.TryGetValue(s.Id,out var entry))
                {
                    var obj=new GameObject("Co-op light");obj.SetActive(false);
                    var light=obj.AddComponent<Light2D>();
                    light.materialPropertyBlock=new MaterialPropertyBlock();
                    entry=new Source{Light=light};sources.Add(s.Id,entry);
                }
                entry.Order=s.Order;var l=entry.Light;
                l.transform.position=new Vector3(s.X,s.Y,s.Z);
                l.Offset=new Vector2(s.OffsetX,s.OffsetY);
                l.cachedCell=Grid.PosToCell(l.transform.position+(Vector3)l.Offset);
                l.Range=s.Range;l.Direction=new Vector2(s.DirectionX,s.DirectionY);l.Angle=s.Angle;l.shape=(LightShape)s.Shape;
                l.Color=new Color(s.Red,s.Green,s.Blue,1);l.drawOverlay=s.DrawOverlay;
                l.overlayColour=new Color32((byte)s.Overlay,(byte)(s.Overlay>>8),(byte)(s.Overlay>>16),(byte)(s.Overlay>>24));
            }
            foreach(int id in removed)
            { UnityEngine.Object.Destroy(sources[id].Light.gameObject);sources.Remove(id); }
            renderers.Clear();
            foreach(var s in sources.Values.OrderBy(s=>s.Order)) renderers.Add(s.Light);
        }
        internal static void Release()
        {
            foreach(var s in sources.Values) if(s.Light!=null) UnityEngine.Object.Destroy(s.Light.gameObject);
            sources.Clear();renderers.Clear();cells=null;sceneWorld=-1;
            if(texture!=null) UnityEngine.Object.Destroy(texture);
            texture=null;
        }
        internal static void Refresh(int left,int bottom,int right,int top)
        {
            if (cells==null || Lighting.Instance==null) return;
            if(texture==null) texture=new Texture2D(Grid.WidthInCells,Grid.HeightInCells,TextureFormat.RGBA32,false,true)
                { name="Guest world light",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp };
            var pixels=texture.GetRawTextureData<Color32>();bool dirty=false;
            Color32 on=Lighting.Instance.Settings.LightColour;
            on.a=(byte)((on.r|on.g|on.b)==0?0:255);
            bool force=PropertyTextures.instance.ForceLightEverywhere;
            for(int y=bottom;y<=top;y++) for(int x=left;x<=right;x++)
            {
                int c=Grid.XYToCell(x,y);
                bool lit=cells[c]==0?Grid.LightCount[c]>0:cells[c]==2;
                var next=force?new Color32(255,255,255,255):lit?on:default;
                var old=pixels[c];
                if(old.a!=next.a || old.r!=next.r || old.g!=next.g || old.b!=next.b) { pixels[c]=next;dirty=true; }
            }
            if(dirty) texture.Apply(false,false);
        }
        internal static void Bind()
        {
            if(texture!=null) Shader.SetGlobalTexture("_WorldLightTex",texture);
        }
        [HarmonyPatch(typeof(PropertyTextures),nameof(PropertyTextures.GetTexture))]
        static class WorldLightTexture
        {
            static bool Prefix(PropertyTextures.Property __0,ref Texture __result)
            {
                if(__0!=PropertyTextures.Property.WorldLight || texture==null || !NativeGuestScene.Ready) return true;
                __result=texture;return false;
            }
        }
        // Keep ONI's complete camera, materials, cone/circle draw code and limits. Only change
        // which lights this renderer reads. The inactive display objects never run ONI emitters.
        [HarmonyPatch(typeof(LightBuffer),"LateUpdate")]
        static class SourceList
        {
            static List<Light2D> Items(Components.Cmps<Light2D> native) =>
                NativeGuestScene.Ready && cells!=null ? renderers : native.Items;
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var getter=AccessTools.PropertyGetter(typeof(Components.Cmps<Light2D>),"Items");
                var replace=AccessTools.Method(typeof(SourceList),nameof(Items));
                foreach(var instruction in instructions)
                {
                    if(instruction.Calls(getter)) {instruction.opcode=OpCodes.Call;instruction.operand=replace;}
                    yield return instruction;
                }
            }
        }
    }
}
