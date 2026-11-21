using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Queyang.Core;
using Xunit;

public class RoomTests
{
    sealed class Pair
    {
        public readonly Queue<byte[]> Up = new(), Down = new();
        public RoomHost Host;
        public RoomClient Client;
        public byte[] Colony = new byte[] { 7, 8, 9 };
        public double Time;
        public int Calls, Dropped;
        public Pair(string build = "test")
        {
            Host = new RoomHost("test", (id, b) => Down.Enqueue(b), id => Dropped++,
                (x, y) => Colony, (action, x, y) => { Calls++; return "OK"; }, () => Time);
            Client = new RoomClient(build, Up.Enqueue);
            Host.Connect(1); Client.Connected();
        }
        public void Step()
        {
            while (Up.TryDequeue(out var data)) Host.Receive(1, data);
            Host.Tick();
            while (Down.TryDequeue(out var data)) Client.Receive(data);
            Time += 0.02;
        }
        public void Live()
        {
            for (int i = 0; i < 200 && !Client.Live; i++) Step();
            Assert.True(Client.Live);
            Step();
        }
    }

    [Fact]
    public void HeaderMatchesFixedLittleEndianVector()
    {
        var packet = Wire.Pack(Message.Hello, w => Wire.Text(w, "X"));
        Assert.Equal("51594D50010000000100000003000000010058", Convert.ToHexString(packet));
        Assert.Throws<InvalidDataException>(() => Wire.Read(packet[..^1], (_, _) => { }));
        packet[4] = 2;
        Assert.Throws<InvalidDataException>(() => Wire.Read(packet, (_, _) => { }));
    }

    [Fact]
    public void FullThenChangedRangeThenViewportSwitch()
    {
        var pair = new Pair(); pair.Live();
        Assert.Equal(pair.Colony, pair.Client.View);
        pair.Colony = new byte[] { 7, 3, 2, 9 }; pair.Time += 1; pair.Step(); pair.Step();
        Assert.Equal(pair.Colony, pair.Client.View);
        pair.Client.Subscribe(12, 8);
        Assert.False(pair.Client.Live);
        pair.Live(); Assert.Equal(pair.Colony, pair.Client.View);
    }

    [Fact]
    public void LargeBaselineRemainsAtomicAndDoesNotBlockWorldChanges()
    {
        var p = new Pair(); var world = new byte[11 * 1024 * 1024]; new Random(457140).NextBytes(world);
        p.Colony = world; p.Step(); p.Step();
        Assert.Null(p.Client.View);
        var changed = (byte[])world.Clone(); changed[2000] ^= 1; p.Colony = changed;
        p.Live();
        for (int i = 0; i < 30; i++) p.Step();
        Assert.Equal(changed, p.Client.View);
    }

    [Fact]
    public void CorruptSnapshotTriggersFreshBaseline()
    {
        var p = new Pair(); p.Step();
        while (p.Up.TryDequeue(out var b)) p.Host.Receive(1, b);
        p.Host.Tick();
        var begin = p.Down.Dequeue(); p.Client.Receive(begin);
        var chunk = p.Down.Dequeue(); chunk[^1] ^= 1; p.Client.Receive(chunk);
        Assert.Null(p.Client.View);
        p.Live(); Assert.Equal(p.Colony, p.Client.View);
    }

    [Fact]
    public void ReplayedCommandDoesNotExecuteTwice()
    {
        var p = new Pair(); p.Live(); p.Client.Command("dig", 2, 3); var b = p.Up.Dequeue();
        p.Host.Receive(1, b); p.Host.Receive(1, b);
        Assert.Equal(1, p.Calls);
        Assert.Equal(p.Down.Dequeue(), p.Down.Dequeue());
        p.Host.Remove(1); p.Host.Connect(2);
        Assert.Throws<InvalidDataException>(() => p.Host.Receive(2, b));
    }

    [Fact]
    public void PeerBuildAndSeatLimitsAreChecked()
    {
        var p = new Pair("different"); p.Step();
        Assert.True(p.Client.Rejected); Assert.False(p.Client.Live);
        p.Host.Connect(2); p.Host.Connect(3); p.Host.Connect(4);
        Assert.Equal(1, p.Dropped);
        p.Time += 35; p.Host.Tick(); Assert.Equal(4, p.Dropped);
    }

    [Fact]
    public void MalformedCommandCannotMutateWorld()
    {
        var p = new Pair(); p.Live(); p.Client.Command("dig", 2, 3); var b = p.Up.Dequeue();
        var trailing = b.Append((byte)0).ToArray(); BitConverter.GetBytes(trailing.Length - 16).CopyTo(trailing, 12);
        Assert.Throws<InvalidDataException>(() => p.Host.Receive(1, trailing));
        Assert.Equal(0, p.Calls);
    }

    [Fact]
    public void StaleViewportPacketsAreIgnored()
    {
        var p = new Pair(); p.Live();
        p.Colony = new byte[] { 1, 2, 3, 4 }; p.Time += 1; p.Host.Tick();
        p.Client.Subscribe(20, 21);
        while (p.Down.TryDequeue(out var b)) p.Client.Receive(b);
        Assert.Null(p.Client.View); p.Live(); Assert.Equal(p.Colony, p.Client.View);
    }
}
