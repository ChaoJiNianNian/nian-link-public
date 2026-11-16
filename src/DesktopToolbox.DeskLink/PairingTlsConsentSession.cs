using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>
/// Experimental in-memory consent exchange on an existing mutually authenticated TLS connection.
/// It does not open a listener, grant trust, generate tokens, persist data, or replace user confirmation.
/// After successful construction this session owns the TLS stream; on constructor failure the caller retains it.
/// Each instance belongs to exactly one TLS connection. EOF is observed while reading, not while idle.
/// </summary>
internal sealed partial class PairingTlsConsentSession : IAsyncDisposable
{
    private const int MaximumFrameBytes = 4096;
    // Version 2 binds the entire offer; the previous experimental HELLO is deliberately rejected.
    private const int ConsentProtocolVersion = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] IdentityFields = ["sessionId", "offerNonce", "transactionId", "hostName", "peerName", "hostFingerprint", "peerFingerprint", "expiresAtUtc"];
    private static readonly string[] OfferFields = ["version", "name", "platform", "keyboardFingerprint", "nonce", "expiresAtUtc"];
    private static readonly string[] HelloFields = ["version", "type", "role", "identity", "offer", "challenge"];
    private static readonly string[] ConfirmFields = ["version", "type", "role", "sessionId", "offerNonce", "transactionId", "senderChallenge", "receiverChallenge"];
    private readonly SslStream stream;
    private readonly PairingSessionIdentity identity;
    private readonly PeerIdentityOffer offer;
    private readonly PairingConsentState consent;
    private readonly Func<DateTimeOffset> clock;
    private readonly string role, otherRole, challenge;
    private readonly object gate = new();
    private readonly SemaphoreSlim writeGate = new(1, 1), readGate = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private readonly TaskCompletionSource abortFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Timer? expiryTimer;
    private Task? disposeTask;
    private int activeOperations;
    private string? remoteChallenge;
    private bool started, helloComplete, localConfirmation, remoteConfirmation;
    private int disposed;

    internal PairingTlsConsentSession(SslStream stream, PairingSessionIdentity identity, PeerIdentityOffer offer, bool isHost, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(offer);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        var now = this.clock(); offer.Validate(now);
        if (!stream.IsAuthenticated || !stream.IsEncrypted || !stream.IsMutuallyAuthenticated || !IsSupportedProtocol(stream.SslProtocol) || stream.LocalCertificate is null || stream.RemoteCertificate is null ||
            !Guid.TryParseExact(offer.Nonce, "N", out var offerNonce) || offerNonce != identity.OfferNonce ||
            offer.Name != identity.PeerName || !DeskLinkFingerprint.Matches(identity.PeerFingerprint, offer.KeyboardFingerprint) ||
            identity.ExpiresAtUtc > offer.ExpiresAtUtc ||
            !Matches(isHost ? identity.HostFingerprint : identity.PeerFingerprint, stream.LocalCertificate) ||
            !Matches(isHost ? identity.PeerFingerprint : identity.HostFingerprint, stream.RemoteCertificate))
            throw new ArgumentException("TLS certificates or identity offer do not match this session.");
        consent = new(identity, this.clock);
        this.stream = stream; this.identity = identity; this.offer = CanonicalizeOffer(offer);
        role = isHost ? "host" : "peer"; otherRole = isHost ? "peer" : "host";
        challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        expiryTimer = new Timer(_ => CheckDeadline(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var checkInterval = TimeSpan.FromMilliseconds(250);
        var remaining = identity.ExpiresAtUtc - now;
        expiryTimer.Change(remaining < checkInterval ? remaining : checkInterval, checkInterval);
    }

    internal static bool IsSupportedProtocol(SslProtocols protocol) => protocol is SslProtocols.Tls12 or SslProtocols.Tls13;

    internal PairingConsentPhase Phase
    {
        get
        {
            var phase = consent.Phase;
            if (phase == PairingConsentPhase.Aborted) Abort();
            return phase;
        }
    }

    internal async Task BeginAsync(CancellationToken ct = default)
    {
        try
        {
            lock (gate)
            {
                RequireActive();
                if (started) throw new InvalidOperationException("Pairing hello has already started.");
                started = true; activeOperations++;
            }
        }
        catch { Abort(); throw; }
        try
        {
            using var deadline = CreateDeadline(ct);
            await WriteAsync(new { version = ConsentProtocolVersion, type = "hello", role, identity, offer, challenge }, deadline.Token);
            using var document = await ReadAsync(deadline.Token);
            var root = document.RootElement;
            if (!ExactFields(root, HelloFields) || root.GetProperty("version").GetInt32() != ConsentProtocolVersion ||
                root.GetProperty("type").GetString() != "hello" || root.GetProperty("role").GetString() != otherRole ||
                !ExactFields(root.GetProperty("identity"), IdentityFields) ||
                root.GetProperty("identity").Deserialize<PairingSessionIdentity>(JsonLineProtocol.Json) != identity ||
                !OfferMatches(root.GetProperty("offer"), offer, clock()) ||
                !TryChallenge(root.GetProperty("challenge"), out var receivedChallenge) || receivedChallenge == challenge)
                throw new InvalidDataException("Pairing hello does not match the active TLS session.");
            lock (gate) { RequireActive(); remoteChallenge = receivedChallenge; helloComplete = true; }
        }
        catch { Abort(); throw; }
        finally { FinishOperation(); }
    }

    internal async Task ConfirmLocalAsync(CancellationToken ct = default)
    {
        string remote;
        try
        {
            lock (gate)
            {
                RequireActive();
                if (!helloComplete || localConfirmation) throw new InvalidOperationException("Pairing confirmation is unavailable or already recorded.");
                // Reserve the one explicit user-confirmation operation before leaving the lock.
                localConfirmation = true; remote = remoteChallenge!; activeOperations++;
            }
        }
        catch { Abort(); throw; }
        try
        {
            using var deadline = CreateDeadline(ct);
            await WriteAsync(new { version = ConsentProtocolVersion, type = "confirm", role, sessionId = identity.SessionId, offerNonce = identity.OfferNonce,
                transactionId = identity.TransactionId, senderChallenge = challenge, receiverChallenge = remote }, deadline.Token);
            lock (gate) { RequireActive(); consent.ConfirmLocal(identity); }
        }
        catch { Abort(); throw; }
        finally { FinishOperation(); }
    }

    internal async Task ReceiveRemoteConfirmationAsync(CancellationToken ct = default)
    {
        try
        {
            lock (gate)
            {
                RequireActive();
                if (!helloComplete || remoteConfirmation) throw new InvalidOperationException("Remote confirmation is unavailable or already received.");
                remoteConfirmation = true; activeOperations++;
            }
        }
        catch { Abort(); throw; }
        try
        {
            using var deadline = CreateDeadline(ct);
            using var document = await ReadAsync(deadline.Token);
            var root = document.RootElement;
            if (!ExactFields(root, ConfirmFields) || root.GetProperty("version").GetInt32() != ConsentProtocolVersion ||
                root.GetProperty("type").GetString() != "confirm" || root.GetProperty("role").GetString() != otherRole ||
                !root.GetProperty("sessionId").TryGetGuid(out var session) || session != identity.SessionId ||
                !root.GetProperty("offerNonce").TryGetGuid(out var offer) || offer != identity.OfferNonce ||
                !root.GetProperty("transactionId").TryGetGuid(out var transaction) || transaction != identity.TransactionId ||
                !TryChallenge(root.GetProperty("senderChallenge"), out var sender) || sender != remoteChallenge ||
                !TryChallenge(root.GetProperty("receiverChallenge"), out var receiver) || receiver != challenge)
                throw new InvalidDataException("Pairing confirmation does not match the active TLS connection.");
            lock (gate) { RequireActive(); consent.ConfirmRemote(identity); }
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        { Abort(); throw new InvalidDataException("Invalid pairing confirmation.", error); }
        catch { Abort(); throw; }
        finally { FinishOperation(); }
    }

    private CancellationTokenSource CreateDeadline(CancellationToken ct)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(ct, stopped.Token);
        var remaining = identity.ExpiresAtUtc - clock();
        if (remaining <= TimeSpan.Zero) next.Cancel(); else next.CancelAfter(remaining);
        return next;
    }
    private async Task WriteAsync<T>(T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonLineProtocol.Json);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Pairing frame exceeds the limit.");
        await writeGate.WaitAsync(ct);
        try { await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct); await stream.FlushAsync(ct); }
        finally { writeGate.Release(); }
    }
    private async Task<JsonDocument> ReadAsync(CancellationToken ct)
    {
        await readGate.WaitAsync(ct);
        try
        {
            var buffer = new byte[MaximumFrameBytes]; var one = new byte[1]; var count = 0;
            while (true)
            {
                var length = await stream.ReadAsync(one, ct);
                if (length == 0) throw new EndOfStreamException("Pairing connection closed.");
                if (one[0] == (byte)'\n')
                {
                    try { return JsonDocument.Parse(StrictUtf8.GetString(buffer, 0, count), new JsonDocumentOptions { MaxDepth = 4 }); }
                    catch (Exception error) when (error is DecoderFallbackException or JsonException)
                    { throw new InvalidDataException("Invalid pairing frame.", error); }
                }
                if (count >= buffer.Length) throw new InvalidDataException("Pairing frame exceeds the limit.");
                buffer[count++] = one[0];
            }
        }
        finally { readGate.Release(); }
    }
    private void RequireActive()
    {
        var phase = consent.Phase;
        if (Volatile.Read(ref disposed) != 0 || phase != PairingConsentPhase.AwaitingConfirmation && phase != PairingConsentPhase.ReadyToPrepare)
            throw new InvalidOperationException("Pairing session is no longer active.");
    }
    private void Abort()
    {
        // The terminal transition shares the same short gate as session operations.
        // Cancel, dispose and continuations run only after leaving that gate.
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Volatile.Write(ref disposed, 1);
        }
        try
        {
            try { consent.Stop(); }
            finally
            {
                Interlocked.Exchange(ref expiryTimer, null)?.Dispose();
                try { stopped.Cancel(); }
                finally { stream.Dispose(); }
            }
        }
        finally
        {
            lock (gate) if (activeOperations == 0) drained.TrySetResult();
            abortFinished.TrySetResult();
        }
    }
    private void CheckDeadline()
    {
        try { if (consent.Phase == PairingConsentPhase.Aborted) Abort(); }
        catch { try { Abort(); } catch { /* Timer callbacks must not escape the process. */ } }
    }
    private void FinishOperation()
    {
        lock (gate)
        {
            activeOperations--;
            if (activeOperations == 0 && Volatile.Read(ref disposed) != 0) drained.TrySetResult();
        }
    }
    public ValueTask DisposeAsync()
    {
        Abort();
        lock (gate) return new(disposeTask ??= ReleaseAsync());
    }
    private async Task ReleaseAsync()
    {
        await abortFinished.Task.ConfigureAwait(false);
        await drained.Task.ConfigureAwait(false);
        stopped.Dispose(); writeGate.Dispose(); readGate.Dispose();
    }
    private static bool Matches(string fingerprint, X509Certificate certificate) => DeskLinkFingerprint.Matches(fingerprint, Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())));
    private static PeerIdentityOffer CanonicalizeOffer(PeerIdentityOffer value) => value with
    {
        KeyboardFingerprint = DeskLinkFingerprint.Normalize(value.KeyboardFingerprint),
        Nonce = Guid.ParseExact(value.Nonce, "N").ToString("N"),
        ExpiresAtUtc = value.ExpiresAtUtc.ToUniversalTime()
    };
    private static bool OfferMatches(JsonElement element, PeerIdentityOffer expected, DateTimeOffset now)
    {
        if (!ExactFields(element, OfferFields)) return false;
        try { return CanonicalizeOffer(PeerIdentityOffer.Parse(element.GetRawText(), now)) == expected; }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException)
        { return false; }
    }
    private static bool TryChallenge(JsonElement value, out string? challenge)
    {
        challenge = null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 44 } text) return false;
        try { var bytes = Convert.FromBase64String(text); if (bytes.Length != 32 || Convert.ToBase64String(bytes) != text) return false; challenge = text; return true; }
        catch (FormatException) { return false; }
    }
    private static bool ExactFields(JsonElement value, IReadOnlyCollection<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!expected.Contains(property.Name) || !found.Add(property.Name)) return false;
        return found.Count == expected.Count;
    }
}
