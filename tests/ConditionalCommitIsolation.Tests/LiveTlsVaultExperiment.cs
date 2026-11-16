using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

public sealed partial class MultiDeviceCredentialVault
{
    // Compiled only into the isolated test assembly, never the product assembly.
    internal void MarkLiveTestWriteUncertain() => Volatile.Write(ref writeUncertain, 1);
    internal void ValidateLiveTestPublishPaths(string temporary, string target) => IsolatedTestPathGuard.Validate(directory, temporary, target, "multi-device-trust.bin");
    internal ControlledLivePublishBarrier? BeforeLivePublish { get; set; }
    internal async Task<string> CommitLiveTlsIsolatedAsync(PairingTlsConsentSession session, object capability, Guid stage,
        PairingSessionIdentity identity, PeerIdentityOffer offer, PendingFrozenInvitationMetadata frozen,
        DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(clock);
        session.BeginLiveVaultTest(capability, identity, offer);
        try
        {
            using var deadline = session.CreateLiveVaultDeadline(ct);
            await gate.WaitAsync(deadline.Token);
            try
            {
                ThrowIfDisposed(); RejectUnresolvedPrepareTest(); session.CheckLiveVaultTest(capability, deadline.Token);
                var captured = await ReadPendingGrantSnapshotAsync(deadline.Token);
                var pending = captured.Document ?? throw new InvalidOperationException("Frozen pending grant is required.");
                if (pending.Frozen is null || pending.StageId != stage || pending.Identity != identity ||
                    pending.Offer != offer || FrozenMetadata(pending) != frozen)
                    throw new InvalidOperationException("Frozen pending grant does not match.");
                var previous = now;
                void Check()
                {
                    var currentTime = clock();
                    if (currentTime < previous || currentTime < pending.CreatedAtUtc) throw new InvalidOperationException("Clock moved backwards.");
                    ValidatePendingBinding(pending.Identity, pending.Offer, currentTime);
                    previous = currentTime; session.CheckLiveVaultTest(capability, deadline.Token);
                }
                Check();
                var current = vaultSnapshot;
                if (current.Transactions.Any(x => x.TransactionId == identity.TransactionId || x.StageId == stage ||
                    x.SessionId == identity.SessionId || x.OfferNonce == identity.OfferNonce))
                    throw new InvalidOperationException("Existing transaction cannot be reused.");
                if (current.Transactions.Length >= MaximumVaultTransactions || current.Devices.Length >= MultiDeviceCredentials.MaximumPeers ||
                    current.Devices.Any(x => string.Equals(x.Name, identity.PeerName, StringComparison.OrdinalIgnoreCase) ||
                        x.Fingerprint == identity.PeerFingerprint || x.Slot == pending.Slot))
                    throw new InvalidOperationException("Device, slot or ledger capacity conflict.");
                var devices = current.Devices.Select(x => x with { Token = CanonicalToken(x.Token) }).Append(
                    new Entry(identity.PeerName, identity.PeerFingerprint, pending.Token, pending.Offer.Platform, pending.Slot)).ToArray();
                var tx = new VaultTransaction(identity.TransactionId, stage, identity.SessionId, identity.OfferNonce,
                    identity.HostName, identity.HostFingerprint, identity.PeerName, identity.PeerFingerprint,
                    pending.Offer.Platform, pending.Slot, pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token), "committed", identity.ExpiresAtUtc);
                var next = new VaultSnapshot(2, devices, current.Transactions.Append(tx).ToArray());
                ValidateVaultSnapshot(next);
                var plain = JsonSerializer.SerializeToUtf8Bytes(new VaultV2Document(2, next.Devices, next.Transactions), StrictVaultJson);
                byte[] encrypted;
                try { encrypted = protector.Protect(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (encrypted.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Protected device trust is invalid or oversized.");
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    RejectLinks(directory); RejectLinks(file); RejectLinks(temporary);
                    await File.WriteAllBytesAsync(temporary, encrypted, deadline.Token);
                    RejectLinks(file);
                    var latest = await ReadPendingGrantSnapshotAsync(deadline.Token);
                    if (latest.Ciphertext is null || captured.Ciphertext is null ||
                        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(captured.Ciphertext), SHA256.HashData(latest.Ciphertext)))
                        throw new InvalidOperationException("Pending grant changed.");
                    Check();
                    if (BeforeLivePublish is not null) await BeforeLivePublish.PauseAsync();
                    Check();
                    session.PublishLiveVaultTest(capability, this, temporary, file, deadline.Token);
                    try { AfterIsolatedMove?.Invoke(); Volatile.Write(ref vaultSnapshot, next); }
                    catch { Volatile.Write(ref writeUncertain, 1); throw; }
                    return "committed";
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            finally { gate.Release(); }
        }
        finally { session.FinishLiveVaultTest(); }
    }
}
