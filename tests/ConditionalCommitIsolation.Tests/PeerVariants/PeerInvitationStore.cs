using System.Security.Cryptography;
using System.Text;

namespace DesktopToolbox.DeskLink;

/// <summary>Persists an explicitly approved peer invitation with platform-provided protection.</summary>
// Test-only store variant; never compiled into product DLL.
public sealed partial class PeerInvitationStore : IAsyncDisposable
{
    private const int MaximumBytes = 65536;
    private readonly string directory, file;
    private readonly IMultiDeviceSecretProtector protector;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private MultiPeerInvitation? invitation;
    private MultiPeerInvitation? promotedInvitation;
    private int promotionUnknown;
    private string PromotionPath => Path.Combine(directory, "peer-promotion.bin");
    private string RecoveryPath => Path.Combine(directory, "peer-recovery.bin");
    private bool disposed;

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
            RejectLinks(store.file); RejectLinks(store.PromotionPath); RejectLinks(store.RecoveryPath);
            if (File.Exists(store.PromotionPath) || Directory.Exists(store.PromotionPath) || File.Exists(store.RecoveryPath) || Directory.Exists(store.RecoveryPath)) Volatile.Write(ref store.promotionUnknown, 1);
            if (!File.Exists(store.file)) return store;
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
            return store;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public MultiPeerInvitation? Get()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Volatile.Read(ref promotionUnknown) != 0) throw new InvalidOperationException("Peer promotion outcome is unknown; authenticated recovery is required.");
        var result = Volatile.Read(ref promotedInvitation) ?? Volatile.Read(ref invitation);
        if (Volatile.Read(ref promotionUnknown) != 0) throw new InvalidOperationException("Peer promotion outcome is unknown; authenticated recovery is required.");
        return result;
    }

    private void RejectPromotionMutationTest()
    {
        RejectLinks(PromotionPath); RejectLinks(RecoveryPath);
        if (Volatile.Read(ref promotionUnknown) != 0 || promotedInvitation is not null || File.Exists(PromotionPath) || Directory.Exists(PromotionPath) || File.Exists(RecoveryPath) || Directory.Exists(RecoveryPath))
            throw new InvalidOperationException("Peer promotion requires authenticated recovery.");
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
            RejectPromotionMutationTest();
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
                Volatile.Write(ref invitation, value);
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
        RecoveryActivationSession? stopping = null; Task? draining = null; TaskCompletionSource? completion = null; Task? existing;
        await gate.WaitAsync();
        try
        {
            existing = isolatedRecoveryDisposeTask;
            if (existing is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); isolatedRecoveryDisposeTask = completion.Task;
                disposed = true; invitation = null; promotedInvitation = null; stopping = activeRecoverySession; draining = activeRecoveryDrained?.Task;
            }
        }
        finally { gate.Release(); }
        if (existing is not null) { await existing; return; }
        try
        {
            Exception? stopError = null;
            try { stopping?.Stop(); } catch (Exception error) { stopError = error; }
            if (draining is not null) await draining;
            await lease.DisposeAsync();
            if (stopError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopError).Throw();
            completion!.TrySetResult();
        }
        catch (Exception error) { completion!.TrySetException(error); throw; }
    }
}
