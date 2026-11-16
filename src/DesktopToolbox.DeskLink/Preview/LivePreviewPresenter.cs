using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using System.Diagnostics;

namespace DesktopToolbox.DeskLink.Preview;

/// <summary>Background decoding and one pending UI delivery. Successful presentation transfers image ownership.</summary>
public sealed partial class LivePreviewPresenter<T> : IAsyncDisposable
{
    private sealed record Input(Guid Epoch, LivePreviewCapturePacket Packet);
    private sealed record Output(Guid Epoch, LivePreviewCapturePacket Packet, T Image, bool HasImage, Exception? Failure = null);
    private readonly object gate = new();
    private readonly LatestFrameMailbox<Input> input = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<LivePreviewCapturePacket, CancellationToken, Task<T>> decode;
    private readonly Action<Action> postToUi;
    private readonly Action<Guid, LivePreviewCapturePacket, T> present;
    private readonly Func<Guid, bool> isCurrent;
    private readonly Action<T> release;
    private readonly Action<Exception>? failed;
    private Output? pending;
    private Guid acceptedEpoch, failedEpoch;
    private long acceptedSequence;
    private bool uiScheduled, disposed;
    public LivePreviewPresenter(Func<LivePreviewCapturePacket, CancellationToken, Task<T>> decode, Action<Action> postToUi,
        Action<Guid, LivePreviewCapturePacket, T> present, Func<Guid, bool> isCurrent, Action<T>? release = null,
        Action<Exception>? failed = null)
    {
        this.decode = decode ?? throw new ArgumentNullException(nameof(decode));
        this.postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        this.present = present ?? throw new ArgumentNullException(nameof(present));
        this.isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        this.release = release ?? (image => { if (image is IDisposable resource) resource.Dispose(); });
        this.failed = failed;
        _ = Task.Run(DecodeLoopAsync);
    }
    public void Push(Guid epoch, LivePreviewCapturePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (epoch == Guid.Empty || packet.Kind != LivePreviewCaptureKind.Image || packet.Sequence <= 0 ||
            packet.Width is < 1 or > 1920 || packet.Height is < 1 or > 1080 ||
            packet.JpegBytes is not { Length: > 0 and <= LivePreviewCaptureOptions.MaximumFrameBytes }) return;
        lock (gate)
        {
            if (disposed || failedEpoch == epoch || !Current(epoch)) return;
            if (acceptedEpoch != epoch) { acceptedEpoch = epoch; acceptedSequence = 0; }
            if (packet.Sequence <= acceptedSequence) return;
            acceptedSequence = packet.Sequence;
            // The sequence check and publication share the lock, so a delayed older Push cannot replace a newer input.
            input.Publish(new(epoch, packet));
        }
    }
    private bool Current(Guid epoch)
    {
        try { return isCurrent(epoch); }
        catch { return false; }
    }
    private bool Eligible(Guid epoch, bool allowFailure = false)
    {
        lock (gate) return !disposed && acceptedEpoch == epoch && (allowFailure || failedEpoch != epoch) && Current(epoch);
    }
    private void Release(Output? output)
    {
        if (output?.HasImage != true) return;
        try { release(output.Image); }
        catch (Exception error) { Trace.TraceError("Live preview image release failed ({0}).", error.GetType().Name); }
    }
    public ValueTask DisposeAsync()
    {
        Output? owned;
        lock (gate)
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true; owned = pending; pending = null;
        }
        try { lifetime.Cancel(); }
        catch (Exception error) { Trace.TraceError("Live preview cancellation failed ({0}).", error.GetType().Name); }
        finally { input.Dispose(); Release(owned); }
        // UI callbacks may never run during window shutdown. A late decoder retains responsibility for its result.
        return ValueTask.CompletedTask;
    }
}
