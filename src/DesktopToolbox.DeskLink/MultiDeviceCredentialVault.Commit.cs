using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Only this method claims the real READY and it serializes runtime changes.
    // The active device and its transaction record are published together.
    internal async Task CommitVerifiedReadyAsync(PairingTlsConsentSession.DeliveryLease delivery, CancellationToken ct)
    {
        await SessionChangeGate.WaitAsync(ct).ConfigureAwait(false);
        try { await CommitVerifiedReadyUnderSessionGateAsync(delivery, ct).ConfigureAwait(false); }
        finally { SessionChangeGate.Release(); }
    }

    private async Task CommitVerifiedReadyUnderSessionGateAsync(PairingTlsConsentSession.DeliveryLease delivery, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); delivery.EnsureCurrent(ct);
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            var ready = delivery.ClaimVerifiedReady(ct);
            var owned = delivery.GetFrozenHost(ct);
            if (!ReferenceEquals(owned.Vault, this) || owned.StageId != ready.StageId ||
                owned.Frozen.Slot != ready.Slot || owned.Frozen.DataPortBase != ready.DataPortBase ||
                !FixedDigestEquals(owned.Frozen.InvitationDigest, ready.InvitationDigest))
                throw new InvalidOperationException("READY does not match this pending invitation.");
            await VerifyPrepareMarkerLockedAsync(delivery, ready, ct).ConfigureAwait(false);
            var pending = await ReadPendingGrantAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The pending grant is missing.");
            if (pending.StageId != ready.StageId || pending.Identity != delivery.Identity || pending.Offer != delivery.Offer ||
                pending.Frozen is null || FrozenMetadata(pending) != owned.Frozen)
                throw new InvalidOperationException("The frozen pending grant changed.");
            ValidatePendingBinding(pending.Identity, pending.Offer, DateTimeOffset.UtcNow);
            ValidateFrozenInvitation(pending);
            var current = vaultSnapshot;
            if (current.Transactions.Any(x => x.TransactionId == pending.Identity.TransactionId || x.StageId == pending.StageId ||
                    x.SessionId == pending.Identity.SessionId || x.OfferNonce == pending.Identity.OfferNonce) ||
                ConflictsWithRetired(current, pending.Identity, pending.StageId, VaultTokenDigest(pending.Token)) ||
                current.Transactions.Length >= MaximumVaultTransactions || current.Devices.Length >= MultiDeviceCredentials.MaximumPeers ||
                current.Devices.Any(x => string.Equals(x.Name, pending.Identity.PeerName, StringComparison.OrdinalIgnoreCase) ||
                    x.Fingerprint == pending.Identity.PeerFingerprint || x.Slot == pending.Slot))
                throw new InvalidOperationException("Device or pairing transaction conflicts with current trust.");
            var id = pending.Identity;
            var devices = current.Devices.Select(x => x with { Token = CanonicalExistingToken(x.Token) }).Append(
                new Entry(id.PeerName, id.PeerFingerprint, pending.Token, pending.Offer.Platform, pending.Slot)).ToArray();
            var transaction = new VaultTransaction(id.TransactionId, pending.StageId, id.SessionId, id.OfferNonce,
                id.HostName, id.HostFingerprint, id.PeerName, id.PeerFingerprint, pending.Offer.Platform, pending.Slot,
                pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token), "committed", id.ExpiresAtUtc);
            var next = current with { Version = current.Version == 1 ? 2 : current.Version, Devices = devices, Transactions = current.Transactions.Append(transaction).ToArray() };
            ValidateVaultSnapshot(next);
            var plain = SerializeVaultSnapshot(next);
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaxVaultFileBytes) throw new InvalidDataException("Protected device trust is invalid.");
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                RejectLinks(directory); RejectLinks(file); RejectLinks(temporary);
                await File.WriteAllBytesAsync(temporary, encrypted, ct).ConfigureAwait(false);
                await VerifyPrepareMarkerLockedAsync(delivery, ready, ct).ConfigureAwait(false);
                await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
                ValidatePendingBinding(pending.Identity, pending.Offer, DateTimeOffset.UtcNow);
                RejectLinks(file); delivery.EnsureCurrent(ct);
                try { delivery.PublishCommittedVault(temporary, file, ct); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
                try { Volatile.Write(ref vaultSnapshot, next); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    // Unknown or externally changed active trust must never be silently overwritten.
    // The empty initial vault with no file is valid (checked by the shared reader).
    private async Task EnsureActiveTrustMatchesDiskAsync(CancellationToken ct)
    {
        try
        {
            if (!await ActiveVaultMatchesDiskLockedAsync(ct).ConfigureAwait(false))
                throw new InvalidOperationException("Device trust changed on disk.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { Volatile.Write(ref writeUncertain, 1); throw; }
    }

    private static string CanonicalExistingToken(string token)
    {
        var decoded = Convert.FromBase64String(token);
        try { return Convert.ToBase64String(decoded); }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }
}
