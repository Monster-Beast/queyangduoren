using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using Klei.AI;
using UnityEngine;

namespace Queyang.Game
{
    internal static class NativeDupeFeedback
    {
        internal const int Header = -5;
        const byte ThoughtBubble = 0, ConversationBubble = 1, FloatingEffect = 2;
        const float RetrySeconds = .6f, BubbleExpiry = .9f;

        sealed class Sample
        {
            internal GameObject Target;
            internal int Serial, Backdrop, Icon, Mode;
            internal byte Kind;
            internal bool Visible;
            internal string Text;
            internal float Time;
        }

        static readonly Dictionary<(int Id, byte Kind), Sample> bubbles = new Dictionary<(int, byte), Sample>();
        static readonly Queue<(int Id, Sample Effect)> effects = new Queue<(int, Sample)>();
        static readonly Dictionary<(int Id, byte Kind), Sample> displayed = new Dictionary<(int, byte), Sample>();
        static readonly Dictionary<int, float> seenEffects = new Dictionary<int, float>();
        static readonly List<(int Id, byte Kind)> expiredBubbles = new List<(int, byte)>();
        static readonly List<int> expiredEffects = new List<int>();
        static int serial;
        static readonly Func<NameDisplayScreen, GameObject, NameDisplayScreen.Entry> entryFor =
            AccessTools.MethodDelegate<Func<NameDisplayScreen, GameObject, NameDisplayScreen.Entry>>(
                AccessTools.Method(typeof(NameDisplayScreen), "GetEntry"));

        [HarmonyPatch(typeof(NameDisplayScreen), nameof(NameDisplayScreen.SetThoughtBubbleDisplay))]
        static class ObserveThought
        {
            static void Postfix(GameObject __0, bool __1, string __2, Sprite __3, Sprite __4) =>
                Record(__0, ThoughtBubble, __1, __2, __3, __4, null);
        }

        [HarmonyPatch(typeof(NameDisplayScreen), nameof(NameDisplayScreen.SetThoughtBubbleConvoDisplay))]
        static class ObserveConversation
        {
            static void Postfix(GameObject __0, bool __1, string __2, Sprite __3, Sprite __4, Sprite __5) =>
                Record(__0, ConversationBubble, __1, __2, __3, __4, __5);
        }

        [HarmonyPatch(typeof(EffectInstance), MethodType.Constructor,
            new[] { typeof(GameObject), typeof(Effect), typeof(bool), typeof(Func<string, object, string>) })]
        static class ObserveEffect
        {
            static void Postfix(GameObject __0, Effect __1)
            {
                if (!CoopSession.IsHosting || __1?.triggerFloatingText != true ||
                    PopFXManager.Instance == null || !TryId(__0, out int id)) return;
                if (effects.Count >= 64) effects.Dequeue();
                effects.Enqueue((id, new Sample {
                    Target = __0, Kind = FloatingEffect, Serial = ++serial,
                    Icon = new HashedString(__1.Id).HashValue, Time = Time.unscaledTime
                }));
            }
        }

        static bool TryId(GameObject obj, out int id)
        {
            id = obj != null && obj.TryGetComponent<MinionIdentity>(out _) ? obj.GetComponent<KPrefabID>()?.InstanceID ?? 0 : 0;
            return id > 0;
        }

        static int SpriteHash(Sprite sprite) => sprite == null ? 0 : new HashedString(sprite.name).HashValue;

        static void Record(GameObject target, byte kind, bool visible, string text, Sprite backdrop, Sprite icon, Sprite mode)
        {
            if (!CoopSession.IsHosting || !TryId(target, out int id)) return;
            var key = (id, kind);
            if (!visible && (!bubbles.TryGetValue(key, out var old) || !old.Visible)) return;
            string label = text ?? "";
            if (Encoding.UTF8.GetByteCount(label) > 800) label = "";
            bubbles[key] = new Sample {
                Target = target, Kind = kind, Serial = ++serial, Visible = visible, Text = label, Time = Time.unscaledTime,
                Backdrop = SpriteHash(backdrop), Icon = SpriteHash(icon), Mode = SpriteHash(mode)
            };
        }

        internal static IEnumerable<byte[]> Capture(WorldContainer world, int left, int bottom)
        {
            float now = Time.unscaledTime;
            expiredBubbles.Clear();
            foreach (var row in bubbles)
            {
                var sample = row.Value;
                if (sample.Target == null || !sample.Visible && now - sample.Time > RetrySeconds)
                    expiredBubbles.Add(row.Key);
                else if (InView(sample.Target, world, left, bottom))
                    yield return Encode(row.Key.Id, sample);
            }
            foreach (var key in expiredBubbles) bubbles.Remove(key);
            while (effects.Count != 0 && now - effects.Peek().Effect.Time > RetrySeconds) effects.Dequeue();
            foreach (var row in effects)
                if (InView(row.Effect.Target, world, left, bottom)) yield return Encode(row.Id, row.Effect);
        }

        static bool InView(GameObject target, WorldContainer world, int left, int bottom)
        {
            if (target == null) return false;
            int cell = Grid.PosToCell(target);
            if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell] != world.id || Grid.Visible[cell] == 0) return false;
            Grid.CellToXY(cell, out int x, out int y);
            return left < 0 || x >= left - 4 && x < left + ColonyView.Width + 4 &&
                y >= bottom - 4 && y < bottom + ColonyView.Height + 4;
        }

        static byte[] Encode(int id, Sample sample)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Header); writer.Write(id); writer.Write(sample.Serial); writer.Write(sample.Kind);
                writer.Write(sample.Icon);
                if (sample.Kind != FloatingEffect)
                {
                    writer.Write(sample.Visible); writer.Write(sample.Backdrop);
                    writer.Write(sample.Mode); writer.Write(sample.Text);
                }
                return stream.ToArray();
            }
        }

        internal static void Apply(byte[] packet)
        {
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                reader.ReadInt32();
                int id = reader.ReadInt32(), sequence = reader.ReadInt32();
                byte kind = reader.ReadByte();
                int icon = reader.ReadInt32();
                if (id <= 0 || sequence <= 0 || kind > FloatingEffect) throw new InvalidDataException("Invalid duplicant feedback");
                var sample = new Sample { Serial = sequence, Kind = kind, Icon = icon, Time = Time.unscaledTime };
                if (kind != FloatingEffect)
                {
                    sample.Visible = reader.ReadBoolean(); sample.Backdrop = reader.ReadInt32();
                    sample.Mode = reader.ReadInt32(); sample.Text = reader.ReadString();
                    if (Encoding.UTF8.GetByteCount(sample.Text) > 800) throw new InvalidDataException("Oversized duplicant feedback");
                }
                if (reader.BaseStream.Position != packet.Length) throw new InvalidDataException("Trailing duplicant feedback data");
                var target = NativeWorldMirror.GetDupeSelectable(id);
                if (target == null) return;
                sample.Target = target.gameObject;
                if (kind == FloatingEffect)
                {
                    var fx = PopFXManager.Instance;
                    var effect = Db.Get().effects.TryGet(new HashedString(icon));
                    if (fx == null || effect == null || seenEffects.ContainsKey(sequence)) return;
                    if (fx.SpawnFX(fx.sprite_Plus, effect.Name, target.transform) != null)
                        seenEffects.Add(sequence, sample.Time);
                    return;
                }
                var key = (id, kind);
                if (displayed.TryGetValue(key, out var previous))
                {
                    if (sequence < previous.Serial) return;
                    if (sequence == previous.Serial && previous.Target == sample.Target)
                    {
                        previous.Time = sample.Time;
                        return;
                    }
                    if (previous.Target != sample.Target) Show(previous, false);
                }
                if (Show(sample, sample.Visible)) displayed[key] = sample;
            }
        }

        static bool Show(Sample sample, bool visible)
        {
            var screen = NameDisplayScreen.Instance;
            if (sample.Target == null || screen == null) return false;
            var entry = entryFor(screen, sample.Target);
            if (visible && entry == null)
            {
                sample.Target.GetComponent<CharacterOverlay>()?.Register();
                entry = entryFor(screen, sample.Target);
            }
            if (entry == null) return false;
            if (visible && entry.thoughtBubble == null)
            {
                entry.thoughtBubble = CreateBubble(EffectPrefabs.Instance.ThoughtBubble, entry.display_go, "Thought Bubble");
                entry.thoughtBubbleConvo = CreateBubble(EffectPrefabs.Instance.ThoughtBubbleConvo, entry.display_go, "Thought Bubble Convo");
            }
            if (entry.thoughtBubble == null) return !visible;
            var backdrop = visible ? Assets.GetSprite(new HashedString(sample.Backdrop)) : null;
            var icon = visible ? Assets.GetSprite(new HashedString(sample.Icon)) : null;
            if (sample.Kind == ConversationBubble)
            {
                var mode = visible ? Assets.GetSprite(new HashedString(sample.Mode)) : null;
                screen.SetThoughtBubbleConvoDisplay(sample.Target, visible, sample.Text, backdrop, icon, mode);
            }
            else screen.SetThoughtBubbleDisplay(sample.Target, visible, sample.Text, backdrop, icon);
            return true;
        }

        static HierarchyReferences CreateBubble(GameObject prefab, GameObject parent, string name)
        {
            var obj = Util.KInstantiateUI(prefab, parent);
            obj.name = name;
            obj.SetActive(false);
            return obj.GetComponent<HierarchyReferences>();
        }

        internal static void Expire(float now)
        {
            expiredBubbles.Clear();
            foreach (var row in displayed)
            {
                if (now - row.Value.Time > BubbleExpiry || row.Value.Target == null) expiredBubbles.Add(row.Key);
            }
            foreach (var key in expiredBubbles)
            {
                Show(displayed[key], false);
                displayed.Remove(key);
            }
            expiredEffects.Clear();
            foreach (var row in seenEffects)
                if (now - row.Value > 3f) expiredEffects.Add(row.Key);
            foreach (int key in expiredEffects) seenEffects.Remove(key);
        }

        internal static void Reset()
        {
            foreach (var sample in displayed.Values) Show(sample, false);
            displayed.Clear(); seenEffects.Clear(); bubbles.Clear(); effects.Clear(); serial = 0;
        }
    }
}
