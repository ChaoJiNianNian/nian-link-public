using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopToolbox.DeskLink;

internal sealed record IsolatedRecoveryObservation(bool Matched, bool HistoryExpired, int MarkerVersion, string Scope = "pending-v3")
{
    public override string ToString() => "[isolated read-only observation]";
}

public enum RecoveryTestFrame { Init, Hello, Query, State }
public enum RecoveryTestFault { None, WrongRole, Duplicate, DuplicateIdentity, Missing, WrongChallenge, WrongSession, WrongNonce, WrongFingerprint, WrongStage, WrongPlatform, WrongOfferExpiry, WrongSlot, WrongPort, WrongDigest, Extra, CaseCollision, WrongVersion, InvalidUtf8, Oversize, Truncated }

internal sealed class RecoveryQueryControls
{
    internal ControlledLivePublishBarrier? BeforePeerState { get; set; }
    internal ControlledLivePublishBarrier? BeforeHostFinal { get; set; }
    internal ControlledLivePublishBarrier? BeforeHostStateRead { get; set; }
    internal ControlledLivePublishBarrier? BeforeFinalPeerRecheck { get; set; }
    internal RecoveryTestFrame FaultFrame { get; set; }
    internal RecoveryTestFault Fault { get; set; }
    internal bool CaptureState { get; set; }
    internal byte[]? CapturedState { get; set; }
    internal byte[]? ReplayState { get; set; }
    internal TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(8);
    internal bool StallPeerBeforeState { get; set; }
    internal TaskCompletionSource? PeerStalled { get; set; }
    internal TaskCompletionSource? StateWritten { get; set; }
    internal TaskCompletionSource? StateTruncatedWritten { get; set; }
    internal TaskCompletionSource<string>? HostStateRejected { get; set; }
    internal bool CaptureGeneratedState { get; set; }
    internal byte[]? GeneratedState { get; set; }
    internal bool ReplayInjected { get; set; }
}

// Test assembly only. It takes actual stores, captures their own records, and never accepts a caller-supplied snapshot.
internal static class IsolatedRecoveryQuery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = 6 };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxFrame = 8192;
    private static readonly string[] InitFields = ["version", "type", "role", "recoveryId", "requestId", "hostChallenge", "sessionId", "transactionId", "offerNonce", "stageId"];
    private static readonly string[] HelloFields = ["version", "type", "role", "recoveryId", "requestId", "hostChallenge", "peerChallenge"];
    private static readonly string[] QueryFields = ["version", "type", "role", "recoveryId", "requestId", "hostChallenge", "peerChallenge", "identity", "offer", "stageId", "slot", "dataPortBase", "host", "controlPort", "keyboardPort", "dataPort", "invitationDigest"];
    private static readonly string[] StateFields = [.. QueryFields, "state"];
    private static readonly string[] IdentityFields = ["sessionId", "offerNonce", "transactionId", "hostName", "peerName", "hostFingerprint", "peerFingerprint", "expiresAtUtc"];
    private static readonly string[] OfferFields = ["version", "name", "platform", "keyboardFingerprint", "nonce", "expiresAtUtc"];

    internal static Task<IsolatedRecoveryObservation?> RunAsync(MultiDeviceCredentialVault host, PeerInvitationStore peer,
        X509Certificate2 hostCertificate, X509Certificate2 peerCertificate, DateTimeOffset now,
        RecoveryQueryControls? controls = null, Func<DateTimeOffset>? clock = null, CancellationToken ct = default)
        => RunCoreAsync(host, peer, hostCertificate, peerCertificate, now, controls, clock, false, ct);

    internal static Task<IsolatedRecoveryObservation?> RunPromotedAsync(MultiDeviceCredentialVault host, PeerInvitationStore peer,
        X509Certificate2 hostCertificate, X509Certificate2 peerCertificate, DateTimeOffset now,
        RecoveryQueryControls? controls = null, Func<DateTimeOffset>? clock = null, CancellationToken ct = default)
        => RunCoreAsync(host, peer, hostCertificate, peerCertificate, now, controls, clock, true, ct);

    private static async Task<IsolatedRecoveryObservation?> RunCoreAsync(MultiDeviceCredentialVault host, PeerInvitationStore peer,
        X509Certificate2 hostCertificate, X509Certificate2 peerCertificate, DateTimeOffset now,
        RecoveryQueryControls? controls, Func<DateTimeOffset>? clock, bool promoted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(hostCertificate); ArgumentNullException.ThrowIfNull(peerCertificate);
        var duration = controls?.Timeout ?? TimeSpan.FromSeconds(8);
        if (duration < TimeSpan.FromMilliseconds(100) || duration > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(controls));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(duration);
        var clockGate = new object(); var last = now; clock ??= () => DateTimeOffset.UtcNow;
        DateTimeOffset CheckClock()
        {
            lock (clockGate)
            {
                var current = clock(); if (current < last) throw new InvalidOperationException("Recovery clock moved backwards.");
                last = current; return current;
            }
        }
        TcpListener? listener = null; TcpClient? client = null, accepted = null;
        SslStream? hostStream = null, peerStream = null;
        try
        {
            var hostSnapshot = promoted ? await host.CapturePromotedRecoveryTestAsync(CheckClock(), deadline.Token) : await host.CaptureOfflineRecoveryTestAsync(CheckClock(), deadline.Token);
            var peerSnapshot = promoted ? await peer.CapturePromotedRecoveryTestAsync(CheckClock(), deadline.Token) : await peer.CaptureOfflineRecoveryTestAsync(CheckClock(), deadline.Token);
            if (hostSnapshot is null || peerSnapshot is null) return null;
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            client = new TcpClient(AddressFamily.InterNetwork);
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token).AsTask();
            accepted = await listener.AcceptTcpClientAsync(deadline.Token); await connecting;
            hostStream = new SslStream(accepted.GetStream(), false, (_, cert, _, _) => CertificateMatches(hostSnapshot.Binding.Identity.PeerFingerprint, cert));
            peerStream = new SslStream(client.GetStream(), false, (_, cert, _, _) => CertificateMatches(peerSnapshot.Binding.Identity.HostFingerprint, cert), (_, _, _, _, _) => peerCertificate);
            await Task.WhenAll(
                hostStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = hostCertificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowTlsResume = false }, deadline.Token),
                peerStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "read-only-recovery-test", ClientCertificates = new X509CertificateCollection { peerCertificate }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowTlsResume = false }, deadline.Token));
            if (!IsPinnedConnection(hostStream, hostSnapshot.Binding.Identity.HostFingerprint, hostSnapshot.Binding.Identity.PeerFingerprint) ||
                !IsPinnedConnection(peerStream, peerSnapshot.Binding.Identity.PeerFingerprint, peerSnapshot.Binding.Identity.HostFingerprint)) return null;
            async Task<T> Guard<T>(Func<Task<T>> operation)
            {
                try { return await operation(); }
                catch { deadline.Cancel(); throw; }
            }
            var hostTask = Guard(() => HostExchangeAsync(hostStream, host, hostSnapshot, CheckClock, controls, promoted, deadline.Token));
            var peerTask = Guard(() => PeerExchangeAsync(peerStream, peer, peerSnapshot, CheckClock, controls, promoted, deadline.Token));
            await Task.WhenAll(hostTask, peerTask);
            if (controls?.BeforeFinalPeerRecheck is not null) await controls.BeforeFinalPeerRecheck.PauseAsync();
            if (!(promoted ? await peer.IsPromotedRecoveryCurrentTestAsync(peerSnapshot, CheckClock(), deadline.Token) : await peer.IsOfflineRecoverySnapshotCurrentAsync(peerSnapshot, CheckClock(), deadline.Token))) return null;
            if (promoted && !await host.IsPromotedRecoveryCurrentTestAsync(hostSnapshot, CheckClock(), deadline.Token)) return null;
            var completedAt = CheckClock(); deadline.Token.ThrowIfCancellationRequested();
            if (!ConsistentHistoryTime(hostSnapshot, completedAt) || !ConsistentHistoryTime(peerSnapshot, completedAt)) return null;
            return hostTask.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or AuthenticationException or SocketException or JsonException or ArgumentException or FormatException or InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        { return null; }
        finally
        {
            if (hostStream is not null) await hostStream.DisposeAsync(); if (peerStream is not null) await peerStream.DisposeAsync();
            accepted?.Dispose(); client?.Dispose(); listener?.Stop();
        }
    }

    private static bool CertificateMatches(string expected, X509Certificate? actual) => actual is not null &&
        DeskLinkFingerprint.Matches(expected, Convert.ToHexString(SHA256.HashData(actual.GetRawCertData())));
    private static bool IsPinnedConnection(SslStream stream, string local, string remote) => stream.IsAuthenticated && stream.IsEncrypted && stream.IsMutuallyAuthenticated &&
        stream.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13 && CertificateMatches(local, stream.LocalCertificate) && CertificateMatches(remote, stream.RemoteCertificate);

    private static async Task<IsolatedRecoveryObservation> HostExchangeAsync(SslStream stream, MultiDeviceCredentialVault host, OfflineRecoverySnapshot snapshot,
        Func<DateTimeOffset> clock, RecoveryQueryControls? controls, bool promoted, CancellationToken ct)
    {
        var recovery = Guid.NewGuid(); var request = Guid.NewGuid(); var hostChallenge = Challenge(); var binding = snapshot.Binding;
        await WriteAsync(stream, new { version = 1, type = "recovery-init", role = "host", recoveryId = recovery, requestId = request, hostChallenge,
            sessionId = binding.Identity.SessionId, transactionId = binding.Identity.TransactionId, offerNonce = binding.Identity.OfferNonce, stageId = binding.Stage }, controls, RecoveryTestFrame.Init, ct);
        using var hello = await ReadAsync(stream, ct); var root = hello.RootElement;
        CheckHeader(root, HelloFields, "recovery-hello", "peer", recovery, request);
        if (root.GetProperty("hostChallenge").GetString() != hostChallenge || !TryChallenge(root.GetProperty("peerChallenge"), out var peerChallenge) || peerChallenge == hostChallenge)
            throw new InvalidDataException("Invalid recovery hello.");
        await WriteAsync(stream, Frame("recovery-query", "host", recovery, request, hostChallenge, peerChallenge!, binding, false, promoted), controls, RecoveryTestFrame.Query, ct);
        if (controls?.BeforeHostStateRead is not null) await controls.BeforeHostStateRead.PauseAsync();
        try
        {
            using var state = await ReadAsync(stream, ct);
            CheckBindingFrame(state.RootElement, StateFields, "recovery-state", "peer", recovery, request, hostChallenge, peerChallenge!, binding);
            if (state.RootElement.GetProperty("state").GetString() != (promoted ? "matched-promotion-current-grant" : "matched-pending-v3")) throw new InvalidDataException("Invalid recovery state.");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or EndOfStreamException or FormatException)
        {
            controls?.HostStateRejected?.TrySetResult(error is EndOfStreamException ? "eof" : "invalid");
            throw;
        }
        if (controls?.BeforeHostFinal is not null) await controls.BeforeHostFinal.PauseAsync();
        ct.ThrowIfCancellationRequested();
        if (!(promoted ? await host.IsPromotedRecoveryCurrentTestAsync(snapshot, clock(), ct) : await host.IsOfflineRecoverySnapshotCurrentAsync(snapshot, clock(), ct)) || !ConsistentHistoryTime(snapshot, clock())) throw new InvalidOperationException("Host recovery snapshot changed.");
        return new(true, snapshot.IsExpired, snapshot.MarkerVersion, promoted ? "promotion-current-grant" : "pending-v3");
    }

    private static async Task<bool> PeerExchangeAsync(SslStream stream, PeerInvitationStore peer, OfflineRecoverySnapshot snapshot,
        Func<DateTimeOffset> clock, RecoveryQueryControls? controls, bool promoted, CancellationToken ct)
    {
        using var init = await ReadAsync(stream, ct); var root = init.RootElement;
        if (!Exact(root, InitFields) || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("type").GetString() != "recovery-init" || root.GetProperty("role").GetString() != "host" ||
            !TryGuid(root.GetProperty("recoveryId"), out var recovery) || !TryGuid(root.GetProperty("requestId"), out var request) ||
            !TryChallenge(root.GetProperty("hostChallenge"), out var hostChallenge) ||
            !MatchesGuid(root.GetProperty("sessionId"), snapshot.Binding.Identity.SessionId) || !MatchesGuid(root.GetProperty("transactionId"), snapshot.Binding.Identity.TransactionId) ||
            !MatchesGuid(root.GetProperty("offerNonce"), snapshot.Binding.Identity.OfferNonce) || !MatchesGuid(root.GetProperty("stageId"), snapshot.Binding.Stage)) throw new InvalidDataException("Invalid recovery init.");
        var peerChallenge = Challenge();
        await WriteAsync(stream, new { version = 1, type = "recovery-hello", role = "peer", recoveryId = recovery, requestId = request, hostChallenge, peerChallenge }, controls, RecoveryTestFrame.Hello, ct);
        using var query = await ReadAsync(stream, ct);
        CheckBindingFrame(query.RootElement, QueryFields, "recovery-query", "host", recovery, request, hostChallenge!, peerChallenge, snapshot.Binding);
        if (controls?.StallPeerBeforeState == true) { controls.PeerStalled?.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        if (controls?.BeforePeerState is not null) await controls.BeforePeerState.PauseAsync();
        ct.ThrowIfCancellationRequested();
        if (!(promoted ? await peer.IsPromotedRecoveryCurrentTestAsync(snapshot, clock(), ct) : await peer.IsOfflineRecoverySnapshotCurrentAsync(snapshot, clock(), ct)) || !ConsistentHistoryTime(snapshot, clock())) throw new InvalidOperationException("Peer recovery snapshot changed.");
        await WriteAsync(stream, Frame("recovery-state", "peer", recovery, request, hostChallenge!, peerChallenge, snapshot.Binding, true, promoted), controls, RecoveryTestFrame.State, ct);
        return true;
    }

    private static object Frame(string type, string role, Guid recoveryId, Guid requestId, string hostChallenge, string peerChallenge, OfflineRecoveryBinding b, bool state, bool promoted) => state
        ? new { version = 1, type, role, recoveryId, requestId, hostChallenge, peerChallenge, identity = b.Identity, offer = b.Offer, stageId = b.Stage, slot = b.Slot, dataPortBase = b.DataPortBase, host = b.Host, controlPort = b.ControlPort, keyboardPort = b.KeyboardPort, dataPort = b.DataPort, invitationDigest = b.InvitationDigest, state = promoted ? "matched-promotion-current-grant" : "matched-pending-v3" }
        : new { version = 1, type, role, recoveryId, requestId, hostChallenge, peerChallenge, identity = b.Identity, offer = b.Offer, stageId = b.Stage, slot = b.Slot, dataPortBase = b.DataPortBase, host = b.Host, controlPort = b.ControlPort, keyboardPort = b.KeyboardPort, dataPort = b.DataPort, invitationDigest = b.InvitationDigest };

    private static void CheckBindingFrame(JsonElement root, string[] fields, string type, string role, Guid recovery, Guid request, string hostChallenge, string peerChallenge, OfflineRecoveryBinding expected)
    {
        CheckHeader(root, fields, type, role, recovery, request);
        if (root.GetProperty("hostChallenge").GetString() != hostChallenge || root.GetProperty("peerChallenge").GetString() != peerChallenge ||
            !Exact(root.GetProperty("identity"), IdentityFields) || !Exact(root.GetProperty("offer"), OfferFields) ||
            root.GetProperty("identity").Deserialize<PairingSessionIdentity>(Json) != expected.Identity || root.GetProperty("offer").Deserialize<PeerIdentityOffer>(Json) != expected.Offer ||
            !MatchesGuid(root.GetProperty("stageId"), expected.Stage) || root.GetProperty("slot").GetInt32() != expected.Slot || root.GetProperty("dataPortBase").GetInt32() != expected.DataPortBase ||
            root.GetProperty("host").GetString() != expected.Host || root.GetProperty("controlPort").GetInt32() != expected.ControlPort ||
            root.GetProperty("keyboardPort").GetInt32() != expected.KeyboardPort || root.GetProperty("dataPort").GetInt32() != expected.DataPort ||
            !DigestMatches(root.GetProperty("invitationDigest").GetString(), expected.InvitationDigest)) throw new InvalidDataException("Invalid recovery binding.");
        var id = root.GetProperty("identity"); var offer = root.GetProperty("offer");
        if (!MatchesGuid(id.GetProperty("sessionId"), expected.Identity.SessionId) || !MatchesGuid(id.GetProperty("transactionId"), expected.Identity.TransactionId) ||
            !MatchesGuid(id.GetProperty("offerNonce"), expected.Identity.OfferNonce) ||
            id.GetProperty("expiresAtUtc").GetString() != JsonSerializer.Serialize(expected.Identity.ExpiresAtUtc, Json).Trim('"') ||
            offer.GetProperty("expiresAtUtc").GetString() != JsonSerializer.Serialize(expected.Offer.ExpiresAtUtc, Json).Trim('"')) throw new InvalidDataException("Noncanonical recovery binding.");
    }

    private static void CheckHeader(JsonElement root, string[] fields, string type, string role, Guid recovery, Guid request)
    {
        if (!Exact(root, fields) || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("type").GetString() != type || root.GetProperty("role").GetString() != role ||
            !MatchesGuid(root.GetProperty("recoveryId"), recovery) || !MatchesGuid(root.GetProperty("requestId"), request)) throw new InvalidDataException("Invalid recovery header.");
    }
    private static bool Exact(JsonElement root, IReadOnlyCollection<string> fields)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject()) if (!fields.Contains(property.Name) || !found.Add(property.Name)) return false;
        return found.Count == fields.Count;
    }
    private static bool TryGuid(JsonElement value, out Guid id)
    {
        id = Guid.Empty; var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return Guid.TryParseExact(text, "D", out id) && id != Guid.Empty && id.ToString("D") == text;
    }
    private static bool MatchesGuid(JsonElement value, Guid expected) => TryGuid(value, out var parsed) && parsed == expected;
    private static string Challenge() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static bool TryChallenge(JsonElement value, out string? challenge)
    {
        challenge = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (challenge is not { Length: 44 }) return false;
        try { var bytes = Convert.FromBase64String(challenge); try { return bytes.Length == 32 && Convert.ToBase64String(bytes) == challenge; } finally { CryptographicOperations.ZeroMemory(bytes); } }
        catch (FormatException) { return false; }
    }
    private static bool DigestMatches(string? a, string b) => a is { Length: 64 } && b.Length == 64 && a.All(char.IsAsciiHexDigitUpper) && b.All(char.IsAsciiHexDigitUpper) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b));
    private static bool ConsistentHistoryTime(OfflineRecoverySnapshot snapshot, DateTimeOffset now) => snapshot.IsExpired == (now >= snapshot.Binding.Identity.ExpiresAtUtc || now >= snapshot.Binding.Offer.ExpiresAtUtc);
    private static async Task WriteAsync<T>(SslStream stream, T frame, RecoveryQueryControls? controls, RecoveryTestFrame frameType, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        try
        {
            if (frameType == RecoveryTestFrame.State && controls?.CaptureGeneratedState == true) controls.GeneratedState = bytes.ToArray();
            if (frameType == RecoveryTestFrame.State && controls?.ReplayState is not null)
            {
                CryptographicOperations.ZeroMemory(bytes); bytes = controls.ReplayState.ToArray(); controls.ReplayInjected = true;
            }
            if (frameType == RecoveryTestFrame.State && controls?.CaptureState == true) controls.CapturedState = bytes.ToArray();
            var fault = controls is not null && controls.FaultFrame == frameType ? controls.Fault : RecoveryTestFault.None;
            if (fault != RecoveryTestFault.None)
            {
                var node = JsonNode.Parse(bytes) ?? throw new InvalidDataException("Invalid test frame.");
                switch (fault)
                {
                    case RecoveryTestFault.WrongRole: node["role"] = "other"; break;
                    case RecoveryTestFault.WrongVersion: node["version"] = 2; break;
                    case RecoveryTestFault.WrongChallenge: node["hostChallenge"] = Challenge(); break;
                    case RecoveryTestFault.WrongSession: node["identity"]!["sessionId"] = Guid.NewGuid(); break;
                    case RecoveryTestFault.WrongNonce: node["identity"]!["offerNonce"] = Guid.NewGuid(); break;
                    case RecoveryTestFault.WrongFingerprint: node["identity"]!["peerFingerprint"] = new string('C', 64); break;
                    case RecoveryTestFault.WrongStage: node["stageId"] = Guid.NewGuid(); break;
                    case RecoveryTestFault.WrongPlatform: node["offer"]!["platform"] = "mac"; break;
                    case RecoveryTestFault.WrongOfferExpiry: node["offer"]!["expiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(5); break;
                    case RecoveryTestFault.WrongSlot: node["slot"] = 4; break;
                    case RecoveryTestFault.WrongPort: node["controlPort"] = 60000; break;
                    case RecoveryTestFault.WrongDigest: node["invitationDigest"] = new string('A', 64); break;
                    case RecoveryTestFault.Extra: node["unexpected"] = true; break;
                    case RecoveryTestFault.CaseCollision: node["Type"] = "extra"; break;
                    case RecoveryTestFault.Missing: node.AsObject().Remove("requestId"); break;
                }
                var next = JsonSerializer.SerializeToUtf8Bytes(node, Json);
                if (fault == RecoveryTestFault.Duplicate)
                {
                    var duplicate = Encoding.UTF8.GetString(next).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
                    CryptographicOperations.ZeroMemory(next); next = Encoding.UTF8.GetBytes(duplicate);
                }
                if (fault == RecoveryTestFault.DuplicateIdentity)
                {
                    var duplicate = Encoding.UTF8.GetString(next).Replace("\"peerName\":\"peer\"", "\"peerName\":\"peer\",\"peerName\":\"peer\"", StringComparison.Ordinal);
                    CryptographicOperations.ZeroMemory(next); next = Encoding.UTF8.GetBytes(duplicate);
                }
                if (fault == RecoveryTestFault.InvalidUtf8) { CryptographicOperations.ZeroMemory(next); next = [0xff]; }
                if (fault == RecoveryTestFault.Oversize) { CryptographicOperations.ZeroMemory(next); next = Enumerable.Repeat((byte)'x', MaxFrame).ToArray(); }
                CryptographicOperations.ZeroMemory(bytes); bytes = next;
                if (fault == RecoveryTestFault.Truncated)
                {
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), ct); await stream.FlushAsync(ct);
                    controls?.StateTruncatedWritten?.TrySetResult();
                    await stream.DisposeAsync();
                    if (controls?.HostStateRejected is not null) await controls.HostStateRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new IOException("Synthetic truncated recovery frame.");
                }
                if (fault == RecoveryTestFault.Oversize)
                {
                    await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct); await stream.FlushAsync(ct);
                    controls?.StateWritten?.TrySetResult(); return;
                }
            }
            if (bytes.Length + 1 > MaxFrame) throw new InvalidDataException("Recovery frame is oversized.");
            await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct); await stream.FlushAsync(ct);
            if (frameType == RecoveryTestFrame.State) controls?.StateWritten?.TrySetResult();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task<JsonDocument> ReadAsync(SslStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxFrame]; var one = new byte[1]; var count = 0;
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("Recovery connection closed.");
                if (one[0] == (byte)'\n') return JsonDocument.Parse(Utf8.GetString(buffer, 0, count), new JsonDocumentOptions { MaxDepth = 6 });
                if (count >= MaxFrame - 1) throw new InvalidDataException("Recovery frame is oversized.");
                buffer[count++] = one[0];
            }
        }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid recovery frame.", e); }
        finally { CryptographicOperations.ZeroMemory(buffer); CryptographicOperations.ZeroMemory(one); }
    }
}
