using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal static class RecoveryActivationProtocol
{
    private const int Limit = 8192;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Fields = ["version", "type", "role", "recoveryId", "requestId", "hostChallenge", "peerChallenge", "expiresAtUtc", "identity", "offer", "stageId", "slot", "dataPortBase", "host", "controlPort", "keyboardPort", "dataPort", "invitationDigest"];
    private static readonly string[] IdentityFields = ["sessionId", "offerNonce", "transactionId", "hostName", "peerName", "hostFingerprint", "peerFingerprint", "expiresAtUtc"];
    private static readonly string[] OfferFields = ["version", "name", "platform", "keyboardFingerprint", "nonce", "expiresAtUtc"];
    internal static string Challenge() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    internal static bool IsChallenge(string? value)
    {
        if (value is not { Length: 44 }) return false;
        try { var bytes = Convert.FromBase64String(value); try { return bytes.Length == 32 && Convert.ToBase64String(bytes) == value; } finally { CryptographicOperations.ZeroMemory(bytes); } }
        catch (FormatException) { return false; }
    }
    private static bool SameDigest(string? actual, string expected)
    {
        if (actual is not { Length: 64 } || expected.Length != 64 || actual.Any(c => !char.IsAsciiHexDigitUpper(c)) || expected.Any(c => !char.IsAsciiHexDigitUpper(c))) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
    }
    private static bool Exact(JsonElement root, string[] fields)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject()) if (!fields.Contains(field.Name) || !found.Add(field.Name)) return false;
        return found.Count == fields.Length;
    }
    internal static void Validate(JsonElement root, string type, string role, OfflineRecoveryBinding b, Guid recovery, Guid request,
        string hostChallenge, string peerChallenge, DateTimeOffset expiry)
    {
        if (!Exact(root, Fields) || root.GetProperty("version").GetInt32() != 1 || root.GetProperty("type").GetString() != type || root.GetProperty("role").GetString() != role ||
            !root.GetProperty("recoveryId").TryGetGuid(out var r) || r != recovery || !root.GetProperty("requestId").TryGetGuid(out var q) || q != request ||
            root.GetProperty("hostChallenge").GetString() != hostChallenge || root.GetProperty("peerChallenge").GetString() != peerChallenge ||
            !root.GetProperty("expiresAtUtc").TryGetDateTimeOffset(out var until) || until != expiry ||
            !Exact(root.GetProperty("identity"), IdentityFields) || !Exact(root.GetProperty("offer"), OfferFields) ||
            root.GetProperty("identity").Deserialize<PairingSessionIdentity>(JsonLineProtocol.Json) != b.Identity || root.GetProperty("offer").Deserialize<PeerIdentityOffer>(JsonLineProtocol.Json) != b.Offer ||
            !root.GetProperty("stageId").TryGetGuid(out var stage) || stage != b.Stage || root.GetProperty("slot").GetInt32() != b.Slot ||
            root.GetProperty("dataPortBase").GetInt32() != b.DataPortBase || root.GetProperty("host").GetString() != b.Host ||
            root.GetProperty("controlPort").GetInt32() != b.ControlPort || root.GetProperty("keyboardPort").GetInt32() != b.KeyboardPort ||
            root.GetProperty("dataPort").GetInt32() != b.DataPort || !SameDigest(root.GetProperty("invitationDigest").GetString(), b.InvitationDigest))
            throw new InvalidDataException("Recovery frame does not match its authenticated transaction.");
    }
    internal static Task WriteAsync(RecoveryActivationSession session, string type, string role, string peerChallenge, CancellationToken ct)
        => session.WriteOrdinaryFrameAsync(type, role, peerChallenge, ct);

    internal static byte[] Encode(RecoveryActivationSession session, string type, string role, string peerChallenge)
    {
        var b = session.Binding;
        return JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type, role, recoveryId = session.RecoveryId, requestId = session.RequestId,
            hostChallenge = session.HostChallenge, peerChallenge, expiresAtUtc = session.ExpiresAtUtc, identity = b.Identity, offer = b.Offer,
            stageId = b.Stage, slot = b.Slot, dataPortBase = b.DataPortBase, host = b.Host, controlPort = b.ControlPort,
            keyboardPort = b.KeyboardPort, dataPort = b.DataPort, invitationDigest = b.InvitationDigest }, JsonLineProtocol.Json);
    }
    internal static async Task<JsonDocument> ReadAsync(SslStream stream, CancellationToken ct)
    {
        var bytes = new byte[Limit]; var one = new byte[1]; var count = 0;
        try
        {
            while (true)
            {
                if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException("Recovery connection closed.");
                if (one[0] == (byte)'\n')
                {
                    try { return JsonDocument.Parse(Utf8.GetString(bytes, 0, count), new JsonDocumentOptions { MaxDepth = 6 }); }
                    catch (Exception error) when (error is DecoderFallbackException or JsonException) { throw new InvalidDataException("Invalid recovery frame.", error); }
                }
                if (count >= Limit - 1) throw new InvalidDataException("Recovery frame too large.");
                bytes[count++] = one[0];
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(one); }
    }
}

internal static class RecoveryActivationExperiment
{
    internal static async Task<string?> RunAsync(MultiDeviceCredentialVault host, PeerInvitationStore peer,
        X509Certificate2 hostCertificate, X509Certificate2 peerCertificate, DateTimeOffset now,
        RecoveryActivationControls controls, Func<DateTimeOffset>? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(controls); controls.Bind(); clock ??= () => DateTimeOffset.UtcNow;
        var clockGate = new object(); var lastTime = now;
        DateTimeOffset ReadClock()
        {
            lock (clockGate)
            {
                var current = clock(); if (current < lastTime) throw new InvalidOperationException("Recovery clock moved backwards.");
                lastTime = current; return current;
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(12));
        TcpListener? listener = null; TcpClient? client = null, accepted = null; SslStream? hostStream = null, peerStream = null;
        RecoveryActivationSession? hostSession = null, peerSession = null; var hostRegistered = false; var peerRegistered = false;
        try
        {
            var h = await host.CapturePromotedRecoveryTestAsync(ReadClock(), deadline.Token); var p = await peer.CapturePromotedRecoveryTestAsync(ReadClock(), deadline.Token);
            if (h is null || p is null || h.IsExpired || p.IsExpired || !h.HasSameBinding(p)) return null;
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1); client = new TcpClient(AddressFamily.InterNetwork);
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token).AsTask();
            accepted = await listener.AcceptTcpClientAsync(deadline.Token); await connecting;
            hostStream = new SslStream(accepted.GetStream(), false, (_, cert, _, _) => CertificateMatches(h.Binding.Identity.PeerFingerprint, cert));
            peerStream = new SslStream(client.GetStream(), false, (_, cert, _, _) => CertificateMatches(p.Binding.Identity.HostFingerprint, cert), (_, _, _, _, _) => peerCertificate);
            await Task.WhenAll(
                hostStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = hostCertificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowTlsResume = false }, deadline.Token),
                peerStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "activation-test", ClientCertificates = new X509CertificateCollection { peerCertificate }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowTlsResume = false }, deadline.Token));
            hostSession = new RecoveryActivationSession(hostStream, h, true, ReadClock, deadline); peerSession = new RecoveryActivationSession(peerStream, p, false, ReadClock, deadline);
            if (controls.ThrowOnCancellationTest) hostSession.InstallThrowingStopCallbackTest();
            await host.BeginHostRecoveryTestAsync(hostSession, h, deadline.Token); hostRegistered = true;
            await peer.BeginPeerRecoveryTestAsync(peerSession, p, deadline.Token); peerRegistered = true;
            controls.HostSessionForTest = hostSession; controls.PeerSessionForTest = peerSession;
            controls.HostSnapshotForTest = h; controls.PeerSnapshotForTest = p;
            var latest = new[] { ReadClock().AddSeconds(30), h.Binding.Identity.ExpiresAtUtc, h.Binding.Offer.ExpiresAtUtc }.Min();
            hostSession.SetParameters(Guid.NewGuid(), Guid.NewGuid(), RecoveryActivationProtocol.Challenge(), latest, deadline.Token);
            async Task<T> Guard<T>(Func<Task<T>> operation) { try { return await operation(); } catch { deadline.Cancel(); throw; } }
            var hostTask = Guard(() => HostAsync(host, hostSession, h, controls, deadline.Token));
            var peerTask = Guard(() => PeerAsync(peer, peerSession, p, controls, deadline.Token));
            await Task.WhenAll(hostTask, peerTask); return peerTask.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or AuthenticationException or SocketException or JsonException or ArgumentException or FormatException or InvalidOperationException or OperationCanceledException or CryptographicException)
        { return null; }
        finally
        {
            Exception? cleanupError = null;
            void Remember(Exception error) => cleanupError ??= error;
            System.Net.Sockets.SafeSocketHandle? hostHandle = null, peerHandle = null;
            try { hostHandle = accepted?.Client?.SafeHandle; } catch (Exception error) { Remember(error); }
            try { peerHandle = client?.Client?.SafeHandle; } catch (Exception error) { Remember(error); }
            try { hostSession?.Stop(); } catch (Exception error) { Remember(error); }
            try { peerSession?.Stop(); } catch (Exception error) { Remember(error); }
            try { if (hostStream is not null) await hostStream.DisposeAsync(); if (controls.ThrowAfterHostStreamCloseTest) throw new IOException("Synthetic host stream close failure."); } catch (Exception error) { Remember(error); }
            try { if (peerStream is not null) await peerStream.DisposeAsync(); if (controls.ThrowAfterPeerStreamCloseTest) throw new IOException("Synthetic peer stream close failure."); } catch (Exception error) { Remember(error); }
            try { accepted?.Dispose(); } catch (Exception error) { Remember(error); }
            try { client?.Dispose(); } catch (Exception error) { Remember(error); }
            try { listener?.Stop(); } catch (Exception error) { Remember(error); }
            var hostClosed = !hostRegistered || hostHandle is not null && hostHandle.IsClosed && !controls.ForceHostClosureUnverifiedTest;
            var peerClosed = !peerRegistered || peerHandle is not null && peerHandle.IsClosed && !controls.ForcePeerClosureUnverifiedTest;
            if (hostRegistered && hostSession is not null && hostClosed)
            {
                try { await host.ReleaseActivationSendTestAsync(hostSession); await host.EndHostRecoveryTestAsync(hostSession); }
                catch (Exception error) { Remember(error); }
            }
            else if (hostRegistered && !hostClosed) { controls.HostEndSkipped?.TrySetResult(); Remember(new IOException("Host recovery socket closure is unverified.")); }
            if (peerRegistered && peerSession is not null && peerClosed)
            {
                try { await peer.EndPeerRecoveryTestAsync(peerSession); } catch (Exception error) { Remember(error); }
            }
            else if (peerRegistered && !peerClosed) { controls.PeerEndSkipped?.TrySetResult(); Remember(new IOException("Peer recovery socket closure is unverified.")); }
            if (cleanupError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }
    private static bool CertificateMatches(string pin, X509Certificate? certificate) => certificate is not null &&
        DeskLinkFingerprint.Matches(pin, Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())));

    private static async Task<bool> HostAsync(MultiDeviceCredentialVault vault, RecoveryActivationSession session, OfflineRecoverySnapshot snapshot,
        RecoveryActivationControls controls, CancellationToken ct)
    {
        await RecoveryActivationProtocol.WriteAsync(session, "recovery-init", "host", "", ct);
        using (var hello = await session.ReadFrameAsync(ct))
        {
            var challenge = hello.RootElement.GetProperty("peerChallenge").GetString();
            if (!RecoveryActivationProtocol.IsChallenge(challenge)) throw new InvalidDataException("Invalid recovery peer challenge.");
            RecoveryActivationProtocol.Validate(hello.RootElement, "recovery-hello", "peer", session.Binding, session.RecoveryId, session.RequestId, session.HostChallenge, challenge!, session.ExpiresAtUtc);
            session.SetPeerChallenge(challenge!, ct);
        }
        await session.AwaitLocalConfirmationAsync(controls, ct);
        await RecoveryActivationProtocol.WriteAsync(session, "recovery-host-confirm", "host", session.PeerChallenge, ct); controls.HostConfirmationSent.TrySetResult();
        await session.ReceiveRemoteConfirmationAsync(ct);
        if (controls.BeforeHostAuthorization is not null) await controls.BeforeHostAuthorization.PauseAsync();
        var permit = await vault.RegisterActivationSendTestAsync(session, snapshot, ct);
        await session.SendAuthorizedFromVaultAsync(vault, snapshot, permit, controls, ct);
        return true;
    }
    private static async Task<string> PeerAsync(PeerInvitationStore store, RecoveryActivationSession session, OfflineRecoverySnapshot snapshot,
        RecoveryActivationControls controls, CancellationToken ct)
    {
        using (var init = await session.ReadFrameAsync(ct))
        {
            var root = init.RootElement;
            if (!root.GetProperty("recoveryId").TryGetGuid(out var recovery) || !root.GetProperty("requestId").TryGetGuid(out var request) ||
                !root.GetProperty("expiresAtUtc").TryGetDateTimeOffset(out var until) || !RecoveryActivationProtocol.IsChallenge(root.GetProperty("hostChallenge").GetString()))
                throw new InvalidDataException("Invalid recovery init.");
            var challenge = root.GetProperty("hostChallenge").GetString()!;
            RecoveryActivationProtocol.Validate(root, "recovery-init", "host", session.Binding, recovery, request, challenge, "", until);
            session.SetParameters(recovery, request, challenge, until, ct);
        }
        session.SetPeerChallenge(RecoveryActivationProtocol.Challenge(), ct);
        await RecoveryActivationProtocol.WriteAsync(session, "recovery-hello", "peer", session.PeerChallenge, ct);
        await session.AwaitLocalConfirmationAsync(controls, ct);
        await session.ReceiveRemoteConfirmationAsync(ct); await RecoveryActivationProtocol.WriteAsync(session, "recovery-peer-confirm", "peer", session.PeerChallenge, ct);
        var permit = await session.ReceivePeerAuthorizationAsync(store, snapshot, controls, ct); controls.PeerPermitForTest = permit;
        if (controls.BeforePeerActivation is not null) await controls.BeforePeerActivation.PauseAsync();
        return await store.ActivateReservedTestAsync(session, snapshot, permit, ct);
    }
}
