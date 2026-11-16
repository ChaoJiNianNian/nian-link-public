using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DesktopToolbox.DeskLink;

public sealed partial class PeerInvitationStore
{
    private sealed class RetiredHistoryFullException : InvalidOperationException
    {
        internal RetiredHistoryFullException() : base("配对防重放记录已满，暂时无法安全清理旧记录。") { }
    }
    private const int MaxRetiredPairings = 1100;
    private const int MaxRetiredIndexBytes = 131072;
    private const byte RetiredLegacy = 0, RetiredAborted = 1, RetiredCompleted = 2;
    private static readonly byte[] RetiredMagicV1 = Encoding.ASCII.GetBytes("NLRIDX01"), RetiredMagic = Encoding.ASCII.GetBytes("NLRIDX02");
    private readonly record struct RetiredIdentity(Guid TransactionId, Guid SessionId, Guid OfferNonce, byte Kind, string? Commitment);
    private string RetiredPath => Path.Combine(directory, "peer-retired.bin");
    internal Action<string>? RetirementCheckpointForTesting { get; set; }

    private async Task<List<RetiredIdentity>> ReadRetiredAsync(CancellationToken ct)
    {
        RejectLinks(RetiredPath);
        if (Directory.Exists(RetiredPath)) throw new InvalidDataException("Retired pairing index path is invalid.");
        if (!File.Exists(RetiredPath)) return [];
        var protectedBytes = await ReadRetiredFileAsync(ct).ConfigureAwait(false);
        var plain = protector.Unprotect(protectedBytes);
        try
        {
            if (plain.Length < 12) throw new InvalidDataException("Retired pairing index is invalid.");
            var legacy = plain.AsSpan(0, 8).SequenceEqual(RetiredMagicV1);
            if (!legacy && !plain.AsSpan(0, 8).SequenceEqual(RetiredMagic))
                throw new InvalidDataException("Retired pairing index is invalid.");
            var count = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(8, 4));
            var stride = legacy ? 48 : 81;
            if (count is < 0 or > MaxRetiredPairings || plain.Length != 12 + count * stride)
                throw new InvalidDataException("Retired pairing index length is invalid.");
            var values = new List<RetiredIdentity>(count);
            for (var i = 0; i < count; i++)
            {
                var offset = 12 + i * stride;
                var kind = legacy ? RetiredLegacy : plain[offset + 48];
                if (kind is not (RetiredLegacy or RetiredAborted or RetiredCompleted)) throw new InvalidDataException("Retired pairing type is invalid.");
                var hash = legacy || kind == RetiredLegacy ? null : Convert.ToHexString(plain.AsSpan(offset + 49, 32));
                if (!legacy && kind == RetiredLegacy && plain.AsSpan(offset + 49, 32).IndexOfAnyExcept((byte)0) >= 0)
                    throw new InvalidDataException("Legacy retirement binding is invalid.");
                var item = new RetiredIdentity(new Guid(plain.AsSpan(offset, 16)), new Guid(plain.AsSpan(offset + 16, 16)),
                    new Guid(plain.AsSpan(offset + 32, 16)), kind, hash);
                if (item.TransactionId == Guid.Empty || item.SessionId == Guid.Empty || item.OfferNonce == Guid.Empty ||
                    values.Any(x => x.TransactionId == item.TransactionId || x.SessionId == item.SessionId || x.OfferNonce == item.OfferNonce))
                    throw new InvalidDataException("Retired pairing index is ambiguous.");
                values.Add(item);
            }
            return values;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<byte[]> ReadRetiredFileAsync(CancellationToken ct)
    {
        RejectLinks(RetiredPath);
        await using var input = new FileStream(RetiredPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (input.Length is <= 0 or > MaxRetiredIndexBytes) throw new InvalidDataException("Protected retired index is invalid.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return bytes;
    }

    private static RetiredIdentity RetiredOf(PairingHistoricalBinding binding, byte kind) =>
        new(binding.Identity.TransactionId, binding.Identity.SessionId, binding.Identity.OfferNonce, kind, binding.Commitment());

    private static bool SameRetiredIds(RetiredIdentity item, PairingSessionIdentity identity) =>
        item.TransactionId == identity.TransactionId && item.SessionId == identity.SessionId && item.OfferNonce == identity.OfferNonce;

    private static bool ConflictsWithRetired(IEnumerable<RetiredIdentity> values, PairingSessionIdentity identity) =>
        values.Any(x => x.TransactionId == identity.TransactionId || x.SessionId == identity.SessionId || x.OfferNonce == identity.OfferNonce);

    internal async Task CompactAbortedHistoryAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); await CompactAbortedHistoryLockedAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // Exact replay identifiers are published atomically before any source record
    // is removed. Reopening after any partial deletion safely repeats the union.
    private async Task CompactAbortedHistoryLockedAsync(CancellationToken ct,
        (string Path, byte[] Cipher, RetiredIdentity Identity)? additional = null, Action? requireAuthority = null)
    {
        requireAuthority?.Invoke();
        var originalIndex = File.Exists(RetiredPath) ? await ReadRetiredFileAsync(ct).ConfigureAwait(false) : null;
        var retired = await ReadRetiredAsync(ct).ConfigureAwait(false);
        var sources = new List<(string Path, byte[] Cipher, RetiredIdentity Identity)>();
        var sourcePaths = Directory.EnumerateFileSystemEntries(directory, "peer-aborted-*.bin").ToArray();
        if (sourcePaths.Length > 64) throw new InvalidDataException("Abort history is invalid.");
        foreach (var path in sourcePaths)
        {
            var cipher = await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false);
            var record = ParseAbortedEvidence(cipher);
            if (!string.Equals(path, AbortedPath(record.Binding.Identity.TransactionId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Abort history name is invalid.");
            sources.Add((path, cipher, RetiredOf(record.Binding, RetiredAborted)));
        }
        var reservation = await ReadReservationAsync(ct).ConfigureAwait(false);
        if (reservation?.Decision == "refused" && !File.Exists(Path.Combine(directory, PendingInvitationFile)) &&
            !File.Exists(ReadyEvidencePath) && !File.Exists(RecoveryPromotionPath) && !File.Exists(FinalizingPath))
            sources.Add((ReservationPath, await ReadReadyEvidenceFileAsync(ReservationPath, ct).ConfigureAwait(false), RetiredOf(reservation.Binding, RetiredAborted)));
        if (additional is not null) sources.Add(additional.Value);
        foreach (var (_, _, identity) in sources)
        {
            var conflicts = retired.Where(x => x.TransactionId == identity.TransactionId || x.SessionId == identity.SessionId ||
                x.OfferNonce == identity.OfferNonce).ToArray();
            if (conflicts.Length == 0) retired.Add(identity);
            else
            {
                var existing = conflicts[0];
                if (conflicts.Length != 1 || existing.TransactionId != identity.TransactionId || existing.SessionId != identity.SessionId ||
                    existing.OfferNonce != identity.OfferNonce || existing.Kind != RetiredLegacy && existing != identity)
                    throw new InvalidDataException("Retired pairing identities conflict.");
                if (existing.Kind == RetiredLegacy) retired[retired.IndexOf(existing)] = identity;
            }
        }
        if (retired.Count > MaxRetiredPairings) throw new RetiredHistoryFullException();
        if (sources.Count == 0) return;
        var plain = new byte[12 + retired.Count * 81];
        RetiredMagic.CopyTo(plain, 0); BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(8, 4), retired.Count);
        for (var i = 0; i < retired.Count; i++)
        {
            var offset = 12 + i * 81;
            retired[i].TransactionId.TryWriteBytes(plain.AsSpan(offset, 16));
            retired[i].SessionId.TryWriteBytes(plain.AsSpan(offset + 16, 16));
            retired[i].OfferNonce.TryWriteBytes(plain.AsSpan(offset + 32, 16));
            plain[offset + 48] = retired[i].Kind;
            if (retired[i].Commitment is not null) Convert.FromHexString(retired[i].Commitment!).CopyTo(plain, offset + 49);
        }
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaxRetiredIndexBytes) throw new InvalidDataException("Protected retired index is invalid.");
        var temporary = RetiredPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteNewFinalFileAsync(temporary, encrypted, ct).ConfigureAwait(false);
            var currentIndex = File.Exists(RetiredPath) ? await ReadRetiredFileAsync(ct).ConfigureAwait(false) : null;
            if (originalIndex is null ? currentIndex is not null : currentIndex is null ||
                !CryptographicOperations.FixedTimeEquals(originalIndex, currentIndex))
                throw new InvalidOperationException("Retired index changed before publication.");
            requireAuthority?.Invoke();
            ct.ThrowIfCancellationRequested(); RejectLinks(RetiredPath); File.Move(temporary, RetiredPath, true);
            var checkedIndex = await ReadRetiredAsync(ct).ConfigureAwait(false);
            if (!checkedIndex.SequenceEqual(retired)) throw new InvalidOperationException("Retired index changed after publication.");
            RetirementCheckpointForTesting?.Invoke("index-written");
            // Keep the published file open without write/delete sharing while
            // removing its source records; a different index cannot replace it
            // in the middle of the cleanup on the supported local stores.
            RejectLinks(RetiredPath);
            await using var retainedIndex = new FileStream(RetiredPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (retainedIndex.Length != encrypted.Length) throw new InvalidOperationException("Retired index changed before cleanup.");
            var retainedBytes = new byte[encrypted.Length];
            await retainedIndex.ReadExactlyAsync(retainedBytes, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(encrypted, retainedBytes))
                throw new InvalidOperationException("Retired index changed before cleanup.");
            foreach (var (path, cipher, _) in sources)
            {
                ct.ThrowIfCancellationRequested(); RejectLinks(path);
                var currentIndexBytes = await ReadRetiredFileAsync(ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(encrypted, currentIndexBytes))
                    throw new InvalidOperationException("Retired index changed during cleanup.");
                if (!File.Exists(path)) continue;
                var current = await ReadReadyEvidenceFileAsync(path, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(cipher, current))
                    throw new InvalidOperationException("Retirement source changed before cleanup.");
                requireAuthority?.Invoke();
                File.Delete(path);
                RetirementCheckpointForTesting?.Invoke("source-deleted");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
