using System;
using System.Collections.Generic;
using UnityEngine;

namespace Queyang.Game
{
    // Render only the build symbols actually used by the host's WearableAccessorizer.
    // Neither equipping nor spawning a wearable on the guest is necessary.
    internal sealed class NativeWearDisplay
    {
        sealed class Attachment
        {
            internal KAnim.Build.Symbol Source;
            internal bool Visible, PreviousVisibility;
        }
        const int MirrorPriority = 10000;
        sealed class Baseline
        {
            internal SymbolOverrideController Controller;
            internal readonly List<SymbolOverrideController.SymbolEntry> Removed = new List<SymbolOverrideController.SymbolEntry>();
        }
        static readonly Dictionary<int,Baseline> suppressed = new Dictionary<int,Baseline>();
        readonly Dictionary<int,Attachment> current = new Dictionary<int,Attachment>();
        readonly Dictionary<int,Attachment> desired = new Dictionary<int,Attachment>();
        readonly List<int> remove = new List<int>();
        readonly SymbolOverrideController controller;
        readonly KBatchedAnimController anim;

        internal NativeWearDisplay(KSelectable wearer)
        {
            controller=wearer.GetComponent<SymbolOverrideController>();
            anim=wearer.GetComponent<KBatchedAnimController>();
        }

        // An original temporary-save duplicant can already be dressed when joining.
        // When the host removes that item, uncover the underlying local accessories.
        internal static void HideBaseline(int id)
        {
            if (suppressed.ContainsKey(id)) return;
            var original=KPrefabIDTracker.Get().GetInstance(id);
            var gear=original==null?null:original.GetComponent<Equippable>();
            if (gear==null || !gear.isEquipped || gear.assignee==null) return;
            var owner=gear.assignee.GetSoleOwner()?.GetComponent<MinionAssignablesProxy>()?.GetTargetGameObject();
            var control=owner==null?null:owner.GetComponent<SymbolOverrideController>();
            var build=gear.GetBuildOverride()?.GetData()?.build;
            if (build==null || control==null) return;
            var baseline=new Baseline { Controller=control };
            foreach(var entry in control.GetSymbolOverrides)
                if(entry.priority!=MirrorPriority && entry.sourceSymbol?.build==build &&
                    control.RemoveSymbolOverride(entry.targetSymbol,entry.priority)) baseline.Removed.Add(entry);
            suppressed.Add(id,baseline);
        }

        internal static void RestoreBaseline()
        {
            foreach(var baseline in suppressed.Values)
                if (baseline.Controller!=null)
                    foreach(var entry in baseline.Removed)
                        baseline.Controller.AddSymbolOverride(entry.targetSymbol,entry.sourceSymbol,entry.priority);
            suppressed.Clear();
        }

        internal static void Capture(ColonyEntity row,GameObject wearer,Equippable gear)
        {
            var file=gear.GetBuildOverride(); // Includes the actual EquippableFacade.
            var symbols=file==null?null:file.GetData()?.build?.symbols;
            var renderer=wearer.GetComponent<SymbolOverrideController>();
            var animator=wearer.GetComponent<KBatchedAnimController>();
            if (symbols==null || renderer==null || animator==null) return;
            int count=Math.Min(256,symbols.Length);
            row.WearBuild=new HashedString(file.name).HashValue;
            row.WearSymbols=new byte[(count+3)/4];
            var entries=renderer.GetSymbolOverrides;
            for (int i=0;i<count;i++)
            {
                var symbol=symbols[i];
                if (symbol==null) continue;
                SymbolOverrideController.SymbolEntry winner=default;
                int best=int.MinValue;
                foreach (var entry in entries)
                    if (entry.targetSymbol.HashValue==symbol.hash.HashValue && entry.priority>best)
                    { winner=entry; best=entry.priority; }
                if (winner.sourceSymbol!=symbol) continue;
                int state=animator.GetSymbolVisiblity(symbol.hash)?2:1;
                row.WearSymbols[i/4]|=(byte)(state<<(2*(i%4)));
            }
        }

        internal void Apply(List<ColonyEntity> items)
        {
            if (controller==null || anim==null) return;
            desired.Clear();
            if (items!=null)
                foreach(var item in items)
                {
                    if (!item.IsWorn || item.WearBuild==0 || item.WearSymbols.Length==0 ||
                        !Assets.TryGetAnim(new HashedString(item.WearBuild),out var file)) continue;
                    var symbols=file.GetData()?.build?.symbols;
                    if (symbols==null) continue;
                    int count=Math.Min(symbols.Length,item.WearSymbols.Length*4);
                    for(int i=0;i<count;i++)
                    {
                        int state=(item.WearSymbols[i/4]>>(2*(i%4)))&3;
                        var symbol=symbols[i];
                        if (state==0 || symbol==null) continue;
                        int key=symbol.hash.HashValue;
                        if (current.TryGetValue(key,out var old) && old.Source==symbol && old.Visible==(state==2))
                            desired[key]=old;
                        else desired[key]=new Attachment {Source=symbol,Visible=state==2};
                    }
                }
            remove.Clear();
            foreach(var old in current)
            {
                if (desired.TryGetValue(old.Key,out var next) && next==old.Value) continue;
                controller.RemoveSymbolOverride(new HashedString(old.Key),MirrorPriority);
                anim.SetSymbolVisiblity(new KAnimHashedString(old.Key),old.Value.PreviousVisibility);
                remove.Add(old.Key);
            }
            foreach(int key in remove) current.Remove(key);
            foreach(var pair in desired)
            {
                if (current.ContainsKey(pair.Key)) continue;
                var symbol=pair.Value.Source;
                pair.Value.PreviousVisibility=anim.GetSymbolVisiblity(symbol.hash);
                controller.AddSymbolOverride(new HashedString(pair.Key),symbol,MirrorPriority);
                anim.SetSymbolVisiblity(symbol.hash,pair.Value.Visible);
                current[pair.Key]=pair.Value;
            }
        }
    }
}
