using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

/// <summary>Temporary continuous capture for control; passive screenshot settings stay unchanged.</summary>
public sealed class WindowsLivePreviewCaptureProvider : ILivePreviewCaptureProvider
{
    private readonly IWindowsLivePreviewSessionFactory factory;
    private int capturing;
    public WindowsLivePreviewCaptureProvider() : this(new GdiLivePreviewSessionFactory()) { }
    internal WindowsLivePreviewCaptureProvider(IWindowsLivePreviewSessionFactory factory) => this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    public bool IsSupported => factory.IsSupported;
    public async IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported) { yield return Unavailable(1, "unsupported"); yield break; }
        if (Interlocked.CompareExchange(ref capturing, 1, 0) != 0) { yield return Unavailable(1, "source-unavailable"); yield break; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var frames = new LatestFrameMailbox<LivePreviewCapturePacket>();
        // GetDC, drawing and ReleaseDC must all remain on the same native owner thread.
        var producer = Task.Factory.StartNew(() => Produce(options, frames, lifetime.Token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            while (true)
            {
                var packet = await frames.ReadAsync(cancellationToken).ConfigureAwait(false);
                yield return packet;
                if (packet.Kind == LivePreviewCaptureKind.Unavailable) yield break;
            }
        }
        finally
        {
            lifetime.Cancel();
            try { await producer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Trace.TraceError("Windows live capture is still returning from a native call; its owner retains cleanup responsibility."); }
        }
    }

    private void Produce(LivePreviewCaptureOptions options, LatestFrameMailbox<LivePreviewCapturePacket> frames, CancellationToken token)
    {
        long sequence = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            using var session = factory.Open(options);
            var interval = TimeSpan.FromSeconds(1d / options.FramesPerSecond);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var started = Stopwatch.GetTimestamp();
                var sample = session.Capture(token);
                token.ThrowIfCancellationRequested();
                if (sample.FailureCode is { } failure) { frames.Publish(Unavailable(++sequence, SafeFailure(failure))); return; }
                if (!IsValid(sample, options)) { frames.Publish(Unavailable(++sequence, "capture-failed")); return; }
                frames.Publish(new() { Kind = LivePreviewCaptureKind.Image, Sequence = ++sequence, CapturedAtUtc = sample.CapturedAtUtc,
                    Width = sample.Width, Height = sample.Height, JpegBytes = sample.JpegBytes });
                var remaining = interval - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero && token.WaitHandle.WaitOne(remaining)) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (WindowsLiveCaptureUnavailableException unavailable) { frames.Publish(Unavailable(++sequence, SafeFailure(unavailable.Code))); }
        catch (Exception) { frames.Publish(Unavailable(++sequence, "capture-failed")); }
        finally { Volatile.Write(ref capturing, 0); }
    }

    private static bool IsValid(WindowsLivePreviewSample sample, LivePreviewCaptureOptions options) =>
        sample.Width > 0 && sample.Width <= options.MaxWidth && sample.Height > 0 && sample.Height <= options.MaxHeight &&
        sample.CapturedAtUtc != default && sample.JpegBytes is { Length: >= 4 and <= LivePreviewCaptureOptions.MaximumFrameBytes } jpeg &&
        jpeg[0] == 0xff && jpeg[1] == 0xd8 && jpeg[^2] == 0xff && jpeg[^1] == 0xd9;
    private static string SafeFailure(string code) => code switch
    {
        "locked" => "locked",
        "unsupported" => "unsupported",
        "display-unavailable" => "source-unavailable",
        _ => "capture-failed"
    };
    private static LivePreviewCapturePacket Unavailable(long sequence, string reason) => new()
        { Kind = LivePreviewCaptureKind.Unavailable, Sequence = sequence, FailureCode = reason };
}
