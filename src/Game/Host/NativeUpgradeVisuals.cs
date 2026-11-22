using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeUpgradeVisuals
    {
        internal const int Header = -6;
        internal const string Asset = "upgrade_fx_kanim";
        static readonly HashedString Clip = new HashedString("upgrade");

        sealed class Source
        {
            internal int Owner;
            internal Transform Target;
            internal KBatchedAnimController Effect;
            internal float StoppedAt = -1f;
        }

        sealed class Visual
        {
            internal int Owner, Tick;
            internal float Arrived;
            internal KBatchedAnimController Effect;
            internal MotionPhase Phase;
        }

        static readonly Dictionary<int,Source> sources = new Dictionary<int,Source>();
        static readonly Dictionary<int,Visual> visuals = new Dictionary<int,Visual>();
        static readonly List<int> expired = new List<int>();
        static int nextId;

        [HarmonyPatch(typeof(FXHelpers), nameof(FXHelpers.CreateEffect),
            new[] {typeof(string),typeof(Vector3),typeof(Transform),typeof(bool),typeof(Grid.SceneLayer),typeof(bool)})]
        static class Created
        {
            static void Postfix(string __0, Transform __2, KBatchedAnimController __result)
            {
                if (!CoopSession.IsHosting || __0 != Asset || __2 == null || __result == null ||
                    !__2.TryGetComponent<MinionIdentity>(out _)) return;
                int id = __2.GetComponent<KPrefabID>().InstanceID;
                if (id > 0) sources.Add(++nextId,new Source { Owner=id, Target=__2, Effect=__result });
            }
        }

        internal static IEnumerable<byte[]> Capture(WorldContainer world, int left, int bottom, bool paused)
        {
            float now = Time.unscaledTime;
            expired.Clear();
            foreach (var entry in sources)
            {
                var s = entry.Value;
                if (s.Effect == null && s.StoppedAt < 0) s.StoppedAt = now;
                if (s.Target == null || s.StoppedAt >= 0 && now-s.StoppedAt > .6f)
                {
                    expired.Add(entry.Key);
                    continue;
                }
                var pos = s.Target.position;
                int cell = Grid.PosToCell(pos);
                if (!Grid.IsValidCell(cell) || Grid.Visible[cell] == 0 || Grid.WorldIdx[cell] != world.id ||
                    left >= 0 && (pos.x < left-4 || pos.x >= left+ColonyView.Width+4 ||
                    pos.y < bottom-4 || pos.y >= bottom+ColonyView.Height+4)) continue;

                int frame = -1, count = 0;
                Vector3 offset = default;
                float rate = 0;
                if (s.Effect != null)
                {
                    var clip = s.Effect.GetCurrentAnim();
                    if (clip == null || s.Effect.currentAnim != Clip) continue;
                    count = clip.numFrames;
                    frame = NativeDupePose.RenderFrame(s.Effect);
                    if (frame < 0 || frame >= count) continue;
                    offset = s.Effect.transform.localPosition;
                    if (!paused && !s.Effect.IsStopped())
                        rate = Mathf.Clamp(s.Effect.GetPlaySpeed()*clip.frameRate*Time.timeScale,0,600);
                }
                using (var stream = new MemoryStream())
                using (var w = new BinaryWriter(stream))
                {
                    w.Write(Header); w.Write(entry.Key); w.Write(s.Owner); w.Write(Time.frameCount); w.Write(now);
                    w.Write(frame); w.Write(count); w.Write(rate); w.Write(offset.x); w.Write(offset.y); w.Write(offset.z);
                    yield return stream.ToArray();
                }
            }
            foreach (int id in expired) sources.Remove(id);
        }

        internal static void Apply(byte[] packet)
        {
            if (packet.Length != 44) throw new InvalidDataException("Invalid upgrade effect packet");
            using (var r = new BinaryReader(new MemoryStream(packet,false)))
            {
                r.ReadInt32();
                int id=r.ReadInt32(), owner=r.ReadInt32(), tick=r.ReadInt32();
                float hostTime=r.ReadSingle();
                int frame=r.ReadInt32(), count=r.ReadInt32();
                float rate=r.ReadSingle();
                var localPosition=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
                if (id <= 0 || owner <= 0 || tick <= 0 || !(hostTime >= 0 && hostTime < float.MaxValue) ||
                    !(rate >= 0 && rate <= 600) || !(localPosition.sqrMagnitude < 16) ||
                    (frame == -1 ? count != 0 : frame < 0 || count < 1 || count > 65536 || frame >= count))
                    throw new InvalidDataException("Invalid upgrade effect sample");

                float now=Time.unscaledTime;
                if (!visuals.TryGetValue(id,out var visual))
                {
                    if (frame < 0) return;
                    var dupe=NativeWorldMirror.GetDupeSelectable(owner);
                    if (dupe == null) return;
                    var native=FXHelpers.CreateEffect(Asset,dupe.transform.position,dupe.transform);
                    native.Play(Clip,KAnim.PlayMode.Once);
                    native.PlaySpeedMultiplier=0;
                    visuals.Add(id,visual=new Visual { Owner=owner,Effect=native });
                }
                if (tick <= visual.Tick || visual.Owner != owner) return;
                float interval=now-visual.Arrived;
                bool continuing=visual.Tick>0 && interval<.45f;
                visual.Tick=tick;
                visual.Arrived=now;
                if (frame < 0)
                {
                    Hide(visual);
                    return;
                }
                if (visual.Effect == null) return;
                visual.Effect.transform.localPosition=localPosition;
                visual.Phase.Anchor(frame,count,rate,false,hostTime,interval,continuing);
                Draw(visual,now);
            }
        }

        static void Draw(Visual v,float now)
        {
            var effect=v.Effect;
            if (effect == null) return;
            int frame=v.Phase.Frame(now-v.Arrived);
            if (frame >= 0 && frame < effect.GetCurrentNumFrames()) NativeBuildingAnimation.SetFrame(effect,frame);
        }

        internal static void Draw(float now)
        {
            expired.Clear();
            foreach (var entry in visuals)
            {
                if (now-entry.Value.Arrived > .85f)
                {
                    Hide(entry.Value);
                    expired.Add(entry.Key);
                }
                else Draw(entry.Value,now);
            }
            foreach (int id in expired) visuals.Remove(id);
        }

        static void Hide(Visual v)
        {
            if (v.Effect == null) return;
            v.Effect.SetVisiblity(false);
            UnityEngine.Object.Destroy(v.Effect.gameObject);
            v.Effect=null;
        }

        internal static void Reset()
        {
            foreach (var visual in visuals.Values) Hide(visual);
            visuals.Clear();
            sources.Clear();
            nextId=0;
        }
    }
}
