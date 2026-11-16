using DesktopToolbox.DeskLink.Collaboration.LivePreview;
namespace DesktopToolbox.DeskLink.Preview;

/// <summary>One viewer owns each temporary operation epoch. It never persists the passive cadence.</summary>
public interface IControlLivePreviewSource : IAsyncDisposable
{
    bool IsSupported { get; }
    string UnavailableHint { get; }
    event Action<Guid, LivePreviewCapturePacket>? PacketReceived;
    event Action<Guid, string>? StreamClosed;
    Task StartAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task StopAsync(Guid operationId, CancellationToken cancellationToken = default);
}
