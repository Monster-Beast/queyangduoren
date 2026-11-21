using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using Xunit;

namespace Queyang.Core.Tests
{
    public sealed class ExtendedViewportTests
    {
        static ColonyView View() => new ColonyView { Native = true, Detailed = true, HasResources = true };

        [Fact]
        public void EnvironmentAndOpaqueEntityPageRoundTripIndependently()
        {
            var v = View();
            Assert.Equal(7, BitConverter.ToUInt16(v.Encode()));
            Assert.False(ColonyView.Decode(v.Encode()).CoversEnvironment(0));
            v.EntityPayload = new byte[] { 18, 0, 255 };
            var entities = ColonyView.Decode(v.Encode());
            Assert.Equal(8, BitConverter.ToUInt16(v.Encode()));
            Assert.Equal(v.EntityPayload, entities.EntityPayload);
            Assert.False(entities.HasEnvironment);
            v.EntityPayload = null;
            v.HasEnvironment = true;
            v.Cells[29] = new ColonyCell { Flags = 1, Utilities = 6, BuildingHash = -19,
                Germs = int.MaxValue, GermType = 2, Lux = 1780, Decor = -34.875f,
                RoomType = -222, RoomAnchor = 43, RoomSize = 117,
                RailPresent = true, RailConnections = 11, RailItem = -110238, RailMass = .032f,
                GasPipe = new MaterialSample { Germs = 1500, GermType = 4 },
                LiquidPipe = new MaterialSample { Germs = 800, GermType = 3 },
                Structure = new MaterialSample { Germs = 33, GermType = 1 } };
            v.Piles = new[] { new ColonyPile { SaveId = 12, Cell = 29, Material = new MaterialSample { Germs = 34, GermType = 5 } } };
            var copy = ColonyView.Decode(v.Encode());
            Assert.Null(copy.EntityPayload);
            Assert.True(copy.CoversEnvironment(29)); Assert.False(copy.CoversEnvironment(0));
            var c = copy.Cells[29];
            Assert.Equal((int.MaxValue, (byte)2, 1780, -34.875f), (c.Germs,c.GermType,c.Lux,c.Decor));
            Assert.Equal((-222,43,117), (c.RoomType,c.RoomAnchor,c.RoomSize));
            Assert.Equal((true, (byte)11, -110238, .032f), (c.RailPresent,c.RailConnections,c.RailItem,c.RailMass));
            Assert.Equal((1500,800,33,34), (c.GasPipe.Germs,c.LiquidPipe.Germs,c.Structure.Germs,copy.Piles[0].Material.Germs));
            Assert.Equal(((byte)4,(byte)3,(byte)1,(byte)5),(c.GasPipe.GermType,c.LiquidPipe.GermType,c.Structure.GermType,copy.Piles[0].Material.GermType));
        }

        [Fact]
        public void RoomMembershipMustHaveValidDimensionsAndCanBeCleared()
        {
            var v=View(); v.HasEnvironment=true;
            v.Cells[0]=new ColonyCell { Flags=1, RoomType=-8, RoomSize=8, RoomAnchor=-1 };
            Assert.Throws<InvalidDataException>(()=>v.Encode());
            v.Cells[0].RoomAnchor=22; v.Cells[0].RoomSize=0;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
            v.Cells[0].RoomType=0;
            var copy=ColonyView.Decode(v.Encode());
            Assert.True(copy.CoversEnvironment(0));
            Assert.Equal((0,0,0),(copy.Cells[0].RoomType,copy.Cells[0].RoomAnchor,copy.Cells[0].RoomSize));
        }

        [Fact]
        public void EntityLengthAndCompressedBoundsRejectCorruption()
        {
            var v = View(); v.EntityPayload = new byte[ColonyView.MaxEntityPayload];
            byte[] encoded = v.Encode();
            Assert.Equal(v.EntityPayload,ColonyView.Decode(encoded).EntityPayload);
            Assert.Throws<EndOfStreamException>(() => ColonyView.Decode(encoded[..^1]));
            v.EntityPayload = new byte[ColonyView.MaxEntityPayload+1];
            Assert.Throws<InvalidDataException>(() => v.Encode());
            byte[] bomb = {8,0,128,255,255,255,127,1,0,0,0,0};
            Assert.Throws<InvalidDataException>(() => ColonyView.Decode(bomb));
            encoded[2] = 64;
            Assert.Throws<InvalidDataException>(() => ColonyView.Decode(encoded));
            v.EntityPayload = null; v.HasEnvironment = true;
            v.Cells[0] = new ColonyCell { Flags = 1, Lux = -1 };
            Assert.Throws<InvalidDataException>(() => v.Encode());
        }

        internal static ColonyView DenseWorld()
        {
            var r = new Random(75214);
            string Name() { var bytes = new byte[48]; r.NextBytes(bytes); return Convert.ToBase64String(bytes); }
            float Value() => BitConverter.Int32BitsToSingle(r.Next(0x3a000000, 0x45000000));
            MaterialSample Material() => new MaterialSample { Germs = r.Next(), GermType = (byte)r.Next(256),
                Element = r.Next(), Mass = Value(), Temperature = Value() };
            var v = View(); v.HasEnvironment = true;
            v.EntityPayload = new byte[ColonyView.MaxEntityPayload]; r.NextBytes(v.EntityPayload);
            for (int i=0; i<v.Cells.Length; ++i)
                v.Cells[i] = new ColonyCell { Flags = 17, Utilities = 15, Element = Name(), Building = Name(), BuildingHash = r.Next(),
                    ElementHash = r.Next(), TargetId = r.Next(), BuildingSaveId = r.Next(), BuildingAnimation = r.Next(),
                    R = (byte)r.Next(256), G = (byte)r.Next(256), B = (byte)r.Next(256), Connections = (ushort)r.Next(65536),
                    Mass=Value(), Temperature=Value(), WattsUsed=Value(), WattsSafe=Value(), WattsGenerated=Value(), WattsNeeded=Value(),
                    LogicValue=r.Next(), GasPipe=Material(), LiquidPipe=Material(), Structure=Material(), Lux=r.Next(), Decor=Value(),
                    RoomType = -r.Next(1,int.MaxValue), RoomAnchor = r.Next(), RoomSize = r.Next(1,int.MaxValue),
                    OrderPriority=(byte)r.Next(1,10), MopOrdered=r.Next(2)==0,
                    Germs=r.Next(), GermType=(byte)r.Next(256), RailPresent=true, RailItem=r.Next(), RailConnections=15, RailMass=Value() };
            v.Dupes = Enumerable.Range(1,64).Select(i=>new ColonyDupe { Id=i, SaveId=i, Name=Name(), Activity=Name() }).ToArray();
            v.Piles = Enumerable.Range(0,ColonyView.MaxPiles).Select(i=>new ColonyPile { SaveId=i, Cell=(ushort)i, Material=Material(), Animation=r.Next() }).ToArray();
            v.Stocks = Enumerable.Range(0,ColonyView.MaxStocks).Select(i=>new ColonyStock { Tag=i, Total=Value(), Reserved=Value() }).ToArray();
            return v;
        }

        [Fact]
        public void FullEntropyWorldKeepsEntityPageAndVisitsEachEnvironmentCellWithinFourPages()
        {
            var v = DenseWorld();
            var covered = new bool[v.Cells.Length];
            for (int page=0; page<4; page++)
            {
                v.EnvironmentPage = page;
                byte[] payload = v.Encode(); Assert.InRange(payload.Length,1,65520);
                var copy = ColonyView.Decode(payload);
                Assert.Equal(v.EntityPayload,copy.EntityPayload);
                int count=0;
                for (int i=0; i<covered.Length; i++)
                {
                    Assert.Equal((v.Cells[i].OrderPriority,v.Cells[i].MopOrdered),(copy.Cells[i].OrderPriority,copy.Cells[i].MopOrdered));
                    if (copy.CoversEnvironment(i))
                    {
                        covered[i]=true; count++;
                        Assert.Equal(v.Cells[i].RailItem,copy.Cells[i].RailItem);
                        Assert.Equal(v.Cells[i].RoomAnchor,copy.Cells[i].RoomAnchor);
                        Assert.Equal(v.Cells[i].LiquidPipe.Germs,copy.Cells[i].LiquidPipe.Germs);
                    }
                }
                Assert.Equal(v.Cells.Length / 4,count);
            }
            Assert.All(covered,b=>Assert.True(b));
        }
    }
}
