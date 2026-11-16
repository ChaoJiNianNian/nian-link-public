using System.Net;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nian.Input;

public sealed class InputTlsServer : IAsyncDisposable
{
    private readonly string profileDirectory;
    private readonly IPAddress bindAddress;
    private readonly int port;
    private readonly IInputTarget target;
    private readonly SemaphoreSlim lifecycle = new(1, 1), owner = new(1, 1), connectionLimit = new(4, 4);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, Task> connections = new();
    private InputProfile? profile;
    private TcpListener? listener;
    private Task accepting = Task.CompletedTask;
    private InputPairing? pairing;
    private int disposed, connectionId;
    public InputTlsServer(string profileDirectory, IPAddress bindAddress, int port, IInputTarget target)
    {
        ArgumentNullException.ThrowIfNull(bindAddress); ArgumentNullException.ThrowIfNull(target);
        if (!Path.IsPathFullyQualified(profileDirectory) || port is < 0 or > 65535 || bindAddress.Equals(IPAddress.Any) || bindAddress.Equals(IPAddress.IPv6Any)) throw new ArgumentException("Choose a concrete receiver address, port and isolated profile.");
        this.profileDirectory = Path.GetFullPath(profileDirectory); this.bindAddress = bindAddress; this.port = port; this.target = target;
    }
    public InputPairing Pairing => pairing ?? throw new InvalidOperationException("Start the isolated receiver before exporting pairing.");
    public event Action<string>? StatusChanged;
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (listener is not null) return;
            profile = await InputProfile.OpenAsync(profileDirectory, cancellationToken);
            try
            {
                listener = new TcpListener(bindAddress, port); listener.Start(4);
                pairing = new(bindAddress.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, profile.Fingerprint, Convert.ToBase64String(profile.Token));
                accepting = AcceptAsync(lifetime.Token); Notify("listening");
            }
            catch { listener?.Stop(); listener = null; profile.Dispose(); profile = null; throw; }
        }
        finally { lifecycle.Release(); }
    }
    public Task WritePairingAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return InputProfile.WritePrivateAsync(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Pairing)), cancellationToken);
    }
    private async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var tcp = await listener!.AcceptTcpClientAsync(token); tcp.NoDelay = true; Notify("tcp-accepted");
                if (!await connectionLimit.WaitAsync(0, token)) { tcp.Dispose(); continue; }
                var id = Interlocked.Increment(ref connectionId); var task = ServeAsync(tcp, token); connections[id] = task;
                _ = task.ContinueWith(completed => { connections.TryRemove(id, out _); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (token.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient tcp, CancellationToken token)
    {
        var ownsConnection = false; var begun = false; long sequence = 0, consentVersion = 0; var session = ""; var stage = "tls";
        void ReleaseActive()
        {
            if (!begun) return;
            begun = false;
            try { target.ReleaseAll(); Notify("released"); } catch { Notify("release-failed"); }
        }
        try
        {
            using (tcp)
            await using (var secure = new SslStream(tcp.GetStream(), false))
            {
                using (var authentication = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    authentication.CancelAfter(TimeSpan.FromSeconds(3));
                    Notify("tls-start");
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = profile!.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false, EncryptionPolicy = EncryptionPolicy.RequireEncryption, AllowTlsResume = false
                    }, authentication.Token);
                    Notify("tls-ready"); Notify("tls-protocol:" + secure.SslProtocol);
                    stage = "hello-read"; Notify(stage); var hello = await InputWire.ReadAsync<InputRequest>(secure, authentication.Token);
                    stage = "hello-validate"; Notify(stage);
                    if (hello.Action != "hello" || hello.Session != "" || hello.Packet is not null || !InputValidation.TryToken(hello.Token, out var supplied) || !CryptographicOperations.FixedTimeEquals(profile.Token, supplied))
                    { await InputWire.WriteAsync(secure, new InputReply(false, "hello", "", Error: "Receiver authentication failed."), authentication.Token); return; }
                    if (!await owner.WaitAsync(0, authentication.Token))
                    { await InputWire.WriteAsync(secure, new InputReply(false, "hello", "", Error: "The receiver already has an authenticated connection."), authentication.Token); return; }
                    ownsConnection = true; session = Guid.NewGuid().ToString("N");
                    stage = "hello-write"; Notify(stage);
                    await InputWire.WriteAsync(secure, new InputReply(true, "hello", session), authentication.Token); Notify("hello-ready"); Notify("connected");
                }
                stage = "request";
                while (!token.IsCancellationRequested)
                {
                    // Manual local consent may take time; only an active input session uses the short release lease.
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(begun ? 1500 : 30000);
                    var request = await InputWire.ReadAsync<InputRequest>(secure, deadline.Token);
                    if (request.Session != session || request.Token is not null || request.Action is not ("begin" or "input" or "heartbeat" or "end") || request.Action != "input" && request.Packet is not null)
                    {
                        ReleaseActive(); await InputWire.WriteAsync(secure, new InputReply(false, "rejected", session, sequence, "The input request or session is invalid."), deadline.Token); break;
                    }
                    InputReply Reply(bool ok, string? error = null) => new(ok, request.Action, session, sequence, error);
                    InputReply reply;
                    if (request.Action == "end") { ReleaseActive(); reply = Reply(true); }
                    else if (begun && target.ConsentVersion != consentVersion)
                    { ReleaseActive(); reply = Reply(false, "Local input consent changed. Begin input again."); }
                    else if ((begun || request.Action == "begin") && !target.IsReady)
                    {
                        ReleaseActive(); var reason = target.UnavailableReason;
                        reply = Reply(false, string.IsNullOrWhiteSpace(reason) || reason.Length > 256 ? "The local target is unavailable." : reason);
                    }
                    else if (request.Action == "begin")
                    {
                        if (begun) reply = Reply(false, "The input session is already active.");
                        else { consentVersion = target.ConsentVersion; begun = true; Notify("active"); reply = Reply(true); }
                    }
                    else if (request.Action == "heartbeat") reply = Reply(true);
                    else if (!begun) reply = Reply(false, "Begin input on the local target first.");
                    else if (request.Packet is not { } packet || !InputValidation.Packet(packet, sequence))
                    { ReleaseActive(); reply = Reply(false, "The input event or sequence is invalid."); }
                    else
                    {
                        try { target.Apply(packet, consentVersion); sequence = packet.Sequence; reply = Reply(true); }
                        catch { ReleaseActive(); reply = Reply(false, "The local target could not apply input."); }
                    }
                    await InputWire.WriteAsync(secure, reply, deadline.Token);
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or AuthenticationException or JsonException or InvalidOperationException or ObjectDisposedException or ArgumentException or NotSupportedException or CryptographicException)
        {
            if (!token.IsCancellationRequested)
            {
                Notify("connection-error:" + stage + ":" + InputDiagnostics.Describe(error));
            }
        }
        finally
        {
            ReleaseActive(); if (ownsConnection) { owner.Release(); Notify("disconnected"); }
            tcp.Dispose(); connectionLimit.Release();
        }
    }
    private void Notify(string status) { try { StatusChanged?.Invoke(status); } catch { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await lifecycle.WaitAsync();
        try
        {
            listener?.Stop(); await accepting;
            await Task.WhenAll(connections.Values.ToArray());
            profile?.Dispose(); profile = null; listener = null; pairing = null;
        }
        finally { lifecycle.Release(); lifetime.Dispose(); }
    }
}
