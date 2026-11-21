using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Queyang.Core;
using Queyang.Core.Tests;
using Queyang.Game;
using Xunit;

public class MultiPeerTests
{
    // Ordered channels model a reliable transport: a lost packet delays this peer's stream until retransmission.
    sealed class Channel
    {
        readonly Random random;
        readonly Queue<(int due, byte[] bytes)> queue = new();
        int tail;
        public Channel(int seed) => random = new Random(seed);
        public void Write(int tick, byte[] bytes)
        {
            var due = tick + 3 + random.Next(3);
            if (random.Next(100) < 2) due += 8;
            tail = Math.Max(tail, due);
            queue.Enqueue((tail, bytes));
        }
        public void Drain(int tick, Action<byte[]> read)
        {
            while (queue.Count > 0 && queue.Peek().due <= tick) read(queue.Dequeue().bytes);
        }
    }

    [Fact]
    public void ThreeGuestsCompeteThenOneStallsAndRejoinsFresh()
    {
        var world = new byte[16];
        int tick = 0;
        var down = new Dictionary<int, Channel>();
        var up = new Dictionary<int, Channel>();
        var guests = new Dictionary<int, RoomClient>();
        var results = new List<string>();
        var dropped = new List<int>();
        var host = new RoomHost("build", (id, bytes) => down[id].Write(tick, bytes), id => dropped.Add(id),
            (x, y) => (byte[])world.Clone(), (action, x, y) =>
            {
                if (world[0] != 0) return "Occupied";
                world[0] = 1;
                return "Accepted";
            }, () => tick * 0.02);
        void Join(int id)
        {
            down[id] = new Channel(id + 457140); up[id] = new Channel(id);
            guests[id] = new RoomClient("build", bytes => up[id].Write(tick, bytes));
            guests[id].ResultReceived += results.Add;
            host.Connect(id); guests[id].Connected();
        }
        void Run(int ticks, int stalled = 0)
        {
            for (int step = 0; step < ticks; step++, tick++)
            {
                foreach (var id in guests.Keys.ToArray())
                {
                    if (id == stalled) continue;
                    if (tick % 150 == 0) guests[id].Ping(tick);
                    up[id].Drain(tick, b => host.Receive(id, b));
                    down[id].Drain(tick, b => guests[id].Receive(b));
                }
                host.Tick();
            }
        }
        Join(1); Join(2); Join(3); Run(100);
        foreach (var client in guests.Values) { Assert.True(client.Live); client.Command("build", 0, 0); }
        Run(200);
        Assert.Single(results, r => r == "Accepted");
        Assert.Equal(2, results.Count(r => r == "Occupied"));
        world[1] = 27; Run(1600, 3);
        Assert.Contains(3, dropped); Assert.Equal(2, host.Guests);
        Assert.Equal(world, guests[1].View); Assert.Equal(world, guests[2].View);
        guests.Remove(3); Join(4); Run(100);
        Assert.Equal(world, guests[4].View);
    }

    [Fact]
    public void StalledNativeSaveKeepsOtherCamerasAndPagedEnvironmentCurrent()
    {
        string temp=Directory.CreateTempSubdirectory("qy-camera-").FullName;
        double time=0;
        var world=ExtendedViewportTests.DenseWorld();
        var baseline=new byte[10*1024*1024];
        var up=new Queue<byte[]>[3]; var down=new Queue<byte[]>[3];
        var guests=new RoomClient[3]; var drops=new List<int>();
        MemoryStream source=null; string published=null;
        using var host=new RoomHost("build",(id,b)=>down[id].Enqueue(b),drops.Add,(x,y)=>
        {
            world.X=Math.Max(0,x);
            for (int i=0; i<world.Dupes.Length; ++i) world.Dupes[i].X=world.X;
            return world.Encode();
        },(a,x,y)=>"OK",()=>time,()=>source=new MemoryStream(baseline,false));
        void Join(int id)
        {
            up[id]=new Queue<byte[]>(); down[id]=new Queue<byte[]>();
            guests[id]=id==2 ? new RoomClient("build",up[id].Enqueue,temp,file=>published=file)
                             : new RoomClient("build",up[id].Enqueue);
            host.Connect(id); guests[id].Connected();
        }
        void Pump(bool stall)
        {
            for (int i=0; i<3; ++i)
                if (!(stall && i==2))
                    while (up[i].TryDequeue(out var frame)) host.Receive(i,frame);
            host.Tick();
            for (int i=0; i<3; ++i)
                while (down[i].TryDequeue(out var frame)) guests[i].Receive(frame);
        }
        try
        {
            Join(0); Join(1); Join(2);
            Pump(false); Pump(true); Pump(true);
            Assert.Equal(8*Wire.ChunkSize,source.Position); Assert.Null(published);
            var oldAck=up[2].Peek();
            // Freeze an old camera target on the wire while the authority changes.
            time+=.3; world.Cells[0].Mass=13; host.Tick();
            guests[0].Subscribe(48,0); guests[1].Subscribe(24,0);
            world.EntityPayload[0]^=0xff; world.Stocks[0].Total=451; baseline=new byte[]{9,7,2};
            for (int page=0;page<4;++page)
            {
                int index=page*world.Cells.Length/4;
                world.EnvironmentPage=page; world.Cells[index].RoomSize=101+page;
                world.Cells[index].Germs=74+page; world.Cells[index].Mass=2.25f+page;
                time+=.3; Pump(true); Pump(true);
                for (int id=0;id<2;id++)
                {
                    var v=ColonyView.Decode(guests[id].View);
                    Assert.Equal(id==0 ? 48 : 24,v.X); Assert.True(v.CoversEnvironment(index));
                    Assert.Equal((101+page,74+page,2.25f+page),(v.Cells[index].RoomSize,v.Cells[index].Germs,v.Cells[index].Mass));
                    Assert.Equal(world.EntityPayload,v.EntityPayload); Assert.Equal(451,v.Stocks[0].Total);
                }
            }
            time+=31; guests[0].Ping(1); guests[1].Ping(2); Pump(true);
            Assert.Equal(new[]{2},drops); Assert.False(source.CanRead); Assert.Equal(2,host.Guests);
            guests[2].Dispose(); Assert.Empty(Directory.GetFiles(temp));
            Join(2); Pump(false); Assert.Throws<InvalidDataException>(()=>host.Receive(2,oldAck));
            Pump(false); Assert.Equal(baseline,File.ReadAllBytes(published));
            guests[2].Subscribe(72,0); guests[2].NativeWorldReady(); Pump(false); Pump(false);
            var fresh=ColonyView.Decode(guests[2].View);
            Assert.Equal(72,fresh.X); Assert.Equal(world.EntityPayload,fresh.EntityPayload);
            Assert.Equal(104,fresh.Cells[288].RoomSize); Assert.Equal(451,fresh.Stocks[0].Total);
        }
        finally
        {
            foreach(var guest in guests) guest?.Dispose();
            Directory.Delete(temp,true);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void EachGuestGetsAllFourDensePagesForTheSameCenterAndPeripheral(int count)
    {
        var world=ExtendedViewportTests.DenseWorld();
        var up=new Queue<byte[]>[count]; var down=new Queue<byte[]>[count];
        var guests=new RoomClient[count]; var center=new HashSet<int>[count]; var peripheral=new HashSet<int>[count];
        string temp=Directory.CreateTempSubdirectory("qy-pages-").FullName;
        double time=0;
        using var host=new RoomHost("v",(id,b)=>down[id].Enqueue(b),_=>{},null,(a,x,y)=>"OK",()=>time,
            ()=>new MemoryStream(new byte[]{4}), (x,y,sequence)=>{
                world.EnvironmentPage=sequence%4;
                return world.Encode();
            });
        void Record(HashSet<int> seen,byte[] bytes)
        {
            var frame=ColonyView.Decode(bytes);
            Assert.Equal(96,Enumerable.Range(0,384).Count(frame.CoversEnvironment));
            seen.Add(frame.EnvironmentPage);
        }
        void Pump()
        {
            for(int i=0;i<count;i++) while(up[i].TryDequeue(out var b)) host.Receive(i,b);
            host.Tick();
            for(int i=0;i<count;i++) while(down[i].TryDequeue(out var b)) guests[i].Receive(b);
        }
        try
        {
            for(int i=0;i<count;i++)
            {
                int id=i; up[id]=new Queue<byte[]>(); down[id]=new Queue<byte[]>();
                center[id]=new HashSet<int>(); peripheral[id]=new HashSet<int>();
                guests[id]=new RoomClient("v",up[id].Enqueue,temp,file=>File.Delete(file));
                guests[id].ViewReceived+=b=>Record(center[id],b);
                guests[id].PeripheralReceived+=b=>Record(peripheral[id],b);
                host.Connect(id); guests[id].Connected();
            }
            for(int tick=0;tick<8;tick++) Pump();
            foreach(var client in guests) { client.Subscribe(0,0); client.NativeWorldReady(); }
            Pump(); Pump();
            for(int i=0;i<4;i++)
            {
                foreach(var client in guests) Assert.True(client.RequestPeripheral(24,0));
                time+=.3; Pump(); Pump();
            }
            foreach(var seen in center.Concat(peripheral)) Assert.Equal(new[]{0,1,2,3},seen.OrderBy(x=>x));
        }
        finally { foreach(var client in guests) client?.Dispose(); Directory.Delete(temp,true); }
    }

    [Fact]
    public void CacheEvictionDoesNotReexecuteOldCommand()
    {
        byte[] first = null; int writes = 0; double time = 0;
        var up = new Queue<byte[]>(); var down = new Queue<byte[]>();
        var h = new RoomHost("v", (_, b) => down.Enqueue(b), _ => { }, (x, y) => new byte[1],
            (a, x, y) => { writes++; return "OK"; }, () => time);
        var c = new RoomClient("v", up.Enqueue);
        h.Connect(1); c.Connected();
        void Pump()
        {
            while (up.Count > 0) h.Receive(1, up.Dequeue());
            h.Tick(); while (down.Count > 0) c.Receive(down.Dequeue());
        }
        for (int i = 0; i < 5; i++) Pump();
        for (int i = 0; i < 65; i++)
        {
            time += 0.2; c.Command("dig", 1, 1); first ??= up.Peek(); Pump();
        }
        var status = c.Status;
        h.Receive(1, first);
        Assert.Contains("ERROR Expired", System.Text.Encoding.UTF8.GetString(down.Peek()));
        Pump(); Assert.Equal(65, writes); Assert.Equal(status, c.Status);
    }

    [Theory]
    [InlineData(-1, 0, 14)]
    [InlineData(15, 0, 14)]
    [InlineData(5, 10, 14)]
    [InlineData(0, 0, 134217729)]
    public void RejectInvalidPatchBeforeAllocation(int prefix, int suffix, int length)
    {
        Assert.Throws<InvalidDataException>(() => new ViewPatch(prefix, suffix, length, new byte[32]));
    }
}
