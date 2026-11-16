namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>One reader and one writer per TLS connection. ACKs measure connection liveness, never source health.</summary>
internal static class LivePreviewDuplex
{
    private static readonly TimeSpan IoBound = TimeSpan.FromSeconds(3);

    internal static async Task SendAsync(Stream stream, Guid id, LivePreviewCaptureOptions options,
        ILivePreviewCaptureProvider source, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var mailbox = new LivePreviewOutboundMailbox();
        async Task Produce()
        {
            var terminal = false;
            await foreach (var packet in source.CaptureAsync(options, stop.Token).WithCancellation(stop.Token))
            {
                if (!mailbox.Publish(packet)) break;
                if (packet.Kind == LivePreviewCaptureKind.Unavailable) { terminal = true; break; }
            }
            if (!terminal) throw new IOException("The live capture source ended.");
            // Completion of the native source must not race the writer: it owns the
            // terminal packet until the bounded write has finished or is cancelled.
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        }
        async Task Write()
        {
            while (true)
            {
                LivePreviewCapturePacket packet;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    timeout.CancelAfter(IoBound);
                    try { packet = await mailbox.ReadAsync(timeout.Token); }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { throw new IOException("The live capture source timed out."); }
                }
                ValidateBounds(packet, options);
                await Bounded(async token => { await LivePreviewFrameProtocol.WriteAsync(stream, id, packet, token); await stream.FlushAsync(token); }, stop.Token);
                if (packet.Kind == LivePreviewCaptureKind.Unavailable) throw new IOException("The live capture source is unavailable.");
            }
        }
        async Task ReadAcks()
        {
            var reader = new LivePreviewFrameReader(stream, id);
            while (true)
            {
                var packet = await Bounded(token => reader.ReadAsync(token).AsTask(), stop.Token);
                if (packet.Kind != LivePreviewCaptureKind.HealthyIdle) throw new InvalidDataException("Invalid live connection acknowledgement.");
            }
        }
        var tasks = new[] { Produce(), Write(), ReadAcks() };
        try { await await Task.WhenAny(tasks); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(tasks); } catch { /* preserve the first terminating task */ }
        }
    }

    internal static async Task ReceiveAsync(Stream stream, Guid id, LivePreviewCaptureOptions options, Action<LivePreviewCapturePacket> onPacket, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Read()
        {
            var reader = new LivePreviewFrameReader(stream, id);
            while (true)
            {
                var packet = await Bounded(token => reader.ReadAsync(token).AsTask(), stop.Token);
                ValidateBounds(packet, options);
                onPacket(packet);
                if (packet.Kind == LivePreviewCaptureKind.Unavailable) throw new IOException("The live capture source is unavailable.");
            }
        }
        async Task Acknowledge()
        {
            long sequence = 0;
            while (true)
            {
                await Bounded(async token =>
                {
                    await LivePreviewFrameProtocol.WriteAsync(stream, id, new() { Kind = LivePreviewCaptureKind.HealthyIdle, Sequence = ++sequence }, token);
                    await stream.FlushAsync(token);
                }, stop.Token);
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
            }
        }
        var tasks = new[] { Read(), Acknowledge() };
        try { await await Task.WhenAny(tasks); }
        finally { stop.Cancel(); try { await Task.WhenAll(tasks); } catch { } }
    }

    private static void ValidateBounds(LivePreviewCapturePacket packet, LivePreviewCaptureOptions options)
    {
        if (packet.Kind == LivePreviewCaptureKind.Image && (packet.Width > options.MaxWidth || packet.Height > options.MaxHeight))
            throw new InvalidDataException("Live media exceeds the negotiated image bounds.");
    }

    private static async Task Bounded(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(IoBound);
        try { await operation(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Live media I/O timed out."); }
    }
    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(IoBound);
        try { return await operation(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Live media I/O timed out."); }
    }
}
