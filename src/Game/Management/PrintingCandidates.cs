using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Database;
using HarmonyLib;
using Klei.AI;
using Packet = Queyang.Core.Wire;

namespace Queyang.Game
{
    internal static class PrintingCandidates
    {
        static readonly MethodInfo Clone = AccessTools.Method(typeof(object), "MemberwiseClone");

        internal static string Write(IReadOnlyList<ITelepadDeliverable> cards)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)cards.Count);
                foreach (var card in cards)
                {
                    if (card is MinionStartingStats dupe)
                    {
                        writer.Write(true);
                        writer.Write(dupe.personality.IdHash.HashValue);
                        Packet.Text(writer, dupe.Name ?? "", 512);
                        Packet.Text(writer, dupe.overrideOutfitID ?? "", 128);
                        writer.Write(dupe.voiceIdx);
                        writer.Write(dupe.joyTrait.IdHash.HashValue);
                        writer.Write(dupe.stressTrait.IdHash.HashValue);
                        writer.Write(dupe.congenitaltrait?.IdHash.HashValue ?? 0);
                        writer.Write((byte)dupe.Traits.Count);
                        foreach (var trait in dupe.Traits) writer.Write(trait.IdHash.HashValue);
                        writer.Write((byte)dupe.StartingLevels.Count);
                        foreach (var level in dupe.StartingLevels)
                        {
                            Packet.Text(writer, level.Key, 64);
                            writer.Write(level.Value);
                        }
                        writer.Write((byte)dupe.skillAptitudes.Count);
                        foreach (var interest in dupe.skillAptitudes)
                        {
                            writer.Write(interest.Key.IdHash.HashValue);
                            writer.Write(interest.Value);
                        }
                    }
                    else
                    {
                        var package = (CarePackageContainer.CarePackageInstanceData)card;
                        writer.Write(false);
                        Packet.Text(writer, package.info.id, 128);
                        writer.Write(package.info.quantity);
                        Packet.Text(writer, package.facadeID ?? "", 128);
                    }
                }
                return Convert.ToBase64String(stream.ToArray()).Replace('/', '_');
            }
        }

        internal static ITelepadDeliverable[] Read(string encoded)
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(encoded.Replace('_', '/'))))
            using (var reader = new BinaryReader(stream))
            {
                int count = reader.ReadByte();
                if (count < 1 || count > 4) throw new InvalidDataException("Invalid printing batch size");
                var cards = new ITelepadDeliverable[count];
                for (int i = 0; i < count; i++)
                {
                    if (reader.ReadBoolean()) cards[i] = ReadDupe(reader);
                    else
                    {
                        var id = Packet.Text(reader, 128);
                        var quantity = reader.ReadSingle();
                        var facade = Packet.Text(reader, 128);
                        if (Assets.GetPrefab(id) == null || float.IsNaN(quantity) || float.IsInfinity(quantity) || quantity <= 0)
                            throw new InvalidDataException("Invalid printing care package");
                        cards[i] = new CarePackageContainer.CarePackageInstanceData
                        {
                            info = new CarePackageInfo(id, quantity, null, facade),
                            facadeID = facade
                        };
                    }
                }
                Packet.End(reader);
                return cards;
            }
        }

        static MinionStartingStats ReadDupe(BinaryReader reader)
        {
            var db = Db.Get();
            var template = db.Personalities.TryGet(new HashedString(reader.ReadInt32()));
            if (template == null) throw new InvalidDataException("Unknown printing personality");
            // The regular constructor draws new traits and mutates the game's rarity deck.
            // The original candidate card only needs the host's already-generated stats.
            var dupe = (MinionStartingStats)FormatterServices.GetUninitializedObject(typeof(MinionStartingStats));
            dupe.personality = (Personality)Clone.Invoke(template, null);
            dupe.Name = Packet.Text(reader, 512);
            dupe.personality.Name = dupe.Name;
            dupe.NameStringKey = template.nameStringKey;
            dupe.GenderStringKey = template.genderStringKey;
            dupe.stickerType = template.stickerType;
            string outfit = Packet.Text(reader, 128);
            dupe.overrideOutfitID = outfit.Length == 0 ? null : outfit;
            dupe.voiceIdx = reader.ReadInt32();
            dupe.joyTrait = Trait(reader);
            dupe.stressTrait = Trait(reader);
            int congenital = reader.ReadInt32();
            dupe.congenitaltrait = congenital == 0 ? null : db.traits.TryGet(new HashedString(congenital));
            dupe.Traits = new List<Trait>();
            int traits = reader.ReadByte();
            for (int i = 0; i < traits; i++) dupe.Traits.Add(Trait(reader));
            dupe.StartingLevels = new Dictionary<string, int>();
            int levels = reader.ReadByte();
            for (int i = 0; i < levels; i++) dupe.StartingLevels.Add(Packet.Text(reader, 64), reader.ReadInt32());
            dupe.skillAptitudes = new Dictionary<SkillGroup, float>();
            int interests = reader.ReadByte();
            for (int i = 0; i < interests; i++)
            {
                var group = db.SkillGroups.TryGet(new HashedString(reader.ReadInt32()));
                float value = reader.ReadSingle();
                if (group == null || float.IsNaN(value) || float.IsInfinity(value))
                    throw new InvalidDataException("Invalid printing interest");
                dupe.skillAptitudes.Add(group, value);
            }
            dupe.accessories = new List<Accessory>();
            dupe.IsValid = true;
            return dupe;
        }

        static Trait Trait(BinaryReader reader) => Db.Get().traits.TryGet(new HashedString(reader.ReadInt32()))
            ?? throw new InvalidDataException("Unknown printing trait");
    }
}
