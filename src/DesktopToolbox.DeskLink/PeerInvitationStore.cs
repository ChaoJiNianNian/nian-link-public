using System.Security.Cryptography;
using System.Text;

namespace DesktopToolbox.DeskLink;

/// <summary>Protected pairing evidence must be inspected before using the invitation.</summary>
public sealed class PairingRecoveryRequiredException : InvalidOperationException
{
    public PairingRecoveryRequiredException() : base("Pairing promotion requires recovery.") { }
}

/// <summary>Persists an explicitly approved peer invitation with platform-provided protection.</summary>
public sealed partial class PeerInvitationStore : IAsyncDisposable
{
    private const int MaximumBytes = 65536;
    private readonly string directory, file;
    private readonly IMultiDeviceSecretProtector protector;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MultiPeerInvitation? invitation;
    private int unsafePromotion;
    private int unfinishedPromotion;
    private long invitationRevision;
    private bool disposed;
    // Internal deterministic synchronization for a concurrent-read regression test.
    internal Action? InvitationReadCheckpointForTesting { get; set; }

    private PeerInvitationStore(string directory, IMultiDeviceSecretProtector protector, FileStream lease)
    {
        this.directory = directory;
        file = Path.Combine(directory, "peer-invitation.bin");
        this.protector = protector;
        this.lease = lease;
    }

    public static async Task<PeerInvitationStore> OpenAsync(string directory, IMultiDeviceSecretProtector protector,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute private directory is required.", nameof(directory));
        directory = Path.GetFullPath(directory);
        RejectLinks(directory);
        Directory.CreateDirectory(directory);
        RejectLinks(directory);
        var lockFile = Path.Combine(directory, ".peer-invitation.lock");
        RejectLinks(lockFile);
        var lease = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new PeerInvitationStore(directory, protector, lease);
        try
        {
            RejectLinks(store.file);
            if (File.Exists(store.file))
            {
                var info = new FileInfo(store.file);
                if (info.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected invitation is invalid or oversized.");
                var encrypted = await File.ReadAllBytesAsync(store.file, ct);
                var plain = protector.Unprotect(encrypted);
                try
                {
                    if (plain.Length > MaximumBytes) throw new InvalidDataException("Invitation is oversized.");
                    store.invitation = MultiPeerInvitation.Parse(Encoding.UTF8.GetString(plain));
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            try
            {
                await store.ResumeFinalizationLockedAsync(ct).ConfigureAwait(false);
                var promoted = await store.ReadPromotedInvitationAsync(ct).ConfigureAwait(false);
                if (promoted is not null) { store.invitation = promoted; Volatile.Write(ref store.unfinishedPromotion, 1); }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                CryptographicException or System.Text.Json.JsonException or ArgumentException or FormatException or InvalidOperationException or DecoderFallbackException)
            {
                // Keep the store available for its recovery UI, but never fall back to
                // an old invitation when promotion evidence cannot be verified.
                Volatile.Write(ref store.unsafePromotion, 1);
            }
            return store;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public MultiPeerInvitation? Get()
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var revision = Interlocked.Read(ref invitationRevision);
            if ((revision & 1) != 0) continue;
            var current = Volatile.Read(ref invitation);
            InvitationReadCheckpointForTesting?.Invoke();
            RejectLinks(RecoveryPromotionPath);
            if (Volatile.Read(ref unsafePromotion) != 0 || Volatile.Read(ref unfinishedPromotion) != 0 ||
                File.Exists(RecoveryPromotionPath) || Directory.Exists(RecoveryPromotionPath))
                throw new PairingRecoveryRequiredException();
            ObjectDisposedException.ThrowIf(disposed, this);
            // A writer increments before and after publishing any invitation. An
            // entire promotion/finalization between reads cannot return stale trust.
            if (revision == Interlocked.Read(ref invitationRevision)) return current;
        }
    }

    private void SetInvitation(MultiPeerInvitation? value)
    {
        Interlocked.Increment(ref invitationRevision);
        Volatile.Write(ref invitation, value);
        Interlocked.Increment(ref invitationRevision);
    }

    public async Task SaveConfirmedAsync(MultiPeerInvitation value, string confirmedFingerprint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        if (!DeskLinkFingerprint.Matches(value.CertificateSha256, confirmedFingerprint))
            throw new InvalidOperationException("The host fingerprint was not confirmed.");
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // A pending exchange preserves its reviewed active baseline until it is
            // either safely abandoned before READY or resolved by authenticated recovery.
            RejectExistingPendingForManualSave();
            var plain = Encoding.UTF8.GetBytes(value.ToJson());
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Protected invitation is invalid or oversized.");
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                RejectLinks(directory); RejectLinks(file); RejectLinks(temporary);
                await File.WriteAllBytesAsync(temporary, encrypted, ct);
                RejectLinks(file);
                File.Move(temporary, file, true);
                SetInvitation(value);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invitation path cannot contain links."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) { disposed = true; SetInvitation(null); await lease.DisposeAsync(); } }
        finally { gate.Release(); }
    }
}
