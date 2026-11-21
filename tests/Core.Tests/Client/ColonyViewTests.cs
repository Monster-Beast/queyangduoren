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

        [Fact]
        public void MaximumUniqueLongNamesFitSingleFrameWithoutSplittingUnicode()
        {
            var v = new ColonyView();
            string suffix = string.Concat(Enumerable.Repeat("🌍菌", 40));
            for (int i = 0; i < v.Cells.Length; i++)
                v.Cells[i] = new ColonyCell { Flags = 1, Element = "E" + i + suffix, Building = "B" + i + suffix };
            v.Dupes = Enumerable.Range(0, 64).Select(i => new ColonyDupe { Id = i + 1, Name = "D" + i + suffix, Activity = "A" + i + suffix }).ToArray();
            var payload = v.Encode();
            Assert.InRange(payload.Length, 50000, 65520);
            var decoded = ColonyView.Decode(payload);
            Assert.All(decoded.Cells, c => {
                Assert.DoesNotContain("\uFFFD", c.Element);
                Assert.InRange(Encoding.UTF8.GetByteCount(c.Building), 55, 58);
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
