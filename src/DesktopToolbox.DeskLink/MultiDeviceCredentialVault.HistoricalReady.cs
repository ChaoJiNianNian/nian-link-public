using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Only a new confirmed session that received this peer's READY proof may
    // commit the old protected pending grant. Its old timeout is not reused as
    // authorization; the new session itself must remain live throughout.
    internal async Task CommitHistoricalReadyAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(reviewed);
        await SessionChangeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var pending = await VerifyHistoricalReadyLockedAsync(lease, reviewed, ct).ConfigureAwait(false);
                var current = vaultSnapshot;
                var id = pending.Identity;
                var devices = current.Devices.Select(x => x with { Token = CanonicalExistingToken(x.Token) }).Append(
                    new Entry(id.PeerName, id.PeerFingerprint, pending.Token, pending.Offer.Platform, pending.Slot)).ToArray();
                var transaction = new VaultTransaction(id.TransactionId, pending.StageId, id.SessionId, id.OfferNonce,
                    id.HostName, id.HostFingerprint, id.PeerName, id.PeerFingerprint, pending.Offer.Platform, pending.Slot,
                    pending.Frozen!.InvitationDigest, VaultTokenDigest(pending.Token), "committed", id.ExpiresAtUtc);
                var next = current with { Version = current.Version == 1 ? 2 : current.Version, Devices = devices, Transactions = current.Transactions.Append(transaction).ToArray() };
                ValidateVaultSnapshot(next);
                var plain = SerializeVaultSnapshot(next);
                byte[] encrypted;
                try { encrypted = protector.Protect(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (encrypted.Length is <= 0 or > MaxVaultFileBytes) throw new InvalidDataException("Protected device trust is invalid.");
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var created = false;
                try
                {
                    RejectLinks(directory); RejectLinks(file); RejectLinks(temporary);
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
                    {
                        created = true;
                        await output.WriteAsync(encrypted, ct).ConfigureAwait(false);
                        await output.FlushAsync(ct).ConfigureAwait(false);
                    }
                    await VerifyHistoricalReadyLockedAsync(lease, reviewed, ct).ConfigureAwait(false);
                    try { lease.PublishCommittedVault(temporary, file, ct); }
                    catch { Volatile.Write(ref writeUncertain, 1); throw; }
                    try { Volatile.Write(ref vaultSnapshot, next); }
                    catch { Volatile.Write(ref writeUncertain, 1); throw; }
                }
                finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
            }
            finally { gate.Release(); }
        }
        finally { SessionChangeGate.Release(); }
    }

    private async Task<PendingGrantDocument> VerifyHistoricalReadyLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        ThrowIfDisposed(); lease.RequireVerifiedHistoricalReady(this, reviewed, ct);
        await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
        var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
        if (!ReferenceEquals(reviewed.Owner, this) || reviewed != current || reviewed.Phase != PairingRecoveryPhase.AwaitingPeer)
            throw new InvalidOperationException("Historical READY evidence changed.");
        var pending = await ReadPendingGrantAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Historical pending grant is unavailable.");
        if (pending.Frozen is null || pending.Identity != reviewed.Binding.Identity || pending.StageId != reviewed.Binding.StageId ||
            pending.Slot != reviewed.Binding.Slot || pending.Frozen.DataPortBase != reviewed.Binding.DataPortBase ||
            !FixedDigestEquals(pending.Frozen.InvitationDigest, reviewed.Binding.InvitationDigest))
            throw new InvalidOperationException("Historical READY does not match the pending grant.");
        ValidateFrozenInvitation(pending);
        var id = pending.Identity;
        if (vaultSnapshot.Transactions.Any(x => x.TransactionId == id.TransactionId || x.StageId == pending.StageId ||
                x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce) ||
            ConflictsWithRetired(vaultSnapshot, id, pending.StageId, VaultTokenDigest(pending.Token)) ||
            vaultSnapshot.Transactions.Length >= MaximumVaultTransactions || vaultSnapshot.Devices.Length >= MultiDeviceCredentials.MaximumPeers ||
            vaultSnapshot.Devices.Any(x => string.Equals(x.Name, id.PeerName, StringComparison.OrdinalIgnoreCase) ||
                x.Fingerprint == id.PeerFingerprint || x.Slot == pending.Slot))
            throw new InvalidOperationException("Historical READY conflicts with current trust.");
        lease.RequireVerifiedHistoricalReady(this, reviewed, ct);
        return pending;
    }
}
