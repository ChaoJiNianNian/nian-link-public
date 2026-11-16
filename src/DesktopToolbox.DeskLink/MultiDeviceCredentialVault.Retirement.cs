using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    private static bool ConflictsWithRetired(VaultSnapshot snapshot, PairingSessionIdentity identity, Guid? stageId = null, string? credentialDigest = null) =>
        snapshot.Retired?.Any(x => x.TransactionId == identity.TransactionId || x.SessionId == identity.SessionId ||
            x.OfferNonce == identity.OfferNonce || stageId is not null && x.StageId == stageId ||
            credentialDigest is not null && FixedDigestEquals(x.CredentialDigest, credentialDigest)) ?? false;

    // Caller holds gate and has verified there is no pending/prepare evidence.
    // Retired identities and full active records are published atomically in the
    // same protected vault, so a crash cannot lose replay protection.
    private async Task RetireTerminalTransactionsIfNeededLockedAsync(CancellationToken ct)
    {
        var current = vaultSnapshot;
        if (current.Transactions.Length < MaximumVaultTransactions) return;
        var existing = current.Retired ?? [];
        var room = MaximumRetiredTransactions - existing.Length;
        var retiring = current.Transactions.Where(x => x.State is "aborted" or "revoked").Take(room).ToArray();
        if (retiring.Length == 0)
            throw new InvalidOperationException("Pairing history is full; a new transaction cannot be started.");
        var ids = retiring.Select(x => x.TransactionId).ToHashSet();
        var compact = current with { Version = 3, Transactions = current.Transactions.Where(x => !ids.Contains(x.TransactionId)).ToArray(),
            Retired = existing.Concat(retiring.Select(x => new RetiredTransaction(x.TransactionId, x.StageId,
                x.SessionId, x.OfferNonce, x.CredentialDigest))).ToArray() };
        ValidateVaultSnapshot(compact);
        await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
        var plain = SerializeVaultSnapshot(compact);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaxVaultFileBytes) throw new InvalidDataException("Protected device trust is invalid or oversized.");
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
            await EnsureActiveTrustMatchesDiskAsync(ct).ConfigureAwait(false);
            RejectExistingPrepareMarker();
            if (await ReadPendingGrantAsync(ct).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Pending pairing evidence prevents history retirement.");
            ct.ThrowIfCancellationRequested(); RejectLinks(file);
            try { File.Move(temporary, file, true); }
            catch { Volatile.Write(ref writeUncertain, 1); throw; }
            try { Volatile.Write(ref vaultSnapshot, compact); }
            catch { Volatile.Write(ref writeUncertain, 1); throw; }
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }
}
