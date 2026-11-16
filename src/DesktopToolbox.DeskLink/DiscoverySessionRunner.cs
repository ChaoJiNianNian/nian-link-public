using System.Net;

namespace DesktopToolbox.DeskLink;

internal interface IDiscoveryDatagramTransport : IDisposable
{
    Task SendQueryAsync(byte[] data, int port, CancellationToken ct);
    Task SendReplyAsync(byte[] data, IPEndPoint destination, CancellationToken ct);
    Task<DiscoveryDatagram> ReceiveAsync(CancellationToken ct);
}

/// <summary>Runs one bounded scan or offer. Disposal and state invalidation happen on every exit.</summary>
internal static class DiscoverySessionRunner
{
    internal static async Task RunScannerAsync(IDiscoveryDatagramTransport transport, DiscoveryScanState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(state);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var remaining = state.Remaining();
            if (remaining <= TimeSpan.Zero) return;
            lifetime.CancelAfter(remaining);
            await transport.SendQueryAsync(state.CreateQuery(), state.ExpectedPort, lifetime.Token);
            while (true)
            {
                var packet = await transport.ReceiveAsync(lifetime.Token);
                state.Observe(packet.Data, packet.Source, packet.Destination, packet.InterfaceIndex);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { state.Stop(); transport.Dispose(); }
    }

    internal static async Task RunResponderAsync(IDiscoveryDatagramTransport transport, DiscoveryResponderState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(state);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var remaining = state.Remaining();
            if (remaining <= TimeSpan.Zero) return;
            lifetime.CancelAfter(remaining);
            while (true)
            {
                var packet = await transport.ReceiveAsync(lifetime.Token);
                var response = state.Reply(packet.Data, packet.Source, packet.Destination, packet.InterfaceIndex);
                if (response is not null) await transport.SendReplyAsync(response, packet.Source, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { state.Stop(); transport.Dispose(); }
    }
}
