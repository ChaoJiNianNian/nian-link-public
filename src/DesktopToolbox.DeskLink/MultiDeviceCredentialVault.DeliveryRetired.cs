using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    internal async Task MarkDeliveryRetiredAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await VerifyFinishedDeliveryLockedAsync(lease, receipt, ct).ConfigureAwait(false);
            await PublishRetiredLedgerLockedAsync(lease, lease.Identity.TransactionId,
                () => VerifyFinishedDeliveryLockedAsync(lease, receipt, ct), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task MarkHistoricalDeliveryRetiredAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await VerifyHistoricalFinishedLockedAsync(lease, reviewed, ct).ConfigureAwait(false);
            await PublishRetiredLedgerLockedAsync(lease, reviewed.Binding.Identity.TransactionId,
                () => VerifyHistoricalFinishedLockedAsync(lease, reviewed, ct), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task PublishRetiredLedgerLockedAsync(PairingTlsConsentSession.DeliveryLease lease, Guid transactionId,
        Func<Task> verify, CancellationToken ct)
    {
        var next = vaultSnapshot with { Transactions = vaultSnapshot.Transactions.Select(x =>
            x.TransactionId == transactionId ? x with { State = "retired" } : x).ToArray() };
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
            await verify().ConfigureAwait(false);
            try { lease.PublishCommittedVault(temporary, file, ct); }
            catch { Volatile.Write(ref writeUncertain, 1); throw; }
            try { Volatile.Write(ref vaultSnapshot, next); }
            catch { Volatile.Write(ref writeUncertain, 1); throw; }
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task VerifyFinishedDeliveryLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        ThrowIfDisposed(); lease.RequireVerifiedHostRetired(this, receipt, ct);
        var owned = lease.GetFrozenHost(ct);
        if (!ReferenceEquals(owned.Vault, this) || owned.StageId != receipt.StageId ||
            owned.Frozen.Slot != receipt.Slot || owned.Frozen.DataPortBase != receipt.DataPortBase ||
            !FixedDigestEquals(owned.Frozen.InvitationDigest, receipt.InvitationDigest))
            throw new InvalidOperationException("The retirement belongs to another transaction.");
        await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
        await VerifyPrepareMarkerLockedAsync(lease, receipt, ct, allowCommitted: true).ConfigureAwait(false);
        var pending = await ReadPendingGrantAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The retirement evidence is unavailable.");
        var id = lease.Identity;
        var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId ||
            x.StageId == receipt.StageId || x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
        if (related.Length != 1 || related[0].State != "finished" || !MatchesCommittedRecovery(related[0], pending))
            throw new InvalidOperationException("The finished pairing no longer matches current trust.");
        lease.RequireVerifiedHostRetired(this, receipt, ct);
    }

    private async Task VerifyHistoricalFinishedLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        ThrowIfDisposed(); lease.RequireVerifiedHistoricalRetired(this, reviewed, ct);
        await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
        var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
        if (!ReferenceEquals(reviewed.Owner, this) || reviewed != current || reviewed.Phase != PairingRecoveryPhase.CommittedAwaitingPeer)
            throw new InvalidOperationException("Historical finished evidence changed.");
        var id = reviewed.Binding.Identity;
        var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId ||
            x.StageId == reviewed.Binding.StageId || x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
        if (related.Length != 1 || related[0].State != "finished")
            throw new InvalidOperationException("Historical pairing is no longer finished.");
        lease.RequireVerifiedHistoricalRetired(this, reviewed, ct);
    }
}
