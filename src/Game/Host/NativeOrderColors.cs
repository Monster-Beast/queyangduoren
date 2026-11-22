using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeOrderColors
    {
        static readonly int colorId=Shader.PropertyToID("_Color");
        static MaterialPropertyBlock tint;

        internal static uint Pack(Color32 c) => (uint)(c.r | c.g<<8 | c.b<<16 | c.a<<24);
        internal static Color32 Unpack(uint value) =>
            new Color32((byte)value,(byte)(value>>8),(byte)(value>>16),(byte)(value>>24));

        internal static Color32 BuildingTint(ColonyCell sample) => sample.HasBlueprintTint
            ? Unpack(sample.BlueprintTint) : (sample.BuildingStyle & 128)!=0
            ? new Color32(255,90,80,220) : new Color32(255,255,255,255);

        internal static void CaptureDig(Diggable dig,ref ColonyCell sample)
        {
            var material=dig?.materialDisplay?.sharedMaterial;
            if (material==null || dig.materials==null || !material.HasProperty(colorId)) return;
            // Diggable changes the texture for harder tiles and instantiates the selected
            // material to tint each order independently when reachability changes.
            for (int i=0;i<dig.materials.Length && i<byte.MaxValue;i++)
            {
                var asset=dig.materials[i];
                if (asset==null || asset.shader!=material.shader || asset.mainTexture!=material.mainTexture) continue;
                sample.DigMaterial=(byte)(i+1);
                sample.DigTint=Pack(material.color);
                return;
            }
        }

        internal static void ApplyDig(MeshRenderer renderer,ColonyCell sample)
        {
            if (renderer==null) return;
            var materials=Assets.TryGetPrefab(new Tag(DigPlacerConfig.ID))?.GetComponent<Diggable>()?.materials;
            int index=sample.DigMaterial-1;
            if (materials==null || index<0 || index>=materials.Length || materials[index]==null) return;
            renderer.sharedMaterial=materials[index];
            if (tint==null) tint=new MaterialPropertyBlock();
            renderer.GetPropertyBlock(tint);
            tint.SetColor(colorId,Unpack(sample.DigTint));
            renderer.SetPropertyBlock(tint);
        }
    }
}
