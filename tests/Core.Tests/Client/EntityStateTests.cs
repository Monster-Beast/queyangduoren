using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using Xunit;

namespace Queyang.Core.Tests
{
    public class EntityStateTests
    {
        [Fact]
        public void DensePageRetainsStoredFoodIdentityFreshnessAndRecipeAmounts()
        {
            var entries = Enumerable.Range(800, EntityStateCodec.MaxCount).Select(id => new ColonyEntity {
                Id = id, Prefab = -251, Kind = ColonyEntityKind.Food, Cell = 383, Place = ColonyItemPlace.Stored,
                OwnerId = 97, Available = 1, Reserved = .5f, Mass = 1.5f, Temperature = 282.9f, Value = 2380, Limit = .67f,
                IngredientTypes = 17, Contents = Enumerable.Range(0, EntityStateCodec.MaxIngredients)
                    .Select(i => new ColonyIngredient {Tag = -i, Stored = 13 + i, Required = 21, Freshness = .6f}).ToArray()
            }).ToArray();
            var page = new ColonyEntityPage { From = 766, Through = 1240, Entities = entries };
            var bytes = EntityStateCodec.Encode(page);
            Assert.InRange(bytes.Length, 7000, ColonyView.MaxEntityPayload);
            var copy = EntityStateCodec.Decode(bytes);
            Assert.Equal(766, copy.From); Assert.Equal(1240, copy.Through);
            Assert.Equal((ColonyItemPlace.Stored, 97, -251, .5f), (copy.Entities[0].Place, copy.Entities[0].OwnerId,
                copy.Entities[0].Prefab, copy.Entities[0].Reserved));
            Assert.Equal((2380f, .67f, 282.9f), (copy.Entities[0].Value, copy.Entities[0].Limit, copy.Entities[0].Temperature));
            var row = copy.Entities[3].Contents[5];
            Assert.Equal((-5, 18f, 21f, .6f), (row.Tag, row.Stored, row.Required, row.Freshness));
            Assert.Throws<EndOfStreamException>(() => EntityStateCodec.Decode(bytes.Take(bytes.Length - 1).ToArray()));
            Assert.Throws<InvalidDataException>(() => EntityStateCodec.Decode(bytes.Concat(new byte[1]).ToArray()));
        }

        [Fact]
        public void MovingAnItemPreservesIdentityAndRejectsInvalidOwnersAndDuplicates()
        {
            var item = new ColonyEntity { Kind = ColonyEntityKind.Item, Id = 21, Cell = 18, Mass = 100 };
            var page = new ColonyEntityPage { Entities = new[] { item } };
            Assert.Equal(-1, EntityStateCodec.Decode(EntityStateCodec.Encode(page)).Entities[0].OwnerId);
            item.Place = ColonyItemPlace.Carried;
            Assert.Throws<InvalidDataException>(() => EntityStateCodec.Encode(page));
            item.OwnerId = 12;
            Assert.Equal(21, EntityStateCodec.Decode(EntityStateCodec.Encode(page)).Entities[0].Id);
            item.Place = ColonyItemPlace.Stored; item.OwnerId = 334;
            Assert.Equal(334, EntityStateCodec.Decode(EntityStateCodec.Encode(page)).Entities[0].OwnerId);
            page.Entities = new[] { item, item };
            Assert.Throws<InvalidDataException>(() => EntityStateCodec.Encode(page));
            page.Entities = new[] { item }; item.Mass = float.PositiveInfinity;
            Assert.Throws<InvalidDataException>(() => EntityStateCodec.Encode(page));
        }
    }
}
