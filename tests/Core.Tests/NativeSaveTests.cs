using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Queyang.Core;
using Xunit;

public sealed class NativeSaveTests
{
    private sealed class Join : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "qy-native-" + Guid.NewGuid().ToString("N"));
        internal readonly Queue<byte[]> Up = new(), Down = new();
        internal readonly RoomHost Host;
        internal RoomClient Client;
        internal byte[] Colony, LiveView = new byte[] { 1 };
        internal int Captures, Calls, Drops, Peer = 1;
        internal double Time;
        internal MemoryStream LastSource;
        internal string Published;
        internal Action<byte[]> InTransit;
        internal Join(int bytes, bool supported = true, string clientVersion = "build")
        {
            Directory.CreateDirectory(DirectoryPath);
            Colony = new byte[bytes]; new Random(457140).NextBytes(Colony);
            Host = new RoomHost("build",(_,b) => Down.Enqueue(b),_ => Drops++,(x,y) => LiveView,
                (action,x,y) => { Calls++; return "OK"; },() => Time,supported ? Capture : null);
            Connect(clientVersion);
        }
        private Stream Capture()
        {
            Captures++;
            LastSource=new MemoryStream(Colony,false);
            return LastSource;
        }
        internal void Connect(string version="build")
        {
            Published=null;
            Client=new RoomClient(version,Up.Enqueue,DirectoryPath,file => Published=file);
            Host.Connect(Peer); Client.Connected();
        }
        internal void Step()
        {
            while (Up.TryDequeue(out var b)) Host.Receive(Peer,b);
            Host.Tick();
            while (Down.TryDequeue(out var b)) { InTransit?.Invoke(b); Client.Receive(b); }
            Time+=0.01;
        }
        internal void AwaitFile()
        {
            for (int i=0; i<500 && Published==null; i++) Step();
            Assert.NotNull(Published);
        }
        public void Dispose()
        {
            Client?.Dispose(); Host.Dispose();
            Directory.Delete(DirectoryPath,true);
        }
    }

    private static Message Kind(byte[] frame) => (Message)BitConverter.ToUInt16(frame,8);

    [Fact]
    public void LargeSaveOpensOnlyAfterVerificationAndUsesFreshViewAfterLoad()
    {
        using var j=new Join(11*1024*1024+19);
        j.Step();
        Assert.Equal(8*Wire.ChunkSize,j.Client.NativeBytesReceived);
        Assert.Null(j.Published); Assert.Empty(Directory.GetFiles(j.DirectoryPath,"*.sav"));
        Assert.Single(Directory.GetFiles(j.DirectoryPath,"*.part"));
        j.Client.Subscribe(5,6); j.Client.Command("dig",5,6);
        byte[] captured=j.Colony;
        j.Colony=new byte[] { 8 }; // The host continues playing during the old immutable transfer.
        j.AwaitFile();
        Assert.Equal(captured,File.ReadAllBytes(j.Published));
        Assert.Empty(Directory.GetFiles(j.DirectoryPath,"*.part"));
        Assert.False(j.LastSource.CanRead);
        Assert.True(j.Client.WaitingForNativeWorld); Assert.False(j.Client.Live);
        Assert.Empty(j.Up); Assert.Equal(0,j.Calls);
        j.Time+=90; j.Step(); Assert.Equal(0,j.Drops); // Native world loading can outlive an ordinary handshake.
        j.LiveView = new byte[]{2};
        j.Client.NativeWorldReady(); j.Step(); j.Step();
        Assert.True(j.Client.Live); Assert.Equal(j.LiveView,j.Client.View);
        j.Client.Command("dig",5,6); j.Step(); Assert.Equal(1,j.Calls);
    }

    [Fact]
    public void NativeReadyRequiresAppliedViewportBeforeFirstCommandCanMutateAuthority()
    {
        using var j=new Join(12); j.AwaitFile();
        j.Client.NativeWorldReady(); j.Step();
        Assert.True(j.Client.Live);
        var applied=j.Up.Dequeue();
        Assert.Equal(Message.Applied,Kind(applied));
        j.Client.Command("dig",2,3);
        j.Host.Receive(j.Peer,j.Up.Dequeue()); j.Step();
        Assert.Contains("View not ready",j.Client.Status); Assert.Equal(0,j.Calls);
        j.Host.Receive(j.Peer,applied);
        j.Client.Command("dig",2,3); j.Step(); Assert.Equal(1,j.Calls);
    }

    [Fact]
    public void PeripheralTileSpansTwoPacketsWithoutInterruptingInput()
    {
        using var j = new Join(19);
        Assert.False(j.Client.RequestPeripheral(10,20));
        j.AwaitFile();
        j.Client.NativeWorldReady(); j.Step(); j.Step();
        var center = j.Client.View;
        j.LiveView = new byte[65520];
        new Random(3).NextBytes(j.LiveView);
        byte[] delivered = null;
        j.Client.PeripheralReceived += bytes => delivered = bytes;
        Assert.True(j.Client.RequestPeripheral(10,20));
        Assert.False(j.Client.RequestPeripheral(20,30));
        j.Client.Command("dig",5,6);
        j.Step();
        Assert.Equal(j.LiveView,delivered); Assert.Same(center,j.Client.View); Assert.Equal(1,j.Calls);
    }

    [Fact]
    public void ProjectionCallbacksKeepOverlappingTilesInCaptureOrderWithinOnePoll()
    {
        using var j = new Join(10);
        j.AwaitFile(); j.Client.NativeWorldReady(); j.Step(); j.Step();
        var applied = new List<byte>();
        j.Client.ViewReceived += bytes => applied.Add(bytes[0]);
        j.Client.PeripheralReceived += bytes => applied.Add(bytes[0]);
        j.Time+=0.3; j.LiveView=new byte[]{2}; j.Host.Tick();
        Assert.True(j.Client.RequestPeripheral(0,0)); j.Host.Receive(j.Peer,j.Up.Dequeue());
        j.LiveView=new byte[]{3}; j.Host.Tick();
        while (j.Down.TryDequeue(out var frame)) j.Client.Receive(frame);
        Assert.Equal(new byte[]{2,3},applied);
        Assert.Equal(new byte[]{2},j.Client.View);
    }

    [Fact]
    public void LatePeripheralReplyCannotOverwriteNewCameraSubscription()
    {
        using var j = new Join(10);
        j.AwaitFile(); j.Client.NativeWorldReady(); j.Step(); j.Step();
        int deliveries=0; j.Client.PeripheralReceived += _ => deliveries++;
        Assert.True(j.Client.RequestPeripheral(10,20));
        j.Host.Receive(j.Peer,j.Up.Dequeue()); j.Host.Tick();
        j.Client.Subscribe(32,48);
        while (j.Down.TryDequeue(out var frame)) j.Client.Receive(frame);
        Assert.Equal(0,deliveries);
        j.Step(); j.Step(); j.Time+=0.3;
        Assert.True(j.Client.RequestPeripheral(32,48)); j.Step(); Assert.Equal(1,deliveries);
    }

    [Fact]
    public void InterruptedSaveIsRemovedAndRejoinCapturesNewBytes()
    {
        using var j=new Join(2*1024*1024);
        j.Step();
        Assert.True(j.LastSource.CanRead);
        j.Client.Dispose(); j.Host.Remove(j.Peer);
        Assert.False(j.LastSource.CanRead); Assert.Empty(Directory.GetFiles(j.DirectoryPath));
        byte[] staleAck=j.Up.Dequeue(); j.Up.Clear(); j.Down.Clear();
        j.Peer++; j.Connect();
        j.Colony=new byte[] {3,2,1};
        j.Step();
        Assert.Throws<InvalidDataException>(() => j.Host.Receive(j.Peer,staleAck));
        j.AwaitFile();
        Assert.Equal(2,j.Captures); Assert.Equal(j.Colony,File.ReadAllBytes(j.Published));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("manifest")]
    public void InvalidDataNeverReachesNativeLoader(string fault)
    {
        using var j=new Join(Wire.ChunkSize + 3);
        j.InTransit = b => {
            if (fault=="hash" && Kind(b)==Message.NativeEnd) b[b.Length-1]^=0xff;
            // Header (16) + room (16) + peer (4), then the integer length / byte offset.
            if (fault=="offset" && Kind(b)==Message.NativeChunk) BitConverter.GetBytes(7).CopyTo(b,36);
            if (fault=="size" && Kind(b)==Message.NativeBegin) BitConverter.GetBytes(Wire.MaxSnapshot+1).CopyTo(b,36);
            if (fault=="manifest" && Kind(b)==Message.NativeBegin) BitConverter.GetBytes(Wire.ChunkSize+2).CopyTo(b,36);
        };
        Assert.Throws<InvalidDataException>(() => { for(int i=0;i<5;i++) j.Step(); });
        Assert.Null(j.Published); Assert.Empty(Directory.GetFiles(j.DirectoryPath));
        Assert.False(j.Client.Live);
    }

    [Fact]
    public void UnavailableAndMismatchedHostsRejectBeforeCapture()
    {
        using var unsupported=new Join(8,false);
        unsupported.Step(); Assert.True(unsupported.Client.Rejected); Assert.Equal(0,unsupported.Captures);
        using var mismatch=new Join(8,true,"different");
        mismatch.Step(); Assert.True(mismatch.Client.Rejected); Assert.Equal(0,mismatch.Captures);
        using var empty=new Join(0);
        empty.Step(); Assert.True(empty.Client.Rejected);
        Assert.False(empty.LastSource.CanRead); Assert.Null(empty.Published);
    }

    [Fact]
    public void MissingAckBoundsReadAheadAndReleasesStalledSnapshot()
    {
        using var j=new Join(20*Wire.ChunkSize);
        j.Step();
        Assert.Equal(8*Wire.ChunkSize,j.LastSource.Position);
        for(int i=0;i<10;i++) j.Host.Tick();
        Assert.Equal(8*Wire.ChunkSize,j.LastSource.Position); Assert.Empty(j.Down);
        var impossible=j.Up.Dequeue(); BitConverter.GetBytes(9*Wire.ChunkSize).CopyTo(impossible,36);
        Assert.Throws<InvalidDataException>(() => j.Host.Receive(j.Peer,impossible));
        j.Time+=31; j.Host.Tick();
        Assert.False(j.LastSource.CanRead); Assert.Equal(1,j.Drops);
    }

    [Fact]
    public void CumulativeWindowsTolerateDuplicateAndOlderProgressWithoutExtendingReadAhead()
    {
        using var j=new Join(24*Wire.ChunkSize);
        j.Step();
        var first=j.Up.Dequeue(); var second=j.Up.Dequeue();
        j.Host.Receive(j.Peer,second); j.Host.Receive(j.Peer,first); j.Host.Receive(j.Peer,second);
        j.Host.Tick(); j.Host.Tick();
        Assert.Equal(16*Wire.ChunkSize,j.LastSource.Position);
        j.AwaitFile(); Assert.Equal(j.Colony,File.ReadAllBytes(j.Published));
    }

    [Fact]
    public void VerifiedFileOwnershipPassesOnlyToSuccessfulLoader()
    {
        using var j=new Join(8);
        j.Client.Dispose(); j.Up.Clear();
        j.Client=new RoomClient("build",j.Up.Enqueue,j.DirectoryPath,_ => throw new IOException("Failed to start native loader"));
        j.Client.Connected(); j.Step();
        Assert.Throws<IOException>(() => j.Step());
        Assert.Empty(Directory.GetFiles(j.DirectoryPath));
        Assert.False(j.Client.WaitingForNativeWorld);
    }
}
