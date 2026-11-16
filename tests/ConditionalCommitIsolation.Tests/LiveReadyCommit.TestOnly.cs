using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

// The READY-to-commit experiment is built only into the isolated test assembly.
public sealed partial class MultiDeviceCredentialVault
{
    internal ControlledLivePublishBarrier? AfterReadyPendingRead { get; set; }
    private async Task<string> CommitReadyTestAsync(PairingTlsConsentSession session, object capability, object permit,
        LivePreparePayload payload, PairingSessionIdentity identity, PeerIdentityOffer offer,
        DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); session.CheckLiveVaultTest(capability, ct);
            if (!ReferenceEquals(activePrepareSession, session) || !ReferenceEquals(activePreparePermit, permit) ||
                !ReferenceEquals(activePreparePayload, payload) || !activePrepareStarted || activePrepareMarkerHash is null || activePreparePendingHash is null)
                throw new InvalidOperationException("READY does not belong to the active PREPARE lease.");
            session.ConsumeReadyCommitTest(capability, payload, ct);
            var captured = await ReadPendingGrantSnapshotAsync(ct);
            if (AfterReadyPendingRead is not null) await AfterReadyPendingRead.PauseAsync();
            var pending = captured.Document ?? throw new InvalidOperationException("Missing frozen grant.");
            if (captured.Ciphertext is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(captured.Ciphertext), activePreparePendingHash) || pending.Frozen is null || pending.Identity != identity || pending.Offer != offer ||
                pending.StageId != payload.StageId || pending.Slot != payload.Slot || pending.Frozen.DataPortBase != payload.DataPortBase ||
                !FixedDigestEquals(pending.Frozen.InvitationDigest, payload.Digest) ||
                ToInvitation(pending, pending.Frozen.Host, pending.Frozen.ControlPort, pending.Frozen.KeyboardPort, pending.Frozen.DataPort) != payload.Invitation)
                throw new InvalidOperationException("READY does not match the frozen grant.");
            ValidatePendingGrant(pending); ValidateFrozenInvitation(pending);
            var last = now;
            void Check()
            {
                var current = clock();
                if (current < last || current < pending.CreatedAtUtc) throw new InvalidOperationException("Clock moved backwards.");
                ValidatePendingBinding(pending.Identity, pending.Offer, current); last = current;
                session.CheckLiveVaultTest(capability, ct);
            }
            async Task CheckFiles()
            {
                RejectLinks(PrepareMarkerPath);
                var marker = await ReadBoundedVaultFileAsync(PrepareMarkerPath, ct);
                var current = await ReadPendingGrantSnapshotAsync(ct);
                if (current.Ciphertext is null ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(marker), activePrepareMarkerHash) ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current.Ciphertext), activePreparePendingHash))
                    throw new InvalidOperationException("READY files changed.");
                Check();
            }
            await CheckFiles();
            var current = vaultSnapshot;
            if (current.Transactions.Any(x => x.TransactionId == identity.TransactionId || x.StageId == payload.StageId ||
                    x.SessionId == identity.SessionId || x.OfferNonce == identity.OfferNonce) ||
                current.Transactions.Length >= MaximumVaultTransactions || current.Devices.Length >= MultiDeviceCredentials.MaximumPeers ||
                current.Devices.Any(x => string.Equals(x.Name, identity.PeerName, StringComparison.OrdinalIgnoreCase) ||
                    x.Fingerprint == identity.PeerFingerprint || x.Slot == pending.Slot))
                throw new InvalidOperationException("Device or transaction conflicts with current vault.");
            var devices = current.Devices.Select(x => x with { Token = CanonicalToken(x.Token) }).Append(
                new Entry(identity.PeerName, identity.PeerFingerprint, pending.Token, pending.Offer.Platform, pending.Slot)).ToArray();
            var tx = new VaultTransaction(identity.TransactionId, payload.StageId, identity.SessionId, identity.OfferNonce,
                identity.HostName, identity.HostFingerprint, identity.PeerName, identity.PeerFingerprint, pending.Offer.Platform, pending.Slot,
                pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token), "committed", identity.ExpiresAtUtc);
            var next = new VaultSnapshot(2, devices, current.Transactions.Append(tx).ToArray()); ValidateVaultSnapshot(next);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new VaultV2Document(2, next.Devices, next.Transactions), StrictVaultJson);
            byte[] encrypted;
            try { encrypted = protector.Protect(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Invalid protected vault.");
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                RejectLinks(directory); RejectLinks(file); RejectLinks(temporary);
                await File.WriteAllBytesAsync(temporary, encrypted, ct);
                if (BeforeLivePublish is not null) await BeforeLivePublish.PauseAsync();
                await CheckFiles();
                session.PublishLiveVaultTest(capability, this, temporary, file, ct);
                try { AfterIsolatedMove?.Invoke(); Volatile.Write(ref vaultSnapshot, next); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
                return "committed";
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
}
