using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Queyang.Core;
using Queyang.Game;
using Xunit;

namespace Queyang.Core.Tests
{
    public class OrderOwnershipTests
    {
        [Fact]
        public void ApprovedCommandsCarryTheSameInstallationAcrossNewPeerGenerations()
        {
            var installation=Guid.NewGuid();
            var calls=new List<(int,Guid,string)>();
            var down=new Queue<byte[]>(); var up=new Queue<byte[]>();
            double time=0;
            using var host=new RoomHost("test",(_,packet)=>down.Enqueue(packet),_=>{},
                (_,__)=>new byte[]{1},(_,__,___)=>throw new Exception("missing attribution"),()=>time);
            host.ExecuteForPeer=(peer,id,action,x,y)=>{ calls.Add((peer,id,action)); return "OK"; };
            foreach (int peer in new[]{7,18})
            {
                using var client=new RoomClient("test",up.Enqueue){Installation=installation};
                host.Connect(peer); client.Connected();
                void Poll()
                {
                    while (up.TryDequeue(out var data)) host.Receive(peer,data);
                    host.Tick();
                    while (down.TryDequeue(out var data)) client.Receive(data);
                    time+=.02;
                }
                for (int n=0;n<100 && !client.Live;n++) Poll();
                Assert.True(client.Live); Poll();
                client.Command("dig",3,4);
                var command=up.Peek();
                up.Enqueue(command);
                Poll();
                host.Remove(peer);
            }
            Assert.Equal(new[]{(7,installation,"dig"),(18,installation,"dig")},calls);
        }

        [Fact]
        public void MixedCreatorsOnOneTileSurviveRoundTripAndAnEmptyPageClearsIt()
        {
            var v=new ColonyView{Native=true,Detailed=true,HasResources=true,OrderFirstRow=1,OrderRows=1};
            int cell=ColonyView.Width;
            v.Cells[cell].Flags=1;
            v.OrderMarks=new[]{
                new OrderMark{Cell=cell,Kind=OrderKind.Build,Author=1},
                new OrderMark{Cell=cell,Kind=OrderKind.Sweep,Author=2},
                new OrderMark{Cell=cell,Kind=OrderKind.Sweep,Author=3},
                new OrderMark{Cell=cell,Kind=OrderKind.Dig,Author=4}};
            v.OrderNames[2]=ColonyView.CleanOrderName(" <b>\nA\u202e🙂 ");
            var copy=ColonyView.Decode(v.Encode());
            Assert.Equal("＜b＞A🙂",copy.OrderNames[2]);
            Assert.Equal(v.OrderMarks,copy.OrderMarks);
            v.OrderNames.Clear(); v.OrderMarks=Array.Empty<OrderMark>();
            copy=ColonyView.Decode(v.Encode());
            Assert.Empty(copy.OrderMarks); Assert.Equal((byte)1,copy.OrderFirstRow);
            v.OrderMarks=null;
            Assert.Null(ColonyView.Decode(v.Encode()).OrderMarks);
        }

        [Fact]
        public void UnknownCellsAndOutOfPageAttributionAreRejected()
        {
            var v=new ColonyView{Native=true,Detailed=true,HasResources=true,OrderRows=1};
            v.OrderMarks=new[]{new OrderMark{Cell=24,Author=2}};
            Assert.Throws<InvalidDataException>(()=>v.Encode());
            v.OrderMarks[0].Cell=1;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
            v.Cells[1].Flags=1;
            v.OrderMarks[0].Kind=(OrderKind)7;
            Assert.Throws<InvalidDataException>(()=>v.Encode());
            Assert.Equal(32,ColonyView.CleanOrderName(string.Concat(Enumerable.Repeat("🙂",70))).Length);
        }

        [Fact]
        public void OwnershipAndDenseColonyFitTheTransportEnvelopeTogether()
        {
            var v=ExtendedViewportTests.DenseWorld();
            v.OrderFirstRow=0; v.OrderRows=1;
            v.OrderMarks=Enumerable.Range(0,ColonyView.Width).SelectMany(cell=>
                Enumerable.Range(1,4).SelectMany(author=>Enumerable.Range(0,6).Select(k=>
                    new OrderMark{Cell=cell,Kind=(OrderKind)k,Author=author}))).ToArray();
            for (int i=1;i<=4;i++) v.OrderNames[i]="Guest"+i;
            var packet=v.Encode();
            Assert.InRange(packet.Length,1,65520);
            var decoded=ColonyView.Decode(packet);
            Assert.Equal(v.OrderMarks,decoded.OrderMarks);
            Assert.Equal(v.EntityPayload,decoded.EntityPayload);
        }
    }
}
