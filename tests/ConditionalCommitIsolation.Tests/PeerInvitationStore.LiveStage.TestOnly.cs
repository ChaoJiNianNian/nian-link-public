using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed record LivePeerStageMetadata(Guid StageId, int Slot, int DataPortBase, string Digest)
{
    public override string ToString() => "[isolated peer stage]";
}

public sealed partial class PeerInvitationStore
{
    private const string LiveStageFile = PendingInvitationFile;
    private static readonly string[] LiveStageFields = ["version", "stageId", "identity", "slot", "dataPortBase", "invitation", "invitationDigest", "baseInvitationDigest"];
    private sealed record LiveStageDocument(int Version, Guid StageId, PairingSessionIdentity Identity, int Slot, int DataPortBase,
        MultiPeerInvitation Invitation, string InvitationDigest, string? BaseInvitationDigest)
    {
        public override string ToString() => "[isolated protected peer stage]";
    }
    private int liveStageUncertain;
    internal ControlledLivePublishBarrier? BeforeLivePeerPublish { get; set; }
    internal TaskCompletionSource? BeforeLivePeerGateWait { get; set; }
    internal void MarkLivePeerStageUncertain() => Volatile.Write(ref liveStageUncertain, 1);
    internal void ValidateLivePeerTestPublishPaths(string temporary, string target) => IsolatedTestPathGuard.Validate(directory, temporary, target, PendingInvitationFile);

    private static void ValidateLiveStage(LiveStageDocument document)
    {
        if (document.Version != 2 || document.Identity is null || document.Invitation is null || document.StageId == Guid.Empty || document.Slot is < 1 or > 4 ||
            document.DataPortBase is < 1 or > 65532 || document.Invitation.Host != IPAddress.Loopback.ToString() ||
            document.Invitation.DataPort != document.DataPortBase + document.Slot - 1 ||
            document.Invitation.Port == document.Invitation.KeyboardPort ||
            document.Invitation.Port >= document.DataPortBase && document.Invitation.Port < document.DataPortBase + 4 ||
            document.Invitation.KeyboardPort >= document.DataPortBase && document.Invitation.KeyboardPort < document.DataPortBase + 4)
            throw new InvalidDataException("Invalid isolated peer stage.");
        ValidatePending(document.Identity, document.Invitation, document.InvitationDigest);
        if (document.BaseInvitationDigest is not null) ValidateDigest(document.BaseInvitationDigest);
    }

    internal async Task<byte[]> StageLivePeerTestAsync(PairingTlsConsentSession session, object capability, Guid stage,
        PairingSessionIdentity identity, PeerIdentityOffer offer, MultiPeerInvitation candidate, int slot, int dataPortBase,
        CancellationToken ct = default, object? protocolTicket = null, bool writeV3 = false)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(candidate);
        if (writeV3 && protocolTicket is null) throw new InvalidOperationException("A parsed prepare ticket is required for v3.");
        if (protocolTicket is not null) session.BeginReservedLivePeerStageTest(capability, protocolTicket, identity, offer, stage, candidate, slot, dataPortBase);
        else session.BeginLivePeerStageTest(capability, identity, offer);
        try
        {
            using var deadline = session.CreateLiveVaultDeadline(ct);
            BeforeLivePeerGateWait?.TrySetResult();
            await gate.WaitAsync(deadline.Token);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this); RejectPromotionMutationTest();
                if (Volatile.Read(ref liveStageUncertain) != 0) throw new InvalidOperationException("Reopen isolated peer store to inspect uncertain result.");
                session.CheckLiveVaultTest(capability, deadline.Token);
                var pendingPath = Path.Combine(directory, LiveStageFile); RejectLinks(pendingPath);
                if (File.Exists(pendingPath) || Directory.Exists(pendingPath))
                    throw new InvalidOperationException("Another pending invitation exists.");
                var digest = ComputePendingDigest(candidate); var previous = invitation is null ? null : ComputePendingDigest(invitation);
                object document;
                if (writeV3)
                {
                    var v3 = new V3Document(3, stage, identity, NormalizeV3Offer(offer), slot, dataPortBase, candidate, digest, previous);
                    ValidateV3History(v3); document = v3;
                }
                else
                {
                    var v2 = new LiveStageDocument(2, stage, identity, slot, dataPortBase, candidate, digest, previous);
                    ValidateLiveStage(v2); document = v2;
                }
                var plain = JsonSerializer.SerializeToUtf8Bytes(document, PendingJson); byte[] encrypted;
                try { encrypted = protector.Protect(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid protected peer stage.");
                var path = Path.Combine(directory, LiveStageFile); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    RejectLinks(directory); RejectLinks(path); RejectLinks(temp);
                    await File.WriteAllBytesAsync(temp, encrypted, deadline.Token);
                    if (BeforeLivePeerPublish is not null) await BeforeLivePeerPublish.PauseAsync();
                    RejectLinks(path); session.CheckLiveVaultTest(capability, deadline.Token);
                    session.PublishLivePeerStageTest(capability, this, temp, path, deadline.Token);
                    return SHA256.HashData(encrypted);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            finally { gate.Release(); }
        }
        finally { session.FinishLiveVaultTest(); }
    }

    internal async Task<LivePeerStageMetadata?> ReadLivePeerStageTestAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); RejectPromotionMutationTest();
            if (Volatile.Read(ref liveStageUncertain) != 0) throw new InvalidOperationException("Reopen isolated peer store to inspect uncertain result.");
            var doc = await ReadLiveStageAsync(ct);
            return doc is null ? null : new(doc.StageId, doc.Slot, doc.DataPortBase, doc.InvitationDigest);
        }
        finally { gate.Release(); }
    }

    private async Task<LiveStageDocument?> ReadLiveStageAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, LiveStageFile); RejectLinks(directory); RejectLinks(path);
        if (Directory.Exists(path)) throw new InvalidDataException("Invalid isolated peer stage path.");
        if (!File.Exists(path)) return null;
        var encrypted = new byte[MaximumBytes + 1]; int count = 0;
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
        {
            while (count < encrypted.Length)
            {
                var read = await input.ReadAsync(encrypted.AsMemory(count), ct);
                if (read == 0) break; count += read;
            }
        }
        if (count is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid protected peer stage length.");
        var plain = protector.Unprotect(encrypted[..count]);
        try
        {
            if (plain.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid peer stage length.");
            using var parsed = JsonDocument.Parse(StrictPendingUtf8.GetString(plain), new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateFields(parsed.RootElement); RequireExactFields(parsed.RootElement, LiveStageFields);
            RequireExactFields(parsed.RootElement.GetProperty("identity"), IdentityFields);
            RequireExactFields(parsed.RootElement.GetProperty("invitation"), InvitationFields);
            var doc = parsed.RootElement.Deserialize<LiveStageDocument>(PendingJson) ?? throw new InvalidDataException("Empty peer stage.");
            ValidateLiveStage(doc); return doc;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or System.Text.DecoderFallbackException)
        { throw new InvalidDataException("Invalid peer stage.", e); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
