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
        public void NativeYellowAlertAndUrgentOrderRoundTrip()
        {
            var view=View();
            view.HasDupeAppearance=true;
            view.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            view.HostAlerts=1;
            view.Cells[0].Flags=11;
            view.Cells[0].OrderPriority=10;
            var received=ColonyView.Decode(view.Encode());
            Assert.Equal((byte)1,received.HostAlerts);
            Assert.Equal((byte)10,received.Cells[0].OrderPriority);
            view.HostAlerts=4;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
        }

        [Fact]
        public void StorageSweepFlagRoundTripsWithoutApplyingItemEquipmentSemantics()
        {
            var row=new ColonyEntity {Id=12,Kind=ColonyEntityKind.Storage,Flags=ColonyEntity.StorageSweepOnly};
            var received=Assert.Single(EntityStateCodec.Decode(EntityStateCodec.Encode(
                new ColonyEntityPage{Entities=new[]{row}})).Entities);
            Assert.Equal(ColonyEntity.StorageSweepOnly,received.Flags);
            Assert.False(received.IsWorn);
        }

        [Fact]
        public void NeighborTileCannotRemoveWorkBarFromAnotherCell()
        {
            var view=View();
            view.X=50;view.Y=20;view.WorkBars=Array.Empty<ColonyWorkBar>();
            view.Cells[0].Flags=1;
            Assert.True(view.CoversWorkBar(50,20,37));
            Assert.False(view.CoversWorkBar(49,20,37));
            Assert.False(view.CoversWorkBar(51,20,37));
            view.WorkBarFrom=38;
            Assert.False(view.CoversWorkBar(50,20,37));
        }

        [Fact]
        public void DigWorkBarIsBoundToAnAuthoritativeDigOrder()
        {
            var view=View();
            view.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            view.HasDupeAppearance=true;
            view.Cells[5].Flags=11;
            view.WorkBars=new[]{new ColonyWorkBar{Cell=5,Owner=42,Token=-7,Dig=true,AutoHide=true,Fill=.45f}};
            var row=Assert.Single(ColonyView.Decode(view.Encode()).WorkBars);
            Assert.True(row.Dig && row.AutoHide);
            Assert.Equal(.45f,row.Fill);
            view.Cells[5].Flags=3;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
        }

        [Fact]
        public void BuildingWarningPagesCoverAbsentAndPresentIconsWithoutGaps()
        {
            var view=View();
            view.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            view.HasDupeAppearance=true;
            var rows=new System.Collections.Generic.List<ColonyBuildingWarning>();
            for(ushort cell=1;cell<=29;cell++)
            {
                view.Cells[cell].Flags=17;
                view.Cells[cell].BuildingHash=7;
                view.Cells[cell].BuildingSaveId=cell+100;
                rows.Add(new ColonyBuildingWarning{Cell=cell,Y=-.35f,Icons=new[]{23,-84}});
            }
            view.SetBuildingWarnings(rows,0);
            var first=ColonyView.Decode(view.Encode());
            Assert.Equal(0,first.WarningFirst);
            Assert.Equal(24,first.WarningLast);
            Assert.Equal(24,first.BuildingWarnings.Length);
            Assert.Equal(new[]{23,-84},first.BuildingWarnings[0].Icons);
            view.SetBuildingWarnings(rows,1);
            var next=ColonyView.Decode(view.Encode());
            Assert.Equal(first.WarningLast+1,next.WarningFirst);
            Assert.Equal(view.Cells.Length-1,next.WarningLast);
            Assert.Equal(5,next.BuildingWarnings.Length);
            view.BuildingWarnings[0].Icons=new[]{3,3};
            Assert.Throws<InvalidDataException>(()=>view.Encode());
            view.SetBuildingWarnings(new System.Collections.Generic.List<ColonyBuildingWarning>(),0);
            Assert.Empty(ColonyView.Decode(view.Encode()).BuildingWarnings);
        }

        [Fact]
        public void WorkBarPagesRetainNativePresentationAndBoundRemovals()
        {
            var view=View();
            view.HasEnvironment=true;
            view.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            view.Cells[3].Flags=1;
            var bars=Enumerable.Range(1,40).Reverse().Select(i=>new ColonyWorkBar
            {
                Owner=27,Token=i%2==0?-i:i,Cell=3,Fill=i%2==0?.25f:.75f,
                X=i+.43f,Y=122.5f,Z=-6,Color=0x7F31BFFF,AutoHide=i%3==0
            }).ToList();
            Assert.Null(ColonyView.Decode(view.Encode()).WorkBars);
            view.SetWorkBars(bars,0);
            var first=ColonyView.Decode(view.Encode());
            view.SetWorkBars(bars,1);
            var last=ColonyView.Decode(view.Encode());
            Assert.Equal(32,first.WorkBars.Length);
            Assert.Equal(8,last.WorkBars.Length);
            Assert.Equal(0L,first.WorkBarFrom);
            Assert.Equal(first.WorkBarThrough+1,last.WorkBarFrom);
            Assert.Equal(long.MaxValue,last.WorkBarThrough);
            Assert.Equal(bars,first.WorkBars.Concat(last.WorkBars).ToList());
            bars.Clear();
            view.SetWorkBars(bars,2);
            var empty=ColonyView.Decode(view.Encode());
            Assert.Empty(empty.WorkBars);
            Assert.Equal((0L,long.MaxValue),(empty.WorkBarFrom,empty.WorkBarThrough));
        }

        [Fact]
        public void WorkBarPagesRejectInvalidPositionsAndReusedNativeToken()
        {
            var view=View();
            view.HasEnvironment=true;
            view.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            view.Cells[3].Flags=1;
            var bar=new ColonyWorkBar{Cell=3,Owner=27,Token=-44,Fill=.5f};
            view.WorkBars=new[]{bar,bar};
            Assert.Throws<InvalidDataException>(()=>view.Encode());
            view.Cells[4].Flags=1;
            view.WorkBars[1].Cell=4;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
            view.WorkBars=new[]{bar};
            view.WorkBars[0].X=float.NaN;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
            view.WorkBars[0]=bar;
            view.Cells[3].Flags=0;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
        }

        [Fact]
        public void BuildingMeterPageRetainsAnimationAndRejectsMissingAnchor()
        {
            var v=View();v.HasEnvironment=true;v.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            v.Cells[2]=new ColonyCell{Flags=17,BuildingHash=7,BuildingSaveId=18};
            v.MeterFirst=512;v.MeterLast=767;
            v.Meters=new[]{new ColonyMeter{Key=513,Target="meter_target",Clip="meter",Offset=1,Layer=-2,
                Position=.65f,Rotation=-11.5f,Animation=99,Visible=true,X=.125f,Hidden=new[]{"meter_frame","meter_fill"},
                TintSymbols=new[]{13},Tints=new[]{0x7F10A0FFu}}};
            var copy=ColonyView.Decode(v.Encode());
            var row=Assert.Single(copy.Meters);
            Assert.Equal(v.MeterFirst,copy.MeterFirst);Assert.Equal(v.MeterLast,copy.MeterLast);
            Assert.Equal(513,row.Key);Assert.Equal(.65f,row.Position);Assert.Equal(-11.5f,row.Rotation);
            Assert.Equal(v.Meters[0].Hidden,row.Hidden);Assert.Equal(v.Meters[0].Tints,row.Tints);Assert.True(row.Visible);
            v.Cells[2].BuildingSaveId=0;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
            v.Meters=null;Assert.Null(ColonyView.Decode(v.Encode()).Meters);
        }

        [Fact]
        public void MeterPagesCoverEveryOrdinalAndCapTheirEncodedSize()
        {
            var v=View();v.HasEnvironment=true;v.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            v.Cells[0]=new ColonyCell{Flags=17,BuildingHash=7,BuildingSaveId=18};
            var all=Enumerable.Range(0,11).Select(i=>new ColonyMeter {Key=i,Target="meter_target",Clip="meter",
                Hidden=i==8?Enumerable.Repeat(new string('h',120),32).ToArray():Array.Empty<string>()}).ToList();
            var seen=new System.Collections.Generic.List<int>();int first=0;
            for(int page=0;first<v.Cells.Length*256;page++)
            {
                Assert.InRange(page,0,10);
                v.SelectMeterPage(all,page);var copy=ColonyView.Decode(v.Encode());
                Assert.Equal(first,copy.MeterFirst);
                Assert.InRange(copy.Meters.Length,1,ColonyView.MaxMeterPageEntries);
                Assert.InRange(copy.Meters.Sum(s=>s.WireBytes())+9,1,ColonyView.MaxMeterPageBytes);
                first=copy.MeterLast+1;seen.AddRange(copy.Meters.Select(s=>s.Key));
            }
            Assert.Equal(all.Select(s=>s.Key),seen);
            v.MeterFirst=0;v.MeterLast=v.Cells.Length*256-1;
            v.Meters=new[]{all[8],all[8].Copy()};v.Meters[1].Key=9;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
        }

        [Fact]
        public void MeterCodecRejectsOverlongUtf8AndSymbolCountsOnBothSides()
        {
            var s=new ColonyMeter{Key=1,Target="target",Clip=new string('表',41)};
            Assert.Throws<InvalidDataException>(()=>s.WireBytes());
            s.Clip="meter";s.Hidden=new string[33];
            Assert.Throws<InvalidDataException>(()=>s.WireBytes());
            s.Hidden=Array.Empty<string>();s.Tints=new uint[17];s.TintSymbols=new int[17];
            Assert.Throws<InvalidDataException>(()=>s.WireBytes());
            s.Tints=Array.Empty<uint>();s.TintSymbols=Array.Empty<int>();
            using(var bytes=new MemoryStream())
            {
                var writer=new BinaryWriter(bytes);
                s.Write(writer);Assert.Equal(s.WireBytes(),bytes.Length);
                var raw=bytes.ToArray();raw[raw.Length-1]=17;
                using(var bad=new BinaryReader(new MemoryStream(raw)))
                    Assert.Throws<InvalidDataException>(()=>ColonyMeter.Read(bad));
                raw[4]=121;raw[5]=0;
                using(var bad=new BinaryReader(new MemoryStream(raw)))
                    Assert.Throws<InvalidDataException>(()=>ColonyMeter.Read(bad));
            }
        }

        [Fact]
        public void HealthBarsStayWithTheirPersonAndOlderViewsOmitThem()
        {
            var v=View();
            v.HasDupeAppearance=true;
            v.Cells[0].Flags=1;
            v.Dupes=new[]{new ColonyDupe{Id=1,SaveId=79,HealthFill=-1},new ColonyDupe{Id=2,SaveId=2358,HealthFill=150}};
            Assert.False(ColonyView.Decode(v.Encode()).HasHealthBars);
            v.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];
            v.HasHealthBars=true;
            var copy=ColonyView.Decode(v.Encode());
            Assert.True(copy.HasHealthBars);
            Assert.Equal(v.Dupes.Select(d=>d.HealthFill),copy.Dupes.Select(d=>d.HealthFill));
            v.Dupes[1].HealthFill=1001;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
        }

        [Fact]
        public void BuriedMarkerDistinguishesHiddenSolidReleasedAndOlderViewport()
        {
            var view=View();
            Assert.False(ColonyView.Decode(view.Encode()).HasEntombed);
            view.HasEntombed=true;view.Cells[17].Flags=3|ColonyView.EntombedFlag;
            var buried=ColonyView.Decode(view.Encode());
            Assert.True(buried.HasEntombed);Assert.Equal(view.Cells[17].Flags,buried.Cells[17].Flags);
            view.Cells[17].Flags=1;
            Assert.Equal(1,ColonyView.Decode(view.Encode()).Cells[17].Flags);
            view.Cells[17].Flags=ColonyView.EntombedFlag;
            Assert.Throws<InvalidDataException>(()=>view.Encode());
        }

        [Fact]
        public void NativeDebrisAndBuriedArtRoundTripWithTheViewport()
        {
            var v=View();v.HasColonyHud=v.HasEntombed=v.HasPileAppearance=true;
            v.Cells[9].Flags=3|ColonyView.EntombedFlag;v.Cells[9].BuriedAnimation=-189;v.Cells[9].BuriedRotation=64589;
            v.PileCoverage[1]=2;
            v.Piles=new[]{new ColonyPile {Cell=9,SaveId=34,HasAppearance=true,File=-19,Frame=14,Tint=0x831226ff,
                M00=.007f,M11=-.007f,M02=123.25f,M12=151.8f,Z=-23.8f}};
            var copy=ColonyView.Decode(v.Encode());
            Assert.True(copy.HasPileAppearance);Assert.Equal(-189,copy.Cells[9].BuriedAnimation);
            Assert.Equal(64589,copy.Cells[9].BuriedRotation);
            var p=copy.Piles[0];
            Assert.Equal((-19,14,0x831226ffu,-23.8f,123.25f),(p.File,p.Frame,p.Tint,p.Z,p.M02));
            v.Piles[0].M01=float.NaN;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
        }

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
            raw[^1]=11;
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
        public void ArtificialLightMaskAndDrawSourcesSurviveWithoutHud()
        {
            var view=View();view.HasEnvironment=true;
            view.LightMask=new byte[ColonyView.Width*ColonyView.Height/8];view.LightMask[3]=129;
            var source=new ColonyLight {Id=-40,Order=3,X=13.125f,Y=4.25f,Z=16.1f,OffsetX=.25f,Range=6,
                DirectionY=-1,Shape=1,Angle=35,Red=1.7f,Green=.75f,Blue=.5f,Overlay=0xF0877208,DrawOverlay=true};
            view.Lights=new[]{source};
            var copy=ColonyView.Decode(view.Encode());
            Assert.False(copy.HasColonyHud);Assert.False(copy.HasPileAppearance);
            Assert.Equal(view.LightMask,copy.LightMask);Assert.Equal(source,Assert.Single(copy.Lights));
            view.Lights=new[]{source,source};
            Assert.Throws<InvalidDataException>(()=>view.Encode());
            view.LightMask=null;
            Assert.Null(ColonyView.Decode(view.Encode()).LightMask);
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
