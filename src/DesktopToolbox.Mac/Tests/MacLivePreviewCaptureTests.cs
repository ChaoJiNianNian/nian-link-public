using System.Buffers.Binary;
using System.Text;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.Mac.Services.LivePreview;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacLivePreviewCaptureTests
{
    private static readonly DateTimeOffset CaptureTime=new(2026,10,27,1,0,0,TimeSpan.Zero);
    private static readonly byte[] Jpeg=[255,216,1,2,255,217];

    [Fact]
    public async Task BinaryImagesAndIdleKeepTheOriginalImageTimeSeparate()
    {
        await using var helper=new Helper(new MemoryStream([..Packet(1,1,Jpeg,640,360,CaptureTime),..Packet(2,2,[])]));
        var provider=new MacLivePreviewCaptureProvider((_,_)=>ValueTask.FromResult<IMacLivePreviewHelper>(helper),()=>true);
        Assert.True(provider.IsSupported);
        await using(var frames=provider.CaptureAsync(new()).GetAsyncEnumerator())
        {
            Assert.True(await frames.MoveNextAsync());Assert.Equal(LivePreviewCaptureKind.Image,frames.Current.Kind);
            Assert.Equal(CaptureTime,frames.Current.CapturedAtUtc);Assert.Equal(Jpeg,frames.Current.JpegBytes);
            Assert.True(await frames.MoveNextAsync());Assert.Equal(LivePreviewCaptureKind.HealthyIdle,frames.Current.Kind);
            Assert.Equal(default,frames.Current.CapturedAtUtc);Assert.Empty(frames.Current.JpegBytes);
        }
        Assert.True(helper.Disposed);
    }

    [Fact]
    public async Task OversizedNativeLengthIsRejectedBeforeAttemptingToReadItsBody()
    {
        var header=Packet(1,1,[],640,360,CaptureTime);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32),LivePreviewCaptureOptions.MaximumFrameBytes+1);
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await MacLivePreviewNativeProtocol.ReadAsync(new MemoryStream(header)));
    }

    [Fact]
    public async Task TruncatedBinaryPayloadCannotBecomeAValidFrame()
    {
        var bytes=Packet(1,1,Jpeg,640,360,CaptureTime);
        await Assert.ThrowsAsync<EndOfStreamException>(async()=>await MacLivePreviewNativeProtocol.ReadAsync(new MemoryStream(bytes[..^2])));
    }

    [Theory]
    [InlineData(0,0)]
    [InlineData(4,9)]
    [InlineData(6,1)]
    [InlineData(36,1)]
    public async Task UnknownHeaderOrReservedBitsAreNotSilentlyAccepted(int offset,byte value)
    {
        var bytes=Packet(1,1,Jpeg,640,360,CaptureTime);bytes[offset]=value;
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await MacLivePreviewNativeProtocol.ReadAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task ARepeatedSequenceFailsAndDisposesOnlyItsOwnedHelper()
    {
        var helper=new Helper(new MemoryStream([..Packet(1,1,Jpeg,640,360,CaptureTime),..Packet(1,1,Jpeg,640,360,CaptureTime)]));
        var other=new Helper(new MemoryStream());
        var provider=new MacLivePreviewCaptureProvider((_,_)=>ValueTask.FromResult<IMacLivePreviewHelper>(helper),()=>true);
        await using(var frames=provider.CaptureAsync(new()).GetAsyncEnumerator())
        {
            Assert.True(await frames.MoveNextAsync());
            await Assert.ThrowsAsync<InvalidDataException>(async()=>await frames.MoveNextAsync());
        }
        Assert.True(helper.Disposed);Assert.False(other.Disposed);await other.DisposeAsync();
    }

    [Fact]
    public async Task CancellationOfABlockedReadStopsOwnedCaptureWithoutAFrame()
    {
        var output=new WaitingStream();var helper=new Helper(output);
        var provider=new MacLivePreviewCaptureProvider((_,_)=>ValueTask.FromResult<IMacLivePreviewHelper>(helper),()=>true);
        using var cancellation=new CancellationTokenSource();
        await using(var frames=provider.CaptureAsync(new(),cancellation.Token).GetAsyncEnumerator())
        {
            var pending=frames.MoveNextAsync().AsTask();await output.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending);
        }
        Assert.True(helper.Disposed);
    }

    [Fact]
    public async Task InvalidOptionsNeverStartAHelper()
    {
        var starts=0;var provider=new MacLivePreviewCaptureProvider((_,_)=>{starts++;return ValueTask.FromResult<IMacLivePreviewHelper>(new Helper(new MemoryStream()));},()=>true);
        await using var frames=provider.CaptureAsync(new(){FramesPerSecond=0}).GetAsyncEnumerator();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async()=>await frames.MoveNextAsync());Assert.Equal(0,starts);
    }

    [Fact]
    public async Task APermissionFailureEndsTheGenerationWithoutDisplayingAnAppendedOldFrame()
    {
        var helper=new Helper(new MemoryStream([..Packet(3,1,Encoding.ASCII.GetBytes("permission")),..Packet(1,2,Jpeg,640,360,CaptureTime)]));
        var provider=new MacLivePreviewCaptureProvider((_,_)=>ValueTask.FromResult<IMacLivePreviewHelper>(helper),()=>true);
        var frames=new List<LivePreviewCapturePacket>();await foreach(var frame in provider.CaptureAsync(new()))frames.Add(frame);
        var failure=Assert.Single(frames);Assert.Equal("permission",failure.FailureCode);Assert.Empty(failure.JpegBytes);Assert.True(helper.Disposed);
    }

    [Fact]
    public async Task IdleCannotSmuggleAnImageOrAnImageTimestamp()
    {
        await Assert.ThrowsAsync<InvalidDataException>(async()=>await MacLivePreviewNativeProtocol.ReadAsync(new MemoryStream(Packet(2,1,Jpeg,640,360,CaptureTime))));
    }

    private static byte[] Packet(byte kind,long sequence,byte[] body,int width=0,int height=0,DateTimeOffset captured=default)
    {
        var bytes=new byte[40+body.Length];"NLMC"u8.CopyTo(bytes);bytes[4]=1;bytes[5]=kind;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8),sequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16),captured==default?0:captured.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24),width);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28),height);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32),body.Length);body.CopyTo(bytes,40);return bytes;
    }
    private sealed class Helper(Stream output):IMacLivePreviewHelper
    {
        public Stream Output=>output;public bool Disposed{get;private set;}
        public async ValueTask DisposeAsync(){Disposed=true;await output.DisposeAsync();}
    }
    private sealed class WaitingStream:Stream
    {
        public TaskCompletionSource ReadStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {ReadStarted.TrySetResult();await Task.Delay(Timeout.Infinite,cancellationToken);return 0;}
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
