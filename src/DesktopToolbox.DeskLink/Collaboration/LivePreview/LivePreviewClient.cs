namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

/// <summary>Mac-initiated media connections; cancellation closes them and never reconnects automatically.</summary>
public sealed class LivePreviewClient
{
    private readonly CollaborationClient owner;
    private readonly CancellationTokenSource lifetime = new();
    internal LivePreviewClient(CollaborationClient owner) => this.owner = owner;
    internal void Stop() => lifetime.Cancel();
    public async Task ReceiveAsync(Guid streamId, LivePreviewCaptureOptions options,
        Action<Guid, LivePreviewCapturePacket> onPacket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(onPacket); options.Validate();
        if (streamId == Guid.Empty) throw new ArgumentException("A live stream generation is required.", nameof(streamId));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var connection = await owner.OpenLiveAsync("preview-live-subscribe", new(streamId, options), stop.Token);
        using var socket = connection.Socket; await using var stream = connection.Secure;
        await LivePreviewDuplex.ReceiveAsync(stream, streamId, options, packet => onPacket(streamId, packet), stop.Token);
    }
    public async Task UploadAsync(LivePreviewDemand demand, ILivePreviewCaptureProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demand); ArgumentNullException.ThrowIfNull(demand.Options); ArgumentNullException.ThrowIfNull(provider);
        demand.Options.Validate();
        if (demand.StreamId == Guid.Empty) throw new ArgumentException("A live stream generation is required.", nameof(demand));
        if (!provider.IsSupported) throw new NotSupportedException("Live capture is unavailable.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var connection = await owner.OpenLiveAsync("preview-live-upload", demand, stop.Token);
        using var socket = connection.Socket; await using var stream = connection.Secure;
        await LivePreviewDuplex.SendAsync(stream, demand.StreamId, demand.Options, provider, stop.Token);
    }
}
