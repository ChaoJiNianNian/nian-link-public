using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Called only after a verified FINAL_ACK on the same confirmed TLS session.
    // Durable finished status is published before any evidence may be removed.
    internal async Task MarkDeliveryFinishedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await VerifyAppliedDeliveryLockedAsync(lease, receipt, ct).ConfigureAwait(false);
            var next = vaultSnapshot with { Transactions = vaultSnapshot.Transactions.Select(x =>
                x.TransactionId == lease.Identity.TransactionId ? x with { State = "finished" } : x).ToArray() };
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
                await VerifyAppliedDeliveryLockedAsync(lease, receipt, ct).ConfigureAwait(false);
                try { lease.PublishCommittedVault(temporary, file, ct); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
                try { Volatile.Write(ref vaultSnapshot, next); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task VerifyAppliedDeliveryLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        ThrowIfDisposed(); lease.RequireVerifiedHostFinal(this, receipt, ct);
        var owned = lease.GetFrozenHost(ct);
        if (!ReferenceEquals(owned.Vault, this) || owned.StageId != receipt.StageId ||
            owned.Frozen.Slot != receipt.Slot || owned.Frozen.DataPortBase != receipt.DataPortBase ||
            !FixedDigestEquals(owned.Frozen.InvitationDigest, receipt.InvitationDigest))
            throw new InvalidOperationException("The final pairing belongs to another transaction.");
        await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
        await VerifyPrepareMarkerLockedAsync(lease, receipt, ct, allowCommitted: true).ConfigureAwait(false);
        var pending = await ReadPendingGrantAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The final pairing evidence is unavailable.");
        var id = lease.Identity;
        var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId ||
            x.StageId == receipt.StageId || x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
        if (related.Length != 1 || related[0].State != "applied" ||
            !MatchesCommittedRecovery(related[0] with { State = "committed" }, pending))
            throw new InvalidOperationException("The applied transaction no longer matches current trust.");
        lease.RequireVerifiedHostFinal(this, receipt, ct);
    }
}
