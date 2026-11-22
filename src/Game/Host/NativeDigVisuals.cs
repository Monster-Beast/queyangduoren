using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Queyang.Game
{
    // The attached tool is a body symbol. Its aimed beam and work target effect
    // are separate renderers and can start or stop independently.
    internal static class NativeDigVisuals
    {
        sealed class Source
        {
            internal MultitoolController.Instance Work;
            internal KBatchedAnimController Contact;
            internal KBatchedAnimTracker[] Children;
        }
        internal sealed class Visual
        {
            internal KBatchedAnimController Contact, Beam;
            internal int Cell=-1;
            internal float Last, OriginalDamage, LastDamage;
            internal bool Digging;
        }
        // This follows the actual native worker lifetime, including work begun
        // before a room is created. Old saves cannot alias a new worker with the same ID.
        static readonly ConditionalWeakTable<KBatchedAnimController,Source> sources=new ConditionalWeakTable<KBatchedAnimController,Source>();
        static readonly List<int> expired=new List<int>();
        static readonly Dictionary<int,Visual> visuals=new Dictionary<int,Visual>();
        static readonly AccessTools.FieldRef<MultitoolController.Instance,GameObject> Hit=
            AccessTools.FieldRefAccess<MultitoolController.Instance,GameObject>("hitEffect");

        [HarmonyPatch(typeof(MultitoolController.Instance),"CreateHitEffect")]
        static class Started
        {
            static void Postfix(MultitoolController.Instance __instance)
            {
                if(NativeGuestScene.Active || __instance.workable?.worker==null) return;
                var w=__instance.workable.worker;
                var owner=w.GetComponent<KBatchedAnimController>();
                if(owner==null) return;
                sources.Remove(owner);
                sources.Add(owner,new Source { Work=__instance,Contact=Hit(__instance)?.GetComponent<KBatchedAnimController>(),
                    Children=w.GetComponentsInChildren<KBatchedAnimTracker>(true) });
            }
        }
        [HarmonyPatch(typeof(MultitoolController.Instance),"DestroyHitEffect")]
        static class Stopped
        {
            static void Prefix(MultitoolController.Instance __instance)
            {
                var worker=__instance.workable?.worker;
                if(worker==null) return;
                var owner=worker.GetComponent<KBatchedAnimController>();
                if(owner!=null && sources.TryGetValue(owner,out var source) && source.Work==__instance) sources.Remove(owner);
            }
        }

        internal static KBatchedAnimController FindBeam(KBatchedAnimTracker[] trackers)
        {
            foreach(var t in trackers)
            {
                if(t==null || !t.useTargetPoint || !t.gameObject.activeInHierarchy) continue;
                var a=t.GetComponent<KBatchedAnimController>();
                if(Visible(a)) return a;
            }
            return null;
        }
        static bool Visible(KBatchedAnimController a) => a!=null && a.enabled && a.gameObject.activeInHierarchy &&
            a.IsVisible() && a.GetCurrentAnim()!=null && a.GetBatchInstanceData()!=null;
        internal static void Write(BinaryWriter w,KBatchedAnimController owner)
        {
            if(!sources.TryGetValue(owner,out var source) || source.Work.workable==null)
            { w.Write((byte)0); return; }
            var beam=FindBeam(source.Children);
            bool contact=Visible(source.Contact);
            int cell=Grid.PosToCell(source.Work.workable);
            if(!Grid.IsValidCell(cell) || (!contact && beam==null)) { w.Write((byte)0); return; }
            bool digging=source.Work.workable is Diggable;
            w.Write((byte)((contact?1:0)|(beam==null?0:2)|(digging?0:4))); w.Write(cell);
            w.Write(digging ? Grid.Damage[cell] : 0f);
            if(contact) WriteEffect(w,source.Contact);
            if(beam!=null) WriteEffect(w,beam);
        }
        static void WriteEffect(BinaryWriter w,KBatchedAnimController a)
        {
            var p=NativeDupePose.Capture(a,a.transform.position);
            w.Write(p.File); w.Write(a.currentAnim.HashValue); w.Write(p.Frame);
            w.Write((int)a.sceneLayer); Color32 c=a.TintColour;
            w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
            w.Write(p.X); w.Write(p.Y); w.Write(p.M00); w.Write(p.M01); w.Write(p.M02); w.Write(p.M10); w.Write(p.M11); w.Write(p.M12);
        }
        internal static Visual Get(int id) => visuals.TryGetValue(id,out var v)?v:null;
        internal static void Read(BinaryReader r,int id,KBatchedAnimController owner)
        {
            byte mask=r.ReadByte();
            if(mask==4 || mask>7) throw new InvalidDataException("Bad work visual flags");
            if(mask==0) { if(owner!=null) Clear(id); return; }
            int cell=r.ReadInt32(); float damage=r.ReadSingle();
            if(!Grid.IsValidCell(cell) || !(damage>=0 && damage<=1)) throw new InvalidDataException("Bad digging target");
            Visual v=Get(id);
            if(v==null) v=new Visual();
            if(owner!=null)
            {
                visuals[id]=v; v.Last=Time.unscaledTime;
                bool digging=(mask&4)==0;
                if(v.Cell!=cell || v.Digging!=digging)
                {
                    RestoreDamage(v);
                    v.Cell=cell; v.Digging=digging; v.OriginalDamage=Grid.Damage[cell];
                }
                if(digging) {v.LastDamage=damage; SetDamage(cell,damage);}
                owner.GetComponent<AnimEventHandler>()?.UpdateWorkTarget(Grid.CellToPosCCC(cell,Grid.SceneLayer.FXFront));
            }
            if((mask&1)!=0) ApplyEffect(r,owner,ref v.Contact);
            else if(owner!=null) Remove(ref v.Contact);
            if((mask&2)!=0) ApplyEffect(r,owner,ref v.Beam);
            else if(owner!=null) Remove(ref v.Beam);
        }
        static void ApplyEffect(BinaryReader r,KBatchedAnimController owner,ref KBatchedAnimController a)
        {
            int file=r.ReadInt32(), name=r.ReadInt32(), frame=r.ReadInt32(), layer=r.ReadInt32();
            var color=new Color32(r.ReadByte(),r.ReadByte(),r.ReadByte(),r.ReadByte());
            float F()
            {
                float f=r.ReadSingle();
                if(float.IsNaN(f) || float.IsInfinity(f)) throw new InvalidDataException("Invalid digging render transform");
                return f;
            }
            float x=F(),y=F();
            var m=new Matrix2x3 { m00=F(),m01=F(),m02=F(),m10=F(),m11=F(),m12=F() };
            if(layer<0 || layer>=(int)Grid.SceneLayer.SceneMAX || frame< -1 || frame>65535)
                throw new InvalidDataException("Bad digging effect frame/layer");
            if(owner==null || !Assets.TryGetAnim(new HashedString(file),out var asset)) return;
            if(a!=null && a.AnimFiles[0]!=asset) Remove(ref a);
            if(a==null)
            {
                var obj=new GameObject("Co-op digging "+asset.name);
                obj.SetActive(false); obj.layer=owner.gameObject.layer; obj.transform.SetParent(owner.transform,false);
                a=obj.AddComponent<KBatchedAnimController>(); a.AnimFiles=new[]{asset}; a.initialAnim=asset.GetData().GetAnim(0).name.ToString();
                a.sceneLayer=(Grid.SceneLayer)layer; a.isMovable=true;
                obj.SetActive(true);
            }
            a.transform.position=new Vector3(x,y,Grid.GetLayerZ((Grid.SceneLayer)layer));
            a.TintColour=color;
            var hash=new HashedString(name);
            if(a.currentAnim!=hash && a.HasAnimation(hash)) a.Play(hash,KAnim.PlayMode.Loop);
            var current=a.GetCurrentAnim();
            if(current!=null && frame>=0 && frame<current.numFrames)
            {
                a.SetPositionPercent((float)frame/current.numFrames); a.UpdateAnim(0);
            }
            a.GetBatchInstanceData()?.SetOverrideTransformMatrix(m); a.SetDirty();
        }
        static void Remove(ref KBatchedAnimController a)
        {
            if(a==null) return;
            a.SetVisiblity(false); UnityEngine.Object.Destroy(a.gameObject); a=null;
        }
        static void SetDamage(int cell,float value)
        {
            if(Grid.Damage==null || cell<0 || cell>=Grid.Damage.Length || Grid.Damage[cell]==value) return;
            Grid.Damage[cell]=value;
            World.Instance?.groundRenderer?.MarkDirty(cell);
        }
        static void RestoreDamage(Visual v)
        {
            if(v.Digging && v.Cell>=0 && Grid.Damage!=null && v.Cell<Grid.Damage.Length && Grid.Damage[v.Cell]==v.LastDamage)
                SetDamage(v.Cell,Grid.Solid[v.Cell]?v.OriginalDamage:0);
        }
        internal static void Expire(float now)
        {
            expired.Clear();
            foreach(var pair in visuals)
                if(now-pair.Value.Last>.8f) expired.Add(pair.Key);
            foreach(int id in expired) Clear(id);
        }
        internal static void Reset()
        {
            foreach(int id in new List<int>(visuals.Keys)) Clear(id);
            expired.Clear();
        }
        internal static void Clear(int id)
        {
            if(!visuals.TryGetValue(id,out var v)) return;
            visuals.Remove(id); RestoreDamage(v); Remove(ref v.Contact); Remove(ref v.Beam);
        }
    }
}
