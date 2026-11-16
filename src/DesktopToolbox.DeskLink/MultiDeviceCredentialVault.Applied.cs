using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Publish the verified APPLIED result together with the current trust snapshot.
    // Evidence cleanup is separate and must tolerate a crash between file deletes.
    internal async Task MarkDeliveryAppliedAsync(PairingTlsConsentSession.DeliveryLease lease,
        PairingPrepareReceipt receipt, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lease.RequireVerifiedHostApplied(this, receipt, ct);
            await VerifyCommittedDeliveryLockedAsync(lease, receipt, ct).ConfigureAwait(false);
            var current = vaultSnapshot;
            var matching = current.Transactions.Single(x => x.TransactionId == lease.Identity.TransactionId);
            var next = current with { Transactions = current.Transactions.Select(x =>
                x.TransactionId == matching.TransactionId ? x with { State = "applied" } : x).ToArray() };
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
                await VerifyCommittedDeliveryLockedAsync(lease, receipt, ct).ConfigureAwait(false);
                lease.RequireVerifiedHostApplied(this, receipt, ct);
                try { lease.PublishCommittedVault(temporary, file, ct); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
                try { Volatile.Write(ref vaultSnapshot, next); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
}
