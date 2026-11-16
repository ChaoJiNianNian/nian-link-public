using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PreviewFenceModel;

namespace PreviewFenceTls;

// Localhost-only research fixture: every credential, device and action is synthetic.
internal sealed class TestTlsServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly X509Certificate2 certificate;
    private readonly List<Task> connections = [];
    private readonly Dictionary<string, string> tokens = new()
    {
        ["a"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["b"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    };
    // Synthetic pairing policy, installed only by the local fixture. The common
    // target supports the concurrency scenarios; target-a/b are peer-specific.
    private readonly Dictionary<string, HashSet<string>> allowedTargets = new(StringComparer.Ordinal)
    {
        ["a"] = new(StringComparer.Ordinal) { "target", "one", "target-a" },
        ["b"] = new(StringComparer.Ordinal) { "target", "target-b" }
    };
    // A handle is installed by this test fixture, never supplied by the network.
    private readonly Fence fence = new(64, previousProcessProvenStopped: true);
    private readonly Dictionary<string, object> grants = new();
    private readonly object grantGate = new();
    private Task? accepting;
    private int fakeStarts;
    private int fakeReturns;
    public Guid Incarnation => fence.Incarnation;
    public bool DelayDispatch { get; set; }
    public bool DelayAction { get; set; }
    public TaskCompletionSource BeginReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ActionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseAction { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int FakeStarts => Volatile.Read(ref fakeStarts);
    public int FakeReturns => Volatile.Read(ref fakeReturns);
    private int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    private byte[] Fingerprint => SHA256.HashData(certificate.RawData);

    public TestTlsServer()
    {
        grants.Add("a", fence.RegisterTrustedGrant("synthetic-a", "grant-1")!);
        grants.Add("b", fence.RegisterTrustedGrant("synthetic-b", "grant-1")!);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic-localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = temporary.Export(X509ContentType.Pkcs12);
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
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
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
                using var reader = new StreamReader(tls, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(stopping.Token);
                var request = JsonSerializer.Deserialize<Request>(line!)!;
                if (request.Action == "begin" && DelayDispatch)
                {
                    BeginReceived.TrySetResult();
                    await ReleaseDispatch.Task.WaitAsync(stopping.Token);
                }
                var result = await DispatchAsync(request);
                if (request.DropResponse) return; // Simulate response loss after server completion.
                await writer.WriteLineAsync(JsonSerializer.Serialize(result));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException or AuthenticationException) { }
    }

    private async Task<Result> DispatchAsync(Request request)
    {
        if (request.Version != 2 || request.Target is null || request.Target.Length is < 1 or > 64 ||
            request.Target.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return Result.Rejected;
        object? grant = null;
        lock (grantGate)
        {
            foreach (var pair in tokens)
            {
                var expected = Encoding.UTF8.GetBytes(pair.Value);
                var supplied = Encoding.UTF8.GetBytes(request.Token ?? "");
                if (CryptographicOperations.FixedTimeEquals(expected, supplied) && allowedTargets[pair.Key].Contains(request.Target))
                    grant = grants[pair.Key];
            }
        }
        if (grant is null) return Result.Rejected;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(request.Target)));
        async Task<bool> BeginAction()
        {
            ActionEntered.TrySetResult();
            if (DelayAction) await ReleaseAction.Task.WaitAsync(stopping.Token);
            Interlocked.Increment(ref fakeStarts);
            return true; // Only the synthetic memory action has settled.
        }
        Task<bool> ReturnAction() { Interlocked.Increment(ref fakeReturns); return Task.FromResult(true); }
        return request.Action switch
        {
            "begin" => await fence.Begin(request.Incarnation, grant, request.Operation, digest, BeginAction),
            "seal" => await fence.Seal(request.Incarnation, grant, request.Operation, digest, ReturnAction),
            "renew" => await fence.Renew(request.Incarnation, grant, request.Operation, digest),
            _ => Result.Rejected
        };
    }

    public Result Revoke(string principal) { lock (grantGate) return fence.RevokeTrustedGrant(grants[principal]); }
    public string GetTestToken(string principal) { lock (grantGate) return tokens[principal]; }
    public void ReplaceTestGrant(string principal)
    {
        lock (grantGate)
        {
            var replacement = fence.ReplaceTrustedGrant(grants[principal], Guid.NewGuid().ToString("N"));
            grants[principal] = replacement ?? throw new InvalidOperationException("Synthetic grant replacement failed.");
            tokens[principal] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }
    }

    public async Task<Result> SendAsync(string principal, string action, Guid operation, int version = 2,
        string target = "target", Guid? incarnation = null, string? token = null, byte[]? pin = null, bool dropResponse = false)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, Port);
        var expected = pin ?? Fingerprint;
        await using var tls = new SslStream(socket.GetStream(), false,
            (_, cert, _, _) => cert is not null && CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(cert.GetRawCertData())));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = "synthetic-localhost", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
        using var reader = new StreamReader(tls, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var request = new Request(version, token ?? GetTestToken(principal), action, incarnation ?? Incarnation, operation, target, dropResponse);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var response = await reader.ReadLineAsync();
        if (response is null) throw new IOException("Synthetic response was lost.");
        return JsonSerializer.Deserialize<Result>(response);
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel(); listener.Stop(); ReleaseDispatch.TrySetResult(); ReleaseAction.TrySetResult();
        if (accepting is not null) await accepting;
        Task[] tasks; lock (connections) tasks = connections.ToArray();
        await Task.WhenAll(tasks); certificate.Dispose(); stopping.Dispose();
    }

    private sealed record Request(int Version, string Token, string Action, Guid Incarnation, Guid Operation, string Target, bool DropResponse);
}
