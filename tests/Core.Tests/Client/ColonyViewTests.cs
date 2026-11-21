using System;
using System.IO;
using System.Linq;
using System.Text;
using Queyang.Game;
using Xunit;

namespace Queyang.Core.Tests
{
    public class ColonyViewTests
    {
        [Fact]
        public void EmptyViewportHasStableV4Encoding()
        {
            byte[] bytes = new ColonyView { X = 256, Y = 12 }.Encode();
            Assert.Equal(new byte[] { 4,0, 0,1,0,0, 12,0,0,0, 0,0,0,0, 0,0,0,0, 0,0, 1,0, 0 }, bytes.Take(23));
            Assert.Equal(23 + 384 * 25 + 1, bytes.Length);
            Assert.All(bytes.Skip(23), value => Assert.Equal(0, value));
        }

        [Fact]
        public void NativeWorldIdentifiersAnimationAndGroundStateRoundTrip()
        {
            var view = new ColonyView { Native = true, Dupes = new[] { new ColonyDupe {
                Id = 2, SaveId = 318, Animation = -983, AnimationPosition = 154, FlipX = true } } };
            view.Cells[5] = new ColonyCell { Flags = 1, ElementHash = -110, BuildingHash = 845,
                BuildingAnimation = -28, BuildingFrame = 240, AnchorDX = -2, AnchorDY = 3,
                Visibility = 216, RenderGround = true };
            byte[] bytes = view.Encode();
            Assert.Equal(ColonyView.NativeVersion, BitConverter.ToUInt16(bytes, 0));
            var decoded = ColonyView.Decode(bytes);
            Assert.True(decoded.Native);
            var cell = decoded.Cells[5];
            Assert.Equal((-110, 845, -2, 3, 216, true),
                (cell.ElementHash, cell.BuildingHash, (int)cell.AnchorDX, (int)cell.AnchorDY, (int)cell.Visibility, cell.RenderGround));
            Assert.Equal((-28, 240), (cell.BuildingAnimation, (int)cell.BuildingFrame));
            var d = decoded.Dupes[0];
            Assert.Equal((318, -983, 154, true), (d.SaveId, d.Animation, (int)d.AnimationPosition, d.FlipX));
            bytes[bytes.Length - 1] = 2;
            Assert.Throws<InvalidDataException>(() => ColonyView.Decode(bytes));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DetailedViewCarriesPhysicalReadingsAndFitsWorstCase(bool resources)
        {
            var v = new ColonyView { Native = true, Detailed = true, HasResources = resources };
            var name = string.Concat(Enumerable.Repeat("测试元素", 30));
            for (int i = 0; i < v.Cells.Length; i++)
                v.Cells[i] = new ColonyCell { Flags = 1, Utilities = 15, Element = "e" + i + name,
                    Building = "b" + i + name, BuildingHash = i + 1, BuildingSaveId = 1113, WattsSafe = 1000, WattsUsed = 150,
                    WattsGenerated = 250, WattsNeeded = 470, LogicValue = 14,
                    GasPipe = new MaterialSample { Element = -24, Mass = .2f, Temperature = 334 },
                    LiquidPipe = new MaterialSample { Element = 16, Mass = 8.5f, Temperature = 290 },
                    Structure = new MaterialSample { Element = 8, Mass = 142, Temperature = 315.5f } };
            v.Dupes = Enumerable.Range(0, 64).Select(i => new ColonyDupe { Id = i+1, Name = "d" + i + name,
                Activity = "a" + i + name, Personality = -120 }).ToArray();
            if (resources)
            {
                v.Piles = Enumerable.Range(0, ColonyView.MaxPiles).Select(i => new ColonyPile { SaveId = i,
                    Cell = (ushort)i, SubX = 53, SubY = 23, Animation = -48,
                    Material = new MaterialSample { Element = 855, Mass = 172, Temperature = 314 } }).ToArray();
                v.PileCoverage[0] = 5;
                v.CompleteStock = true;
                v.Stocks = Enumerable.Range(0, ColonyView.MaxStocks).Select(i => new ColonyStock { Tag = i,
                    Total = 1600, Reserved = 80 }).ToArray();
            }
            byte[] bytes = v.Encode();
            Assert.InRange(bytes.Length, 50000, 65520);
            var copy = ColonyView.Decode(bytes);
            Assert.True(copy.Detailed);
            var c = copy.Cells[113];
            Assert.Equal((150f, 250f, 1000f, 470f, 14), (c.WattsUsed, c.WattsGenerated, c.WattsSafe, c.WattsNeeded, c.LogicValue));
            Assert.Equal((-24, .2f, 334f), (c.GasPipe.Element, c.GasPipe.Mass, c.GasPipe.Temperature));
            Assert.Equal((16, 8.5f, 290f), (c.LiquidPipe.Element, c.LiquidPipe.Mass, c.LiquidPipe.Temperature));
            Assert.Equal((8, 142f, 315.5f), (c.Structure.Element, c.Structure.Mass, c.Structure.Temperature));
            Assert.Equal(-120, copy.Dupes[63].Personality);
            if (resources)
            {
                var pile = copy.Piles[127];
                Assert.Equal((127, 127, 855, 172f, 314f), (pile.SaveId, (int)pile.Cell, pile.Material.Element,
                    pile.Material.Mass, pile.Material.Temperature));
                Assert.Equal(1113, c.BuildingSaveId);
                Assert.True(copy.CoversPiles(2)); Assert.False(copy.CoversPiles(1));
                Assert.True(copy.CompleteStock);
                Assert.Equal((383, 1600f, 80f), (copy.Stocks[383].Tag, copy.Stocks[383].Total, copy.Stocks[383].Reserved));
                v.PileThrough = 80;
                Assert.Throws<InvalidDataException>(() => ColonyView.Decode(v.Encode()));
                v.PileThrough = int.MaxValue;
            }
            Assert.Throws<EndOfStreamException>(() => ColonyView.Decode(bytes.Take(bytes.Length - 1).ToArray()));
            var invalid = v.Cells[0]; invalid.LiquidPipe.Temperature = float.NaN; v.Cells[0] = invalid;
            Assert.Throws<InvalidDataException>(() => ColonyView.Decode(v.Encode()));
        }

        [Fact]
        public void TwoViewsDecodeIndependentlyWithExactTargetAndCellCoordinates()
        {
            var v = new ColonyView { X = 113, Y = 40, Cycle = 17, CycleFraction = .5f, Paused = true, Speed = 3,
                Dupes = new[] { new ColonyDupe { Id = 11, X = 115,
                Y = 42, SubX = 120, SubY = 4, Name = "小美", Activity = "挖掘" } } };
            int i = ColonyView.Width * 2 + 2;
            v.Cells[i] = new ColonyCell { Element = "氧气", Mass = 1.625f, Temperature = 290.5f,
                TargetId = 312, Flags = 1 | 64 | 16, Building = "梯子", Utilities = 13, Connections = 0x9050,
                R = 32, G = 64, B = 96, BuildingStyle = 0x51, Progress = 40 };
            byte[] snapshot = v.Encode();
            var first = ColonyView.Decode(snapshot);
            v.Cells[i].TargetId = 320;
            var second = ColonyView.Decode(v.Encode());
            Assert.Equal(312, first.Cells[i].TargetId);
            Assert.Equal(320, second.Cells[i].TargetId);
            Assert.Equal((17,.5f,true,3), (first.Cycle, first.CycleFraction, first.Paused, (int)first.Speed));
            Assert.Equal((32,64,96,0x51,40), ((int)first.Cells[i].R, (int)first.Cells[i].G, (int)first.Cells[i].B,
                (int)first.Cells[i].BuildingStyle, (int)first.Cells[i].Progress));
            Assert.Equal((13, 0x9050), ((int)first.Cells[i].Utilities, (int)first.Cells[i].Connections));
            Assert.Equal((81, "梯子", "氧气", 1.625f, 290.5f), ((int)first.Cells[i].Flags, first.Cells[i].Building,
                first.Cells[i].Element, first.Cells[i].Mass, first.Cells[i].Temperature));
            Assert.Equal((115, 42, "小美"), (first.Dupes[0].X, first.Dupes[0].Y, first.Dupes[0].Name));
            Assert.Equal("挖掘", first.Dupes[0].Activity);
            Assert.Equal((11,120,4), (first.Dupes[0].Id, (int)first.Dupes[0].SubX, (int)first.Dupes[0].SubY));
            Assert.Equal(snapshot, first.Encode());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MaximumUniqueLongNamesFitSingleFrameWithoutSplittingUnicode(bool native)
        {
            var v = new ColonyView { Native = native };
            string suffix = string.Concat(Enumerable.Repeat("🌍菌", 40));
            for (int i = 0; i < v.Cells.Length; i++)
                v.Cells[i] = new ColonyCell { Flags = 1, Element = "E" + i + suffix, Building = "B" + i + suffix };
            v.Dupes = Enumerable.Range(0, 64).Select(i => new ColonyDupe { Id = i + 1, Name = "D" + i + suffix, Activity = "A" + i + suffix }).ToArray();
            var payload = v.Encode();
            Assert.InRange(payload.Length, 50000, 65520);
            var decoded = ColonyView.Decode(payload);
            Assert.All(decoded.Cells, c => {
                Assert.DoesNotContain("\uFFFD", c.Element);
                Assert.InRange(Encoding.UTF8.GetByteCount(c.Building), native ? 47 : 55, native ? 50 : 58);
            });
            Assert.Equal(64, decoded.Dupes.Length);
        }

        [Fact]
        public void VisualMotionInterpolatesWithinBoundsAndImmediatelyForgetsRemovedDupes()
        {
            var motion = new ColonyMotion();
            var view = new ColonyView { Dupes = new[] { new ColonyDupe { Id = 4, X = 2, Y = 3 } } };
            for (int i = 0; i < view.Cells.Length; i++) view.Cells[i].Flags = 1;
            motion.Apply(view, 1);
            view.Dupes[0].X = 3;
            motion.Apply(view, 1.25f);
            Assert.True(motion.TryGet(4, 1.375f, out float x, out float y));
            Assert.Equal((2.5f, 3f), (x, y));
            view.Dupes[0].X = 20;
            motion.Apply(view, 1.5f);
            Assert.True(motion.TryGet(4, 1.5f, out x, out y));
            Assert.Equal(20f, x);
            view.Cells[3 * ColonyView.Width + 20].Flags = 0;
            motion.Apply(view, 1.75f);
            Assert.False(motion.TryGet(4, 1.75f, out _, out _));
        }

        [Fact]
        public void RejectsUnknownFlagsNameReferencesTrailingDataAndTruncation()
        {
            byte[] bytes = new ColonyView().Encode();
            bytes[23] = 128;
            Assert.Throws<InvalidDataException>(() => ColonyView.Decode(bytes));
            bytes[23] = 0; bytes[36] = 1;
            Assert.Throws<InvalidDataException>(() => ColonyView.Decode(bytes));
            bytes[36] = 0;
            Assert.Throws<InvalidDataException>(() => ColonyView.Decode(bytes.Concat(new byte[1]).ToArray()));
            Assert.Throws<EndOfStreamException>(() => ColonyView.Decode(bytes.Take(bytes.Length - 1).ToArray()));
        }
    }
}
