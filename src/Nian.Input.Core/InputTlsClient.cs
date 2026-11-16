using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nian.Input;

public sealed class InputTlsClient : IAsyncDisposable
{
    private readonly InputPairing pairing;
    private readonly byte[] fingerprint;
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private TcpClient? tcp;
    private SslStream? secure;
    private string session = "";
    private int disposed;
    public InputTlsClient(InputPairing pairing) { fingerprint = InputValidation.Pairing(pairing); this.pairing = pairing; }
    public event Action<string>? StatusChanged;
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await requests.WaitAsync(deadline.Token);
        var stage = "tcp-connect";
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (secure is not null) throw new InvalidOperationException("This input client is already connected.");
            Notify("tcp-connect"); tcp = new TcpClient { NoDelay = true }; await tcp.ConnectAsync(pairing.Host, pairing.Port, deadline.Token); Notify("tcp-ready");
            secure = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null && CryptographicOperations.FixedTimeEquals(fingerprint, SHA256.HashData(certificate.GetRawCertData())));
            stage = "tls"; Notify("tls-start");
            await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "nian-input-lab", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption, AllowTlsResume = false
            }, deadline.Token);
            // Enforce the pin after every handshake as well as in the callback, including resumed TLS implementations.
            stage = "tls-pin";
            if (secure.RemoteCertificate is not { } certificate || !CryptographicOperations.FixedTimeEquals(fingerprint, SHA256.HashData(certificate.GetRawCertData()))) throw new AuthenticationException("The receiver identity did not match.");
            Notify("tls-ready"); Notify("tls-protocol:" + secure.SslProtocol);
            stage = "hello-write"; Notify(stage);
            await InputWire.WriteAsync(secure, new InputRequest("hello", "", pairing.Token), deadline.Token);
            stage = "hello-read"; Notify(stage);
            var reply = await InputWire.ReadAsync<InputReply>(secure, deadline.Token);
            if (!reply.Ok || reply.Action != "hello" || !Guid.TryParseExact(reply.Session, "N", out _)) throw new AuthenticationException("The receiver rejected authentication or is busy.");
            session = reply.Session; Notify("hello-ready"); Notify("connected");
        }
        catch (Exception error) { Notify("connection-error:" + stage + ":" + InputDiagnostics.Describe(error)); CloseTransport(); throw; }
        finally { requests.Release(); }
    }
    public Task<InputReply> BeginAsync(CancellationToken cancellationToken = default) => RequestAsync("begin", null, cancellationToken);
    public Task<InputReply> SendAsync(InputPacket packet, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(packet); return RequestAsync("input", packet, cancellationToken); }
    public Task<InputReply> HeartbeatAsync(CancellationToken cancellationToken = default) => RequestAsync("heartbeat", null, cancellationToken);
    public Task<InputReply> EndAsync(CancellationToken cancellationToken = default) => RequestAsync("end", null, cancellationToken);
    private async Task<InputReply> RequestAsync(string action, InputPacket? packet, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); deadline.CancelAfter(1500);
        await requests.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (secure is null || session.Length == 0) throw new InvalidOperationException("Connect the paired input receiver first.");
            await InputWire.WriteAsync(secure, new InputRequest(action, session, Packet: packet), deadline.Token);
            var reply = await InputWire.ReadAsync<InputReply>(secure, deadline.Token);
            if (reply.Action != action || reply.Session != session || reply.Sequence < 0 || reply.Ok && packet is not null && reply.Sequence != packet.Sequence) throw new InvalidDataException("The receiver reply did not match this input request.");
            if (reply.Ok && action is "begin" or "end") Notify(action == "begin" ? "active" : "released");
            return reply;
        }
        catch { CloseTransport(); throw; }
        finally { requests.Release(); }
    }
    private void CloseTransport() { secure?.Dispose(); secure = null; tcp?.Dispose(); tcp = null; session = ""; Notify("disconnected"); }
    private void Notify(string status) { try { StatusChanged?.Invoke(status); } catch { } }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); tcp?.Dispose();
        await requests.WaitAsync();
        try { CloseTransport(); } finally { requests.Release(); lifetime.Dispose(); }
    }
}
