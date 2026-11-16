using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
namespace DesktopToolbox.DeskLink;

/// <summary>TLS-only paired state/switch endpoint; never accepts programs, paths or shell commands.</summary>
public sealed class PairedControlServer : IAsyncDisposable
{
    private readonly PairedControlServerOptions options;
    private readonly Func<CancellationToken,Task<DeskLinkState>> getState;
    private readonly Func<string,CancellationToken,Task<DeskLinkSwitchResult>> switchComputer;
    private readonly IDeskflowPreviewControl? previewControl;
    private readonly Action? previewClientObserved;
    private readonly HashSet<string> targets;
    private readonly SemaphoreSlim lifecycle = new(1,1);
    private readonly SemaphoreSlim clientsLimit = new(8,8);
    private readonly ConcurrentDictionary<int,TaskCompletionSource> clients = new();
    private CancellationTokenSource? running;
    private TcpListener? listener;
    private X509Certificate2? certificate;
    private byte[]? tokenBytes;
    private Task accepting = Task.CompletedTask;
    private int connectionId;
    public event EventHandler<Exception>? PeerError;
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? options.Port;
    public string? CertificateSha256 => certificate is null ? null : DeskLinkFingerprint.OfCertificate(certificate);
    public PairedControlServer(PairedControlServerOptions options, Func<CancellationToken,Task<DeskLinkState>> getState, Func<string,CancellationToken,Task<DeskLinkSwitchResult>> switchComputer, IDeskflowPreviewControl? previewControl = null, Action? previewClientObserved = null)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(getState); ArgumentNullException.ThrowIfNull(switchComputer);
        if (!Path.IsPathFullyQualified(options.ProfileDirectory) || options.Port is < 0 or > 65535) throw new ArgumentException("Invalid paired control options.");
        targets = new(options.AllowedTargets, StringComparer.Ordinal);
        if (targets.Count != 2) throw new ArgumentException("Paired control requires exactly two configured targets.");
        foreach (var target in targets) DeskflowProfile.ValidateName(target);
        this.options = options; this.getState = getState; this.switchComputer = switchComputer;
        this.previewControl = previewControl;
        this.previewClientObserved = previewClientObserved;
    }
    public async Task StartAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if (running is not null) return;
            PrivateFiles.EnsureDirectory(options.ProfileDirectory);
            var path = Path.Combine(options.ProfileDirectory,"control.pem");
            await PrivateFiles.EnsureCertificateAsync(path,ct);
            certificate = PrivateFiles.LoadTlsCertificate(path);
            var tokenPath = Path.Combine(options.ProfileDirectory,"pair-token");
            if (!File.Exists(tokenPath)) await PrivateFiles.WriteAsync(tokenPath,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),ct);
            var tokenText = await File.ReadAllTextAsync(tokenPath,ct);
            try { tokenBytes = Convert.FromBase64String(tokenText.Trim()); }
            catch (FormatException) { throw new InvalidDataException("Stored pairing token is invalid."); }
            if (tokenBytes.Length != 32) throw new InvalidDataException("Stored pairing token must contain 256 bits.");
            listener = new TcpListener(options.BindAddress,options.Port);
            listener.Start(8); running = new(); accepting = AcceptAsync(running.Token);
        }
        catch { listener?.Stop(); listener = null; certificate?.Dispose(); certificate = null; throw; }
        finally { lifecycle.Release(); }
    }
    public DeskLinkPairingBundle CreatePairingBundle(string host, string windowsName, string macName) =>
        CreatePairingBundle(host, windowsName, macName, null);
    public DeskLinkPairingBundle CreatePairingBundle(string host, string windowsName, string macName, DeskLinkDisplayBinding? display)
    {
        if (running is null || certificate is null || tokenBytes is null) throw new InvalidOperationException("Start the paired control endpoint before exporting pairing.");
        if (!targets.SetEquals(new[] { windowsName, macName })) throw new ArgumentException("Pairing names do not match the configured targets.");
        var bundle = new DeskLinkPairingBundle { Host=host, Port=Port, CertificateSha256=DeskLinkFingerprint.OfCertificate(certificate), Token=Convert.ToBase64String(tokenBytes), WindowsName=windowsName, MacName=macName, Display=display };
        bundle.Validate(); return bundle;
    }
    public async Task RegeneratePairingTokenAsync(CancellationToken ct = default)
    {
        var replacement = RandomNumberGenerator.GetBytes(32);
        await PrivateFiles.WriteAsync(Path.Combine(options.ProfileDirectory,"pair-token"),Convert.ToBase64String(replacement),ct);
        tokenBytes = replacement;
    }
    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(ct);
                if (!await clientsLimit.WaitAsync(0,ct)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref connectionId);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                clients[id] = completion;
                _ = HandleConnectionAsync(id,client,completion,ct);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { if (!ct.IsCancellationRequested) throw; }
    }
    private async Task HandleConnectionAsync(int id, TcpClient client, TaskCompletionSource completion, CancellationToken ct)
    {
        try { await HandleAsync(client,ct); }
        finally
        {
            clients.TryRemove(id,out _);
            clientsLimit.Release();
            completion.TrySetResult();
        }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            var ct = deadline.Token;
            await using var secure = new SslStream(client.GetStream(),false);
            try
            {
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate=certificate, ClientCertificateRequired=false,
                    EnabledSslProtocols=SslProtocols.Tls12 | SslProtocols.Tls13,
                    EncryptionPolicy=EncryptionPolicy.RequireEncryption
                },ct);
                var line = await JsonLineProtocol.ReadAsync(secure,ct);
                if (line is null) return;
                var request = JsonSerializer.Deserialize<PairedControlRequest>(line,JsonLineProtocol.Json);
                if (request is null || request.Version != 1 || request.RequestId?.Length != 32 || request.RequestId.Any(c=>!Uri.IsHexDigit(c))) return;
                var response = await DispatchAsync(request,ct);
                await JsonLineProtocol.WriteAsync(secure,response,ct);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or SocketException or AuthenticationException or OperationCanceledException or JsonException or ArgumentException or InvalidOperationException) { PeerError?.Invoke(this,error); }
        }
    }
    private async Task<PairedControlResponse> DispatchAsync(PairedControlRequest request, CancellationToken ct)
    {
        byte[] supplied;
        try { supplied = Convert.FromBase64String(request.Token ?? ""); }
        catch (FormatException) { supplied = []; }
        if (supplied.Length != 32 || tokenBytes is null || !CryptographicOperations.FixedTimeEquals(supplied,tokenBytes))
            return new(1,request.RequestId,false,Error:"unauthorized");
        if (request.Action == "state")
        {
            if (request.PreviewHandoffClient) previewClientObserved?.Invoke();
            return new(1,request.RequestId,true,State:await getState(ct));
        }
        if (request.Action == "switch" && request.Target is { } target && targets.Contains(target))
            return new(1,request.RequestId,true,Result:await switchComputer(target,ct));
        if (request.Action is "preview-begin" or "preview-renew" or "preview-end")
        {
            if (previewControl is null || request.Action == "preview-begin" && !previewControl.PreviewHandoffSupported) return new(1,request.RequestId,false,Error:"preview unsupported");
            if (request.Target is not null || request.Action == "preview-begin" && request.PreviewId is not null)
                return new(1,request.RequestId,false,Error:"unsupported action or target");
            if (request.Action != "preview-begin" && !Guid.TryParseExact(request.PreviewId,"N",out _))
                return new(1,request.RequestId,false,Error:"invalid preview session");
            try
            {
                var preview = request.Action switch
                {
                    "preview-begin" => await previewControl.BeginPreviewAsync(ct),
                    "preview-renew" => await previewControl.RenewPreviewAsync(request.PreviewId!,ct),
                    _ => await previewControl.EndPreviewAsync(request.PreviewId!,ct)
                };
                return new(1,request.RequestId,true,Preview:preview);
            }
            catch (UnauthorizedAccessException) { return new(1,request.RequestId,false,Error:"preview not owner"); }
            catch (NotSupportedException) { return new(1,request.RequestId,false,Error:"preview unsupported"); }
            catch (InvalidOperationException) { return new(1,request.RequestId,false,Error:"preview handoff was not confirmed"); }
        }
        return new(1,request.RequestId,false,Error:"unsupported action or target");
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            running?.Cancel(); listener?.Stop();
            try { await accepting; } catch (OperationCanceledException) { }
            await Task.WhenAll(clients.Values.Select(completion => completion.Task)).WaitAsync(ct);
            listener = null; running?.Dispose(); running = null;
            certificate?.Dispose(); certificate = null; tokenBytes = null;
        }
        finally { lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); lifecycle.Dispose(); clientsLimit.Dispose(); }
}
