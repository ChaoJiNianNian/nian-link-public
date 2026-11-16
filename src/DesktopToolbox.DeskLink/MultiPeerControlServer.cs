using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
#if NIAN_FENCE_RESEARCH
using System.Security.Cryptography;
using System.Reflection;
#endif
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
#if NIAN_FENCE_RESEARCH
using System.Text.Json.Serialization;
#endif
using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.DeskLink;

/// <summary>
/// Isolated multi-peer switch endpoint. It is not connected to the product UI,
/// data-sharing service or physical input engine. Caller must verify each peer's
/// fingerprint before registering its independently generated credential.
/// </summary>
public sealed partial class MultiPeerControlServer : IAsyncDisposable
{
#if NIAN_FENCE_RESEARCH
    private sealed record Request(int Version, string RequestId, string Peer, string Token, string Action, string? Target, string? PreviewId = null,
        string? OperationId = null, string? Incarnation = null, long? ExpectedRevision = null)
    {
        public override string ToString() => "[redacted research request]";
    }
    private sealed record Response(int Version, string RequestId, bool Ok, string? Error = null, DeskflowPreviewState? Preview = null,
        DeskLinkSwitchResult? Switch = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Incarnation = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FenceResult = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? KeyboardRevision = null);
#else
    private sealed record Request(int Version, string RequestId, string Peer, string Token, string Action, string? Target, string? PreviewId = null);
    private sealed record Response(int Version, string RequestId, bool Ok, string? Error = null, DeskflowPreviewState? Preview = null,
        DeskLinkSwitchResult? Switch = null);
#endif
    private readonly string profile;
    private readonly IPAddress bindAddress;
    private readonly VerifiedLanBinding? lanBinding;
    private readonly int requestedPort;
    private readonly string hostName;
    private readonly IMultiDeviceAuthorizer credentials;
    private readonly Func<CancellationToken, Task<DeskLinkState>> getState;
    private readonly Func<string, string, long, CancellationToken, Task<DeskLinkSwitchResult>> switchComputer;
    private readonly MultiPeerPreviewManager? preview;
#if NIAN_FENCE_RESEARCH
    // Deliberately available only from the internal loopback research factory.
    private MultiDeviceCredentialVault? researchVault;
    private FenceResearchState? researchFence;
#endif
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim switchGate = new(1, 1);
    private readonly SemaphoreSlim clientSlots = new(8, 8);
    private readonly ConcurrentDictionary<int, TaskCompletionSource> clients = new();
    private CancellationTokenSource? running;
    private TcpListener? listener;
    private X509Certificate2? certificate;
    private Task accepting = Task.CompletedTask;
    private int nextClient;
    private bool disposed;

    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? requestedPort;
    public string? CertificateSha256 => certificate is null ? null : DeskLinkFingerprint.OfCertificate(certificate);

    public MultiPeerControlServer(string profile, IPAddress bindAddress, int port, string hostName,
        IMultiDeviceAuthorizer credentials, Func<CancellationToken, Task<DeskLinkState>> getState,
        Func<string, string, long, CancellationToken, Task<DeskLinkSwitchResult>> switchComputer, MultiPeerPreviewManager? preview = null)
        : this(profile, bindAddress, port, hostName, credentials, getState, switchComputer, null, preview) { }

    internal static MultiPeerControlServer CreateForLan(string profile, VerifiedLanBinding binding, int port, string hostName,
        IMultiDeviceAuthorizer credentials, Func<CancellationToken, Task<DeskLinkState>> getState,
        Func<string, string, long, CancellationToken, Task<DeskLinkSwitchResult>> switchComputer, MultiPeerPreviewManager? preview = null) =>
        new(profile, binding.Address, port, hostName, credentials, getState, switchComputer, binding, preview);

#if NIAN_FENCE_RESEARCH
    internal static MultiPeerControlServer CreateForFenceResearch(string hostName, MultiDeviceCredentialVault vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var root = typeof(MultiPeerControlServer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "NianFenceResearchRoot").Value ?? throw new InvalidOperationException("Missing isolated research root.");
        root = Path.GetFullPath(root);
        var directory = Path.GetFullPath(vault.FenceResearchDirectory);
        var relative = Path.GetRelativePath(root, directory);
        if (relative == "." || Path.IsPathFullyQualified(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Research vault must be inside its isolated directory.", nameof(vault));
        RejectLinks(root); RejectLinks(directory);
        // No callback, runtime, or LAN binding can be supplied to this research endpoint.
        var server = new MultiPeerControlServer(directory, IPAddress.Loopback, 0, hostName, vault,
            _ => throw new InvalidOperationException("Research mode has no keyboard."),
            (_, _, _, _) => throw new InvalidOperationException("Research mode has no keyboard."));
        server.researchVault = vault;
        server.researchFence = new FenceResearchState(64);
        return server;
    }
#endif

    private MultiPeerControlServer(string profile, IPAddress bindAddress, int port, string hostName,
        IMultiDeviceAuthorizer credentials, Func<CancellationToken, Task<DeskLinkState>> getState,
        Func<string, string, long, CancellationToken, Task<DeskLinkSwitchResult>> switchComputer, VerifiedLanBinding? binding,
        MultiPeerPreviewManager? preview)
    {
        if (!Path.IsPathFullyQualified(profile)) throw new ArgumentException("Profile path must be absolute.", nameof(profile));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        DeskflowProfile.ValidateName(hostName);
        this.profile = profile;
        ArgumentNullException.ThrowIfNull(bindAddress);
        // The prototype has not completed identity binding or native revision
        // synchronization, so it must never listen on a real network yet.
        if (!IPAddress.IsLoopback(bindAddress) && (binding is null || !binding.Address.Equals(bindAddress)))
            throw new ArgumentException("A verified LAN interface is required.", nameof(bindAddress));
        this.bindAddress = bindAddress;
        lanBinding = binding;
        requestedPort = port;
        this.hostName = hostName;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.getState = getState ?? throw new ArgumentNullException(nameof(getState));
        this.switchComputer = switchComputer ?? throw new ArgumentNullException(nameof(switchComputer));
        this.preview = preview;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running is not null) return;
            if (lanBinding is not null && !lanBinding.IsStillAvailable())
                throw new InvalidOperationException("The approved LAN interface is unavailable.");
#if NIAN_FENCE_RESEARCH
            if (researchFence is not null)
            {
                using var key = RSA.Create(2048);
                var certificateRequest = new CertificateRequest("CN=nian-research", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var temporary = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
                var data = temporary.Export(X509ContentType.Pkcs12);
                // Schannel cannot use ephemeral keys; the OS manages this temporary user key.
                try { certificate = X509CertificateLoader.LoadPkcs12(data, null, X509KeyStorageFlags.UserKeySet); }
                finally { CryptographicOperations.ZeroMemory(data); }
                listener = new TcpListener(bindAddress, requestedPort);
                listener.Start(8);
                running = new CancellationTokenSource();
                accepting = AcceptAsync(running.Token);
                return;
            }
#endif
            RejectLinks(profile);
            PrivateFiles.EnsureDirectory(profile);
            var path = Path.Combine(profile, "multi-control.pem");
            var leasePath = Path.Combine(profile, ".identity.lock");
            RejectLinks(path); RejectLinks(leasePath);
            using var identityLease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await PrivateFiles.EnsureCertificateAsync(path, ct);
            certificate = PrivateFiles.LoadTlsCertificate(path);
            listener = new TcpListener(bindAddress, requestedPort);
            listener.Start(8);
            running = new CancellationTokenSource();
            accepting = AcceptAsync(running.Token);
        }
        catch
        {
            listener?.Stop(); listener = null;
            certificate?.Dispose(); certificate = null;
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Control identity cannot use linked paths."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    // Await any accepted switch before revoking, so a completed revocation
    // guarantees this endpoint will not start another switch for the old token.
    public async Task<bool> RevokeAsync(string peer, CancellationToken ct = default)
    {
        await switchGate.WaitAsync(ct);
        try { return await credentials.RevokeAsync(peer, ct); }
        finally { switchGate.Release(); }
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener!.AcceptTcpClientAsync(ct);
                if (!await clientSlots.WaitAsync(0, ct)) { socket.Dispose(); continue; }
                var id = Interlocked.Increment(ref nextClient);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                clients[id] = completion;
                _ = HandleConnectionAsync(id, socket, completion, ct);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested) throw;
        }
    }

    private async Task HandleConnectionAsync(int id, TcpClient socket, TaskCompletionSource completion, CancellationToken stopping)
    {
        try
        {
            using (socket)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                await using var tls = new SslStream(socket.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption
                }, deadline.Token);
                var frame = await JsonLineProtocol.ReadAsync(tls, deadline.Token);
                if (frame is null) return;
                var request = JsonSerializer.Deserialize<Request>(frame, JsonLineProtocol.Json);
#if NIAN_FENCE_RESEARCH
                if (request is null || (researchFence is null ? request.Version != 1 : request.Version is not (1 or 2)) || request.RequestId?.Length != 32 ||
                    request.RequestId.Any(c => !Uri.IsHexDigit(c))) return;
                var response = emergencyResearchManager is not null ? await DispatchEmergencyResearchAsync(request, deadline.Token)
                    : researchFence is null ? await DispatchAsync(request, deadline.Token)
                    : await DispatchFenceResearchAsync(request, deadline.Token);
#else
                if (request is null || request.Version != 1 || request.RequestId?.Length != 32 ||
                    request.RequestId.Any(c => !Uri.IsHexDigit(c))) return;
                var response = await DispatchAsync(request, deadline.Token);
#endif
                await JsonLineProtocol.WriteAsync(tls, response, deadline.Token);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or
            OperationCanceledException or JsonException or ArgumentException or InvalidOperationException or ObjectDisposedException) { }
        finally
        {
            clients.TryRemove(id, out _);
            clientSlots.Release();
            completion.TrySetResult();
        }
    }

#if NIAN_FENCE_RESEARCH
    private async Task<Response> DispatchFenceResearchAsync(Request request, CancellationToken ct)
    {
        if (request.Version != 2) return new(request.Version, request.RequestId, false, "research mode rejects legacy control");
        if (researchVault is null || researchFence is null || string.IsNullOrEmpty(request.Peer) ||
            string.Equals(request.Peer, hostName, StringComparison.OrdinalIgnoreCase) || request.Token is not { Length: 44 } ||
            request.Target != hostName || request.Action is not ("probe" or "seal" or "begin"))
            return new(2, request.RequestId, false, "unauthorized or invalid");
        byte[] token;
        try { token = Convert.FromBase64String(request.Token); }
        catch (FormatException) { return new(2, request.RequestId, false, "unauthorized or invalid"); }
        try
        {
            if (token.Length != 32 || Convert.ToBase64String(token) != request.Token)
                return new(2, request.RequestId, false, "unauthorized or invalid");
            // Token comparison and the small in-memory transition are linearized
            // with the vault's own revoke/add lock; no I/O occurs inside the callback.
            Response? response = null;
            var authorized = await researchVault.TryCommitUniqueResearchAuthorizedAsync(request.Peer, token, identity =>
            {
                var result = researchFence.Apply(request.Action, identity, token, request.Target,
                    request.OperationId, request.Incarnation);
                response = new Response(2, request.RequestId, true, Incarnation: researchFence.Incarnation, FenceResult: result);
            }, ct).ConfigureAwait(false);
            return authorized == ResearchGrantAuthorization.Committed ? response! : new(2, request.RequestId, false, "unauthorized or invalid");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(token); }
    }
#endif

    private async Task<Response> DispatchAsync(Request request, CancellationToken ct)
    {
        await switchGate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrEmpty(request.Peer) || string.Equals(request.Peer, hostName, StringComparison.OrdinalIgnoreCase) ||
                lanBinding is not null && !lanBinding.IsStillAvailable() ||
                !credentials.IsAuthorized(request.Peer, request.Token))
                return new(1, request.RequestId, false, "unauthorized");
            if (string.IsNullOrEmpty(request.Action)) return new(1, request.RequestId, false, "unsupported action or target");
            if (request.Action.StartsWith("preview-", StringComparison.Ordinal))
            {
                if (preview is null) return new(1, request.RequestId, false, "preview unavailable");
                try
                {
                    var previewResult = request.Action switch
                    {
                        "preview-begin" when request.Target == hostName => await preview.BeginAsync(request.Peer, request.Peer, ct),
                        "preview-renew" when request.PreviewId is not null => await preview.RenewAsync(request.Peer, request.PreviewId, ct),
                        "preview-end" when request.PreviewId is not null => await preview.EndAsync(request.Peer, request.PreviewId, ct),
                        "preview-read" => await preview.ReadForDeviceAsync(request.Peer, ct),
                        _ => throw new ArgumentException("Unsupported preview request.")
                    };
                    return new(1, request.RequestId, true, Preview: previewResult);
                }
                catch (UnauthorizedAccessException) { return new(1, request.RequestId, false, "preview unauthorized"); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return new(1, request.RequestId, false, "preview unavailable"); }
            }
            if (request.Action != "switch" || request.Target is null ||
                !(string.Equals(request.Target, hostName, StringComparison.Ordinal) ||
                  string.Equals(request.Target, request.Peer, StringComparison.Ordinal)))
                return new(1, request.RequestId, false, "unsupported action or target");
            if (preview is not null)
            {
                try
                {
                    var guarded = await preview.SwitchNormalAsync(request.Peer, request.Target, ct);
                    var transferred = guarded.InputTransferred && string.Equals(guarded.ActiveComputer, request.Target, StringComparison.Ordinal);
                    var visibleActive = guarded.ActiveComputer == hostName || guarded.ActiveComputer == request.Peer ? guarded.ActiveComputer : null;
                    return new(1, request.RequestId, transferred, transferred ? null : "switch not confirmed",
                        Switch: guarded with { Target = request.Target, InputTransferred = transferred, ActiveComputer = visibleActive,
                            Error = guarded.VideoStatus switch
                            {
                                "acceptedUnverified" => "显示器已收到命令，请确认画面。",
                                "failed" => "显示器切换未确认，请检查画面。",
                                _ => transferred ? null : "键鼠状态已变化，请重试。"
                            } });
                }
                catch (Exception error) when (error is UnauthorizedAccessException or InvalidOperationException)
                { return new(1, request.RequestId, false, "preview busy"); }
            }
            var state = await getState(ct);
            if (state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable || state.Revision < 0 ||
                !(string.Equals(state.ActiveComputer, hostName, StringComparison.Ordinal) ||
                  string.Equals(state.ActiveComputer, request.Peer, StringComparison.Ordinal)) ||
                !state.ConnectedComputers.Contains(request.Peer, StringComparer.Ordinal))
                return new(1, request.RequestId, false, "keyboard unavailable or controlled by another device");
            // The callback must use an atomic native compare-and-switch, not an
            // ordinary switch after a separate state read.
            var result = await switchComputer(request.Target, state.ActiveComputer!, state.Revision, ct);
            var confirmed = result.Ok && result.InputTransferred &&
                string.Equals(result.ActiveComputer, request.Target, StringComparison.Ordinal);
            return new(1, request.RequestId, confirmed, confirmed ? null : "switch not confirmed");
        }
        finally { switchGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            running?.Cancel();
            listener?.Stop();
            try { await accepting; } catch (OperationCanceledException) { }
            await Task.WhenAll(clients.Values.Select(completion => completion.Task));
#if NIAN_FENCE_RESEARCH
            researchFence?.Dispose();
#endif
            listener = null;
            running?.Dispose(); running = null;
            certificate?.Dispose(); certificate = null;
        }
        finally { lifecycle.Release(); }
    }
}
