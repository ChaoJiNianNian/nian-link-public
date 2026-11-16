using System.IO.Pipelines;
using System.Text;
using DesktopToolbox.DeskLink;
namespace DeskLink.Tests;

public sealed class ControlProtocolTests
{
    [Fact]
    public async Task ConditionalSwitchSendsExpectedOwnerAndRevisionWithoutFallbackToOldSwitch()
    {
        var (clientStream, serverStream) = Duplex();
        await using var client = new NativeControlClient(clientStream);
        var pending = client.SwitchIfCurrentAsync("Win-Two", "Win-PC", 42);
        using var frame = System.Text.Json.JsonDocument.Parse((await JsonLineProtocol.ReadAsync(serverStream))!);
        var request = frame.RootElement;
        var id = request.GetProperty("requestId").GetString();
        Assert.Equal("switch-if-current", request.GetProperty("action").GetString());
        Assert.Equal("Win-Two", request.GetProperty("target").GetString());
        Assert.Equal("Win-PC", request.GetProperty("expectedActive").GetString());
        Assert.Equal(42, request.GetProperty("expectedRevision").GetInt64());
        // The real legacy core rejects unknown actions with an empty activeComputer.
        await JsonLineProtocol.WriteAsync(serverStream, new { version = 1, requestId = id, ok = false, activeComputer = "", connectedComputers = Array.Empty<string>(), revision = 0, error = "invalid_action" });
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Ok);
        Assert.Equal("invalid_action", result.Error);
        using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JsonLineProtocol.ReadAsync(serverStream, shortWait.Token));
        await serverStream.DisposeAsync();
    }
    [Fact]
    public async Task NativeClientCorrelatesReplyAndReportsOnlyConfirmedFocusEvents()
    {
        var (clientStream, serverStream) = Duplex();
        await using var client = new NativeControlClient(clientStream);
        var focus = new TaskCompletionSource<FocusChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FocusChanged += (_, value) => focus.TrySetResult(value);
        var pending = client.SwitchAsync("My-Mac");
        var request = await JsonLineProtocol.ReadAsync(serverStream);
        var id = System.Text.Json.JsonDocument.Parse(request!).RootElement.GetProperty("requestId").GetString();
        await JsonLineProtocol.WriteAsync(serverStream, new { version = 1, requestId = id, ok = true, activeComputer = "My-Mac", connectedComputers = new[] { "Win-PC", "My-Mac" }, revision = 4 });
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(reply.Ok);
        Assert.Equal("My-Mac", reply.ActiveComputer);
        Assert.False(focus.Task.IsCompleted);
        await JsonLineProtocol.WriteAsync(serverStream, new { version = 1, @event = "focusChanged", activeComputer = "My-Mac", revision = 4 });
        Assert.Equal(4, (await focus.Task.WaitAsync(TimeSpan.FromSeconds(2))).Revision);
        await serverStream.DisposeAsync();
    }
    [Fact]
    public async Task BoundedJsonFramingRejectsOversizedOrIncompleteFrames()
    {
        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 16385) + "\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => JsonLineProtocol.ReadAsync(oversized));
        using var incomplete = new MemoryStream(Encoding.UTF8.GetBytes("{\"version\":1}"));
        await Assert.ThrowsAsync<EndOfStreamException>(() => JsonLineProtocol.ReadAsync(incomplete));
    }
    [Fact]
    public void CertificateFingerprintsAreValidatedBeforeAnyConnection()
    {
        Assert.Throws<ArgumentException>(() => DeskLinkFingerprint.Normalize("not-a-fingerprint"));
        Assert.True(DeskLinkFingerprint.Matches(new string('A', 64), string.Join(":", Enumerable.Repeat("aa", 32))));
    }
    internal static (Stream, Stream) Duplex()
    {
        var one = new Pipe(); var two = new Pipe();
        return (new DuplexStream(one.Reader.AsStream(), two.Writer.AsStream()), new DuplexStream(two.Reader.AsStream(), one.Writer.AsStream()));
    }
    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush(); public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => input.Read(b,o,c);
        public override ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default) => input.ReadAsync(b,ct);
        public override void Write(byte[] b,int o,int c) => output.Write(b,o,c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b,CancellationToken ct=default) => output.WriteAsync(b,ct);
        public override long Seek(long o,SeekOrigin so)=>throw new NotSupportedException(); public override void SetLength(long v)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if(disposing){input.Dispose();output.Dispose();} base.Dispose(disposing); }
    }
}
