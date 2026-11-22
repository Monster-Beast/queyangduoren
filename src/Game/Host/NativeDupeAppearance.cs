using System.Runtime.CompilerServices;

namespace Queyang.Game
{
    internal static class NativeDupeAppearance
    {
        sealed class Stamp { internal int Tick; }
        static readonly ConditionalWeakTable<KBatchedAnimController,Stamp> stamps=new ConditionalWeakTable<KBatchedAnimController,Stamp>();
        static int AccessoryId(Accessorizer a,AccessorySlot slot) => a.GetAccessory(slot)?.IdHash.HashValue ?? 0;

        internal static void Capture(KBatchedAnimController anim,ref ColonyDupe d)
        {
            if (anim==null) return;
            var a=anim.GetComponent<Accessorizer>();
            if (a==null) return;
            var slots=Db.Get().AccessorySlots;
            d.Hat=AccessoryId(a,slots.Hat); d.Hair=AccessoryId(a,slots.Hair); d.HatHair=AccessoryId(a,slots.HatHair);
            d.HeadVisibility=(byte)((anim.GetSymbolVisiblity(slots.Hat.targetSymbolId)?1:0) |
                (anim.GetSymbolVisiblity(slots.Hair.targetSymbolId)?2:0) | (anim.GetSymbolVisiblity(slots.HatHair.targetSymbolId)?4:0));
        }

        static void SetHair(Accessorizer a,AccessorySlot slot,int hash)
        {
            if (AccessoryId(a,slot)==hash) return;
            var next=slot.Lookup(new HashedString(hash));
            if (hash!=0 && next==null) return;
            var old=a.GetAccessory(slot);
            if (old!=null) a.RemoveAccessory(old);
            if (next!=null) a.AddAccessory(next);
        }

        internal static void Apply(KBatchedAnimController anim,ColonyDupe d,int tick)
        {
            var stamp=stamps.GetOrCreateValue(anim);
            if (tick<stamp.Tick) return;
            stamp.Tick=tick;
            var a=anim.GetComponent<Accessorizer>();
            if (a==null) return;
            var slots=Db.Get().AccessorySlots;
            if (AccessoryId(a,slots.Hat)!=d.Hat)
            {
                var hat=slots.Hat.Lookup(new HashedString(d.Hat));
                if (hat!=null || d.Hat==0) MinionResume.ApplyHat(hat?.Id,anim);
            }
            SetHair(a,slots.Hair,d.Hair);
            SetHair(a,slots.HatHair,d.HatHair);
            Visible(anim,slots.Hat,(d.HeadVisibility&1)!=0);
            Visible(anim,slots.Hair,(d.HeadVisibility&2)!=0);
            Visible(anim,slots.HatHair,(d.HeadVisibility&4)!=0);
        }

        static void Visible(KBatchedAnimController anim,AccessorySlot slot,bool show)
        {
            if (anim.GetSymbolVisiblity(slot.targetSymbolId)!=show) anim.SetSymbolVisiblity(slot.targetSymbolId,show);
        }
    }
}
