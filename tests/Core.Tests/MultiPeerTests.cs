using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Queyang.Core;
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
        h.Receive(1, first); Pump(); Assert.Equal(65, writes); Assert.Contains("Expired", c.Status);
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
