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
        public void ThreeCrowdedDuplicantPagesRetainOthersAndRemoveMissingStableIdentities()
        {
            var world=View(); world.HasEnvironment=true; world.Cells[0].Flags=1;
            var visible=Enumerable.Range(1,150).Reverse().Select(n=>new ColonyDupe { Id=n, SaveId=n*3, Name="d"+n }).ToList();
            ColonyDupe[] displayed=null;
            ColonyView Sample(int page)
            {
                world.SetDupePage(visible,page);
                var v=ColonyView.Decode(world.Encode());
                Assert.All(v.Dupes,d=>Assert.True(v.CoversDupeId(d.SaveId)));
                Assert.InRange(v.Dupes.Length,1,64);
                displayed=v.DisplayDupes(displayed);
                return v;
            }
            Sample(0); Sample(1); Sample(2);
            Assert.Equal(150,displayed.Length);
            int gone=visible[80].SaveId; visible.RemoveAt(80);
            var first=Sample(0); Assert.False(first.CoversDupeId(gone));
            Assert.Contains(displayed,d=>d.SaveId==gone);
            var second=Sample(1); Assert.True(second.CoversDupeId(gone));
            Assert.DoesNotContain(displayed,d=>d.SaveId==gone);
            Sample(2);
            Assert.Equal(visible.Select(d=>d.SaveId).OrderBy(i=>i),displayed.Select(d=>d.SaveId).OrderBy(i=>i));
            // After a crowd disperses, the default full range retires all absent projected identities.
            visible.RemoveRange(5,visible.Count-5); world.HasEnvironment=false;
            var full=Sample(3); Assert.Equal(7,BitConverter.ToUInt16(world.Encode()));
            Assert.Equal(5,displayed.Length); Assert.True(full.CoversDupeId(gone));
        }

        [Fact]
        public void OverlappingBuildingAndOrderIdentitiesHaveIndependentPriorityBounds()
        {
            var v=View(); v.HasEnvironment=true;
            v.Cells[8]=new ColonyCell { Flags=1, TargetId=80, OrderPriority=7, MopOrdered=true, OrderTargetId=19, SeparateOrderPriority=4 };
            var raw=v.Encode();
            var cell=ColonyView.Decode(raw).Cells[8];
            Assert.Equal((80,(byte)7,19,(byte)4,true),(cell.TargetId,cell.OrderPriority,cell.OrderTargetId,cell.SeparateOrderPriority,cell.MopOrdered));
            raw[^1]=10;
            Assert.Throws<InvalidDataException>(()=>ColonyView.Decode(raw));
            raw[^1]=4; Array.Clear(raw,raw.Length-5,4);
            Assert.Throws<InvalidDataException>(()=>ColonyView.Decode(raw));
            v.Cells[8].OrderTargetId=0;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
        }

        [Fact]
        public void OverlappingDigAndBlueprintKeepTheirOwnMaterialAndTint()
        {
            var v=View(); v.HasEnvironment=true;
            v.Cells[8]=new ColonyCell { Flags=1|8|32, DigMaterial=3, DigTint=0xc0ff0854,
                HasBlueprintTint=true, BlueprintTint=0x30ab2900 };
            var c=ColonyView.Decode(v.Encode()).Cells[8];
            Assert.Equal(((byte)3,0xc0ff0854u,true,0x30ab2900u),(c.DigMaterial,c.DigTint,c.HasBlueprintTint,c.BlueprintTint));
            v.Cells[8].DigMaterial=0; v.Cells[8].HasBlueprintTint=false;
            c=ColonyView.Decode(v.Encode()).Cells[8];
            Assert.False(c.HasBlueprintTint);
            Assert.Equal(0,c.DigMaterial);
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
                    OrderTargetId=r.Next(1,int.MaxValue), SeparateOrderPriority=(byte)r.Next(10),
                    Germs=r.Next(), GermType=(byte)r.Next(256), RailPresent=true, RailItem=r.Next(), RailConnections=15, RailMass=Value() };
            v.DupePoseTick=110;
            v.Dupes = Enumerable.Range(1,64).Select(i=>new ColonyDupe { Id=i, SaveId=i, Name=Name(), Activity=Name(),
                Pose=new ColonyDupePose { X=(float)r.NextDouble(),Y=(float)r.NextDouble(),M00=Value(),M01=Value(),M02=Value(),
                    M10=Value(),M11=Value(),M12=Value(),File=r.Next(),Frame=r.Next(65536) } }).ToArray();
            v.Piles = Enumerable.Range(0,ColonyView.MaxPiles).Select(i=>new ColonyPile { SaveId=i, Cell=(ushort)i, Material=Material(), Animation=r.Next() }).ToArray();
            v.Stocks = Enumerable.Range(0,ColonyView.MaxStocks).Select(i=>new ColonyStock { Tag=i, Total=Value(), Reserved=Value() }).ToArray();
            return v;
        }

        [Fact]
        public void NativePosePreservesRenderMatrixAndGridBoundary()
        {
            var v=View();
            v.DupePoseTick=1234;
            v.Cells[0].Flags=1;
            var pose=new ColonyDupePose { X=.71f,Y=-.049f,M00=-1,M11=1,M02=.49f,M12=.25f,Frame=5,File=912 };
            v.Dupes=new[]{new ColonyDupe { Name="Test",Id=7,SaveId=32,Pose=pose }};
            var copy=ColonyView.Decode(v.Encode());
            Assert.Equal(v.DupePoseTick,copy.DupePoseTick);
            Assert.Equal(pose,copy.Dupes[0].Pose);
            v.Dupes[0].Pose.M02=float.NaN;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
            v.DupePoseTick=0;
            Assert.Equal(0,ColonyView.Decode(v.Encode()).DupePoseTick);
        }

        [Fact]
        public void NativeAppearanceAndColonyHudRoundTripWithoutChangingLegacyViews()
        {
            var view=View();
            view.HasDupeAppearance=true;
            view.Dupes=new[]{new ColonyDupe { Id=1,SaveId=2,Hat=42,Hair=57,HatHair=63,HeadVisibility=5 }};
            view.Cells[0].Flags=1;
            view.HasColonyHud=true; view.HudWorld=0; view.HudLocalPopulation=4; view.HudTotalPopulation=5;
            view.HudCalories=18571428f; view.HudStress=3.5f;
            var restored=ColonyView.Decode(view.Encode());
            Assert.True(restored.HasColonyHud);
            Assert.Equal(view.HudTotalPopulation,restored.HudTotalPopulation);
            Assert.Equal(view.HudCalories,restored.HudCalories);
            Assert.Equal((42,57,63,(byte)5),(restored.Dupes[0].Hat,restored.Dupes[0].Hair,restored.Dupes[0].HatHair,restored.Dupes[0].HeadVisibility));
            view.HudTotalPopulation=2;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
            view.HasColonyHud=false;
            Assert.False(ColonyView.Decode(view.Encode()).HasColonyHud);
            view.Dupes[0].HeadVisibility=8;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
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
                    Assert.Equal((v.Cells[i].OrderTargetId,v.Cells[i].SeparateOrderPriority),(copy.Cells[i].OrderTargetId,copy.Cells[i].SeparateOrderPriority));
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
