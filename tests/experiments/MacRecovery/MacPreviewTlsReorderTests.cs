using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed partial class MacCollaborationIntegrationTests
{
    [Fact]
    public async Task ViewingReadCannotProvePreviouslySentTlsBeginWillNotExecute()
    {
        using var folder = new RecoveryDirectory();
        await using var server = new ReorderingTlsControlServer();
        server.Start();
        using var ipcListener = new TcpListener(IPAddress.Loopback, 0);
        ipcListener.Start();
        using var ipcSocket = new TcpClient();
        var connect = ipcSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)ipcListener.LocalEndpoint).Port);
        using var ipcPeer = await ipcListener.AcceptTcpClientAsync();
        await connect;
        var process = new RecoveryProcess();
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult<Stream>(ipcSocket.GetStream())
        };
        await using var owner = await MacMultiPeerClient.OpenAsync(
            Path.Combine(folder.Path, "mac"), Path.Combine(folder.Path, "engine"), new RecoveryIdentityVault(), dependencies);
        var invitation = new MultiPeerInvitation
        {
            Host = "127.0.0.1", Port = server.Port, HostName = "host", PeerName = owner.DeviceName,
            CertificateSha256 = server.Fingerprint, Token = server.Token
        };
        var preview = await owner.ReviewInvitationAsync(invitation.ToJson());
        await owner.ConfirmInvitationAsync(preview.Id, server.Fingerprint);
        await owner.StartAsync();
        var lease = Assert.IsType<MacMultiPeerClient.CollaborationLease>(await owner.GetCollaborationLeaseAsync());
        await using var client = new MultiPeerControlClient(invitation, true);
        var control = new MacMultiPreviewControl(lease, new TlsPreviewBackend(client));
        try
        {
            var begin = control.BeginPreviewAsync();
            await server.BeginReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<IOException>(() => begin.WaitAsync(TimeSpan.FromSeconds(10)));
            var drain = lease.Suspend();
            Assert.False(drain.IsCompleted);
            var state = await control.ReadPreviewAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DeskflowPreviewPhase.Viewing, state.Phase);
            var drainedAfterRead = drain.IsCompleted;
            server.ReleaseBegin.TrySetResult();
            await server.BeginApplied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(server.Active);
            Assert.False(drainedAfterRead); // The prior request can still change focus after Read.
        }
        finally
        {
            server.ReleaseBegin.TrySetResult();
            // Isolated fixture cleanup only: production must never forge a return proof.
            var pending = typeof(MacMultiPreviewControl).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control);
            foreach (var item in (System.Collections.IEnumerable)pending!)
            {
                var operation = item!.GetType().GetField("Operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item);
                ((IDisposable)operation!).Dispose();
            }
            process.Exit();
        }
    }

    private sealed class TlsPreviewBackend(MultiPeerControlClient client) : IDeskflowPreviewControl
    {
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "return";
        public string PreviewHandoffHint => "synthetic";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken token = default) => client.BeginPreviewAsync(token);
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken token = default) => client.RenewPreviewAsync(id, token);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken token = default) => client.EndPreviewAsync(id, token);
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken token = default) => client.ReadPreviewAsync(token);
    }

    private sealed class ReorderingTlsControlServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly X509Certificate2 certificate;
        private readonly List<Task> connections = [];
        private Task? accepting;
        private int active;
        private int reads;
        private int begins;
        private int switches;
        private readonly string sessionId = Guid.NewGuid().ToString("N");
        public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string Fingerprint => Convert.ToHexString(SHA256.HashData(certificate.RawData));
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public bool Active => Volatile.Read(ref active) != 0;
        public int BeginRequests => Volatile.Read(ref begins);
        public int SwitchRequests => Volatile.Read(ref switches);
        public bool HoldBeginResponse { get; set; }
        public bool ImmediateBegin { get; set; }
        public bool HoldEndResponse { get; set; }
        public bool ReadAsViewing { get; set; }
        public bool FailFirstRead { get; set; }
        public bool PauseReadResponse { get; set; }
        public TaskCompletionSource BeginReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBegin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BeginApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeferredBeginApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EndReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EndApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReorderingTlsControlServer()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=nian-desklink", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var pfx = generated.Export(X509ContentType.Pkcs12);
            try { certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        public void Start() { listener.Start(); accepting = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    var socket = await listener.AcceptTcpClientAsync(stopping.Token);
                    var task = HandleAsync(socket);
                    lock (connections) connections.Add(task);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task HandleAsync(TcpClient socket)
        {
            try
            {
                using (socket)
                await using (var tls = new SslStream(socket.GetStream(), false))
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, stopping.Token);
                    var frame = await JsonLineProtocol.ReadAsync(tls, stopping.Token);
                    using var request = JsonDocument.Parse(frame!);
                    var root = request.RootElement;
                    Assert.Equal(Token, root.GetProperty("token").GetString());
                    var action = root.GetProperty("action").GetString();
                    if (action == "preview-begin")
                    {
                        Interlocked.Increment(ref begins);
                        if (ImmediateBegin)
                        {
                            Volatile.Write(ref active, 1); BeginReceived.TrySetResult(); BeginApplied.TrySetResult();
                            var beginState = new DeskflowPreviewState { Id = sessionId, Phase = DeskflowPreviewPhase.Active, ViewerComputer = "host", TargetComputer = root.GetProperty("peer").GetString()! };
                            await JsonLineProtocol.WriteAsync(tls, new { version = 1, requestId = root.GetProperty("requestId").GetString(), ok = true, preview = beginState }, stopping.Token);
                            return;
                        }
                        // The TLS request has arrived, but this controlled server
                        // delays its dispatch and drops the response connection.
                        if (!HoldBeginResponse) socket.Dispose();
                        BeginReceived.TrySetResult();
                        await ReleaseBegin.Task.WaitAsync(stopping.Token);
                        Volatile.Write(ref active, 1);
                        BeginApplied.TrySetResult();
                        DeferredBeginApplied.TrySetResult();
                        return;
                    }
                    if (action == "switch")
                    {
                        Interlocked.Increment(ref switches);
                        await JsonLineProtocol.WriteAsync(tls, new { version = 1, requestId = root.GetProperty("requestId").GetString(), ok = true }, stopping.Token);
                        return;
                    }
                    if (action == "preview-read")
                    {
                        if (Interlocked.Increment(ref reads) == 1 && FailFirstRead) { socket.Dispose(); return; }
                        ReadReceived.TrySetResult();
                        if (PauseReadResponse) await ReleaseRead.Task.WaitAsync(stopping.Token);
                    }
                    var state = Active && !(action == "preview-read" && ReadAsViewing)
                        ? new DeskflowPreviewState { Id = sessionId, Phase = DeskflowPreviewPhase.Active, ViewerComputer = "host", TargetComputer = root.GetProperty("peer").GetString()! }
                        : new DeskflowPreviewState { Phase = DeskflowPreviewPhase.Viewing };
                    if (action == "preview-end")
                    {
                        EndReceived.TrySetResult();
                        if (HoldEndResponse) await ReleaseEnd.Task.WaitAsync(stopping.Token);
                        Volatile.Write(ref active, 0); EndApplied.TrySetResult(); state = new() { Phase = DeskflowPreviewPhase.Viewing };
                    }
                    await JsonLineProtocol.WriteAsync(tls, new { version = 1, requestId = root.GetProperty("requestId").GetString(), ok = true, preview = state }, stopping.Token);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            stopping.Cancel(); listener.Stop(); ReleaseBegin.TrySetResult(); ReleaseRead.TrySetResult(); ReleaseEnd.TrySetResult();
            if (accepting is not null) await accepting;
            Task[] tasks; lock (connections) tasks = connections.ToArray();
            await Task.WhenAll(tasks); certificate.Dispose(); stopping.Dispose();
        }
    }
}
