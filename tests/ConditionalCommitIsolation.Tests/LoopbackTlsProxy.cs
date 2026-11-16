using System.Net;
using System.Net.Sockets;

namespace ConditionalCommitIsolation.Tests;

// Raw TCP only: never decrypts, inspects, logs, or persists TLS payload bytes.
internal sealed class LoopbackTlsProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopped = new();
    private readonly TaskCompletionSource dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task run;
    private TcpClient? peerSide, hostSide;
    private int dropPeer;
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal Task Dropped => dropped.Task;

    internal LoopbackTlsProxy(int hostPort)
    {
        listener.Start(1); run = RunAsync(hostPort);
    }

    internal void DropPeerToHost() => Volatile.Write(ref dropPeer, 1);
    internal void CutConnections() { stopped.Cancel(); peerSide?.Dispose(); hostSide?.Dispose(); listener.Stop(); }

    private async Task RunAsync(int hostPort)
    {
        try
        {
            peerSide = await listener.AcceptTcpClientAsync(stopped.Token);
            hostSide = new TcpClient(AddressFamily.InterNetwork);
            await hostSide.ConnectAsync(IPAddress.Loopback, hostPort, stopped.Token);
            await Task.WhenAll(PumpAsync(peerSide.GetStream(), hostSide.GetStream(), true), PumpAsync(hostSide.GetStream(), peerSide.GetStream(), false));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task PumpAsync(Stream input, Stream output, bool peerToHost)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer, stopped.Token);
                if (count == 0) return;
                if (peerToHost && Volatile.Read(ref dropPeer) != 0) { dropped.TrySetResult(); continue; }
                await output.WriteAsync(buffer.AsMemory(0, count), stopped.Token);
            }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
    }

    public async ValueTask DisposeAsync()
    {
        CutConnections(); await run.WaitAsync(TimeSpan.FromSeconds(5)); stopped.Dispose();
    }
}
