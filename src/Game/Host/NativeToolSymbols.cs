using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace Queyang.Game
{
    // SnapOn attaches tools and carried art to body symbols at priority 5.
    // The same pose can draw a drill, a construction gun, or an empty hand.
    internal static class NativeToolSymbols
    {
        const int SnapPriority=5;
        static readonly HashedString[] slots={"snapTo_rgtHand","snapTo_chest","snapTo_neck","snapTo_goggles","foot"};
        static readonly AccessTools.FieldRef<SymbolOverrideController,List<SymbolOverrideController.SymbolEntry>> entries=
            AccessTools.FieldRefAccess<SymbolOverrideController,List<SymbolOverrideController.SymbolEntry>>("symbolOverrides");

        internal static void Write(BinaryWriter writer,KBatchedAnimController anim)
        {
            var controller=anim.GetComponent<SymbolOverrideController>();
            int attached=0,visible=0;
            for(int i=0;i<slots.Length;i++)
            {
                if(controller!=null && controller.GetSymbolOverrideIdx(slots[i],SnapPriority)>=0) attached|=1<<i;
                if(anim.GetSymbolVisiblity(slots[i])) visible|=1<<i;
            }
            writer.Write((byte)attached);
            writer.Write((byte)visible);
            for(int i=0;i<slots.Length;i++)
            {
                if((attached&(1<<i))==0) continue;
                var symbol=entries(controller)[controller.GetSymbolOverrideIdx(slots[i],SnapPriority)].sourceSymbol;
                writer.Write(symbol.build.fileHash.HashValue);
                writer.Write(symbol.hash.HashValue);
            }
        }

        internal static void Read(BinaryReader reader,KBatchedAnimController anim)
        {
            int attached=reader.ReadByte(),visible=reader.ReadByte();
            if((attached|visible)>=(1<<slots.Length)) throw new InvalidDataException("Invalid tool attachment mask");
            var controller=anim==null ? null : anim.GetComponent<SymbolOverrideController>();
            for(int i=0;i<slots.Length;i++)
            {
                var slot=slots[i];
                KAnim.Build.Symbol source=null;
                if((attached&(1<<i))!=0)
                {
                    var file=new HashedString(reader.ReadInt32());
                    var symbol=new KAnimHashedString(reader.ReadInt32());
                    if(controller==null || !Assets.TryGetAnim(file,out var asset)) continue;
                    source=asset.GetData()?.build?.GetSymbol(symbol);
                    if(source==null) continue;
                }
                if(controller==null) continue;
                int idx=controller.GetSymbolOverrideIdx(slot,SnapPriority);
                if(source==null && idx>=0) controller.RemoveSymbolOverride(slot,SnapPriority);
                if(source!=null && (idx<0 || entries(controller)[idx].sourceSymbol!=source))
                    controller.AddSymbolOverride(slot,source,SnapPriority);
                bool show=(visible&(1<<i))!=0;
                if(anim.GetSymbolVisiblity(slot)!=show) anim.SetSymbolVisiblity(slot,show);
            }
        }
    }
}
