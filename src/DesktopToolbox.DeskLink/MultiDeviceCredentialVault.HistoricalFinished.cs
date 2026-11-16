using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // A fresh mutually authenticated session may finish only the exact reviewed
    // historical applied transaction after receiving its peer's actual final ACK.
    internal async Task MarkHistoricalDeliveryFinishedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(reviewed);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await VerifyHistoricalAppliedLockedAsync(lease, reviewed, ct).ConfigureAwait(false);
            var transactionId = reviewed.Binding.Identity.TransactionId;
            var next = vaultSnapshot with { Transactions = vaultSnapshot.Transactions.Select(x =>
                x.TransactionId == transactionId ? x with { State = "finished" } : x).ToArray() };
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
                await VerifyHistoricalAppliedLockedAsync(lease, reviewed, ct).ConfigureAwait(false);
                try { lease.PublishCommittedVault(temporary, file, ct); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
                try { Volatile.Write(ref vaultSnapshot, next); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private async Task VerifyHistoricalAppliedLockedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingHistoricalSnapshot reviewed, CancellationToken ct)
    {
        ThrowIfDisposed();
        lease.RequireVerifiedHistoricalFinal(this, reviewed, ct);
        await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
        var current = await CaptureHistoricalRecoveryLockedAsync(lease, ct).ConfigureAwait(false);
        if (!ReferenceEquals(reviewed.Owner, this) || reviewed != current ||
            reviewed.Phase != PairingRecoveryPhase.CommittedAwaitingPeer)
            throw new InvalidOperationException("Historical pairing evidence changed.");
        var id = reviewed.Binding.Identity;
        var related = vaultSnapshot.Transactions.Where(x => x.TransactionId == id.TransactionId ||
            x.StageId == reviewed.Binding.StageId || x.SessionId == id.SessionId || x.OfferNonce == id.OfferNonce).ToArray();
        if (related.Length != 1 || related[0].State != "applied")
            throw new InvalidOperationException("Historical pairing is no longer applied.");
        lease.RequireVerifiedHistoricalFinal(this, reviewed, ct);
    }
}
