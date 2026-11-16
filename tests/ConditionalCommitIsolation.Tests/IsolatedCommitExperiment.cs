using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

internal sealed record IsolatedGrantKey(PairingSessionIdentity Identity, Guid Stage, string Platform, int Slot, string InvitationDigest, string CredentialDigest)
{
    public override string ToString() => "IsolatedGrantKey { [redacted] }";
}
internal sealed record IsolatedCommitReceipt(string State, IsolatedGrantKey Locator)
{
    public override string ToString() => $"IsolatedCommitReceipt {{ State = {State} }}";
}

// This confirmation simulator and partial class compile only into the isolated test assembly.
// They provide no evidence of actual TLS or a real human's consent.
internal sealed class IsolatedConfirmation
{
    private IsolatedConfirmation(Flow owner) => Owner = owner;
    internal Flow Owner { get; }

    internal sealed class Flow
    {
        private readonly object sync = new();
        private readonly SemaphoreSlim lifetime = new(1, 1);
        private readonly Func<DateTimeOffset> clock;
        private DateTimeOffset last;
        private bool local, remote, issued, closed;
        internal PairingSessionIdentity Identity { get; }
        internal PeerIdentityOffer Offer { get; }
        internal Guid Stage { get; }
        internal PendingFrozenInvitationMetadata Frozen { get; }
        internal Flow(PairingSessionIdentity identity, PeerIdentityOffer offer, Guid stage, PendingFrozenInvitationMetadata frozen, Func<DateTimeOffset> clock)
        {
            ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(offer);
            Identity = identity; Offer = offer; Stage = stage; Frozen = frozen; this.clock = clock;
            last = clock();
            if (stage == Guid.Empty || stage != frozen.StageId || last >= identity.ExpiresAtUtc) throw new ArgumentException("Invalid isolated flow.");
            offer.Validate(last); _ = new PairingConsentState(identity, () => last);
            if (!Guid.TryParseExact(offer.Nonce, "N", out var nonce) || nonce != identity.OfferNonce || offer.Name != identity.PeerName ||
                !DeskLinkFingerprint.Matches(identity.PeerFingerprint, offer.KeyboardFingerprint) || identity.ExpiresAtUtc > offer.ExpiresAtUtc)
                throw new ArgumentException("Offer does not match the isolated session.");
        }
        internal void ConfirmLocal(PairingSessionIdentity identity) { lock (sync) { Check(identity); if (local) throw new InvalidOperationException(); local = true; } }
        internal void ConfirmRemote(PairingSessionIdentity identity) { lock (sync) { Check(identity); if (remote) throw new InvalidOperationException(); remote = true; } }
        internal IsolatedConfirmation Issue()
        {
            lock (sync)
            {
                Check(Identity);
                if (!local || !remote || issued) throw new InvalidOperationException("Isolated confirmation is incomplete.");
                issued = true; return new(this);
            }
        }
        private void Check(PairingSessionIdentity identity)
        {
            var current = clock();
            if (closed || identity != Identity || current < last || current >= Identity.ExpiresAtUtc) throw new InvalidOperationException("Isolated confirmation is unavailable.");
            last = current;
        }
        internal async Task EnterAsync(CancellationToken ct) { await lifetime.WaitAsync(ct); try { lock (sync) { Check(Identity); if (!issued) throw new InvalidOperationException(); } } catch { lifetime.Release(); throw; } }
        internal void Recheck() { lock (sync) Check(Identity); }
        internal void Exit() => lifetime.Release();
        internal async Task CloseAsync() { await lifetime.WaitAsync(); try { lock (sync) closed = true; } finally { lifetime.Release(); } }
    }
}

public sealed partial class MultiDeviceCredentialVault
{
    internal Action? AfterIsolatedMove { get; set; }
    internal async Task<IsolatedCommitReceipt> RecoverFromPendingIsolatedAsync(IsolatedConfirmation confirmation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var flow = confirmation.Owner;
        await flow.EnterAsync(ct);
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                ThrowIfDisposed(); flow.Recheck();
                var pending = await ReadPendingGrantAsync(ct);
                if (pending is null || pending.Frozen is null || pending.StageId != flow.Stage || pending.Identity != flow.Identity || pending.Offer != flow.Offer || FrozenMetadata(pending) != flow.Frozen)
                    throw new InvalidOperationException("Pending recovery identity does not match.");
                var key = new IsolatedGrantKey(pending.Identity, pending.StageId, pending.Offer.Platform, pending.Slot, pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token));
                var state = InspectIsolatedCore(key);
                if (state != "committed") throw new InvalidOperationException("No current committed grant is available.");
                flow.Recheck(); ct.ThrowIfCancellationRequested();
                return new(state, key);
            }
            finally { gate.Release(); }
        }
        finally { flow.Exit(); }
    }
    internal async Task<IsolatedCommitReceipt> CommitAndLocateIsolatedAsync(IsolatedConfirmation confirmation, DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
    {
        await CommitIsolatedAsync(confirmation, now, clock, ct);
        var flow = confirmation.Owner;
        await flow.EnterAsync(ct);
        try
        {
            var receipt = await LocatePendingResultAsync(flow.Stage, flow.Identity, "committed", ct);
            flow.Recheck(); ct.ThrowIfCancellationRequested();
            return receipt;
        }
        finally { flow.Exit(); }
    }
    internal async Task<string> ConfirmExistingIsolatedAsync(IsolatedConfirmation confirmation, IsolatedGrantKey locator, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation); ArgumentNullException.ThrowIfNull(locator);
        var flow = confirmation.Owner;
        await flow.EnterAsync(ct);
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                ThrowIfDisposed(); flow.Recheck();
                if (locator.Identity != flow.Identity || locator.Stage != flow.Stage || locator.Slot != flow.Frozen.Slot || locator.Platform != flow.Offer.Platform ||
                    !IsVaultDigest(locator.InvitationDigest) || !FixedDigestEquals(locator.InvitationDigest, flow.Frozen.InvitationDigest) ||
                    InspectIsolatedCore(locator) != "committed") throw new InvalidOperationException("Current committed grant cannot be verified.");
                return "committed";
            }
            finally { gate.Release(); }
        }
        finally { flow.Exit(); }
    }
    internal async Task<IsolatedCommitReceipt> AbortAndLocateIsolatedAsync(Guid stage, PairingSessionIdentity identity, CancellationToken ct = default)
    {
        await AbortIsolatedAsync(stage, identity, ct);
        return await LocatePendingResultAsync(stage, identity, "aborted", ct);
    }
    private async Task<IsolatedCommitReceipt> LocatePendingResultAsync(Guid stage, PairingSessionIdentity identity, string expected, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var pending = await ReadPendingGrantAsync(ct);
            if (pending is null || pending.Frozen is null || pending.StageId != stage || pending.Identity != identity)
                throw new InvalidOperationException("Transaction result requires recovery; pending data changed.");
            var key = new IsolatedGrantKey(identity, stage, pending.Offer.Platform, pending.Slot, pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token));
            if (InspectIsolatedCore(key) != expected) throw new InvalidOperationException("Transaction result requires recovery; current state changed.");
            return new(expected, key);
        }
        finally { gate.Release(); }
    }
    internal async Task<string> InspectIsolatedAsync(IsolatedGrantKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            return InspectIsolatedCore(key);
        }
        finally { gate.Release(); }
    }
    private string InspectIsolatedCore(IsolatedGrantKey key)
    {
        var tx = vaultSnapshot.Transactions.SingleOrDefault(x => x.TransactionId == key.Identity.TransactionId);
        if (tx is null || !MatchesKey(tx, key)) throw new InvalidOperationException("Transaction cannot be verified.");
        if (tx.State == "committed" && !vaultSnapshot.Devices.Any(x => x.Name == key.Identity.PeerName && x.Fingerprint == key.Identity.PeerFingerprint &&
            x.Platform == key.Platform && x.Slot == key.Slot && FixedDigestEquals(VaultTokenDigest(x.Token), key.CredentialDigest)))
            throw new InvalidOperationException("Current grant cannot be verified.");
        return tx.State;
    }
    internal async Task<string> AbortIsolatedAsync(Guid stage, PairingSessionIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); RejectUnresolvedPrepareTest();
            var pending = await ReadPendingGrantAsync(ct) ?? throw new InvalidOperationException("Frozen pending grant is required.");
            if (pending.StageId != stage || pending.Identity != identity || pending.Frozen is null) throw new InvalidOperationException("Pending identity does not match.");
            var key = new IsolatedGrantKey(identity, stage, pending.Offer.Platform, pending.Slot, pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token));
            var current = vaultSnapshot;
            var prior = current.Transactions.Where(x => x.TransactionId == identity.TransactionId || x.StageId == stage ||
                x.SessionId == identity.SessionId || x.OfferNonce == identity.OfferNonce).ToArray();
            if (prior.Length != 0)
            {
                if (prior.Length == 1 && prior[0].State == "aborted" && MatchesKey(prior[0], key)) return "aborted";
                throw new InvalidOperationException("Transaction cannot be cancelled.");
            }
            if (current.Transactions.Length >= MaximumVaultTransactions) throw new InvalidOperationException("Ledger capacity reached.");
            var tx = new VaultTransaction(identity.TransactionId, stage, identity.SessionId, identity.OfferNonce, identity.HostName,
                identity.HostFingerprint, identity.PeerName, identity.PeerFingerprint, pending.Offer.Platform, pending.Slot,
                pending.Frozen.InvitationDigest, key.CredentialDigest, "aborted", identity.ExpiresAtUtc);
            var next = new VaultSnapshot(2, current.Devices.Select(x => x with { Token = CanonicalToken(x.Token) }).ToArray(), current.Transactions.Append(tx).ToArray());
            await CommitAsync(next, ct);
            Volatile.Write(ref vaultSnapshot, next);
            return "aborted";
        }
        finally { gate.Release(); }
    }
    internal async Task<string> RevokeIsolatedAsync(IsolatedGrantKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            var current = vaultSnapshot;
            var tx = current.Transactions.SingleOrDefault(x => x.TransactionId == key.Identity.TransactionId);
            if (tx is null || !MatchesKey(tx, key)) throw new InvalidOperationException("Grant identity does not match.");
            if (tx.State == "revoked") return "revoked";
            if (tx.State != "committed") throw new InvalidOperationException("Grant is not active.");
            var device = current.Devices.SingleOrDefault(x => x.Name == key.Identity.PeerName && x.Fingerprint == key.Identity.PeerFingerprint &&
                x.Platform == key.Platform && x.Slot == key.Slot && FixedDigestEquals(VaultTokenDigest(x.Token), key.CredentialDigest));
            if (device is null) throw new InvalidOperationException("Current grant does not match.");
            var next = current with { Devices = current.Devices.Where(x => !ReferenceEquals(x, device)).ToArray(),
                Transactions = current.Transactions.Select(x => ReferenceEquals(x, tx) ? x with { State = "revoked" } : x).ToArray() };
            await CommitAsync(next, ct);
            Volatile.Write(ref vaultSnapshot, next);
            return "revoked";
        }
        finally { gate.Release(); }
    }

    private static bool MatchesKey(VaultTransaction tx, IsolatedGrantKey key) =>
        tx.TransactionId == key.Identity.TransactionId && tx.StageId == key.Stage && tx.SessionId == key.Identity.SessionId &&
        tx.OfferNonce == key.Identity.OfferNonce && tx.HostName == key.Identity.HostName && tx.HostFingerprint == key.Identity.HostFingerprint &&
        tx.PeerName == key.Identity.PeerName && tx.PeerFingerprint == key.Identity.PeerFingerprint && tx.ExpiresAtUtc == key.Identity.ExpiresAtUtc &&
        tx.Platform == key.Platform && tx.Slot == key.Slot && IsVaultDigest(key.InvitationDigest) && IsVaultDigest(key.CredentialDigest) &&
        FixedDigestEquals(tx.InvitationDigest, key.InvitationDigest) && FixedDigestEquals(tx.CredentialDigest, key.CredentialDigest);
    internal async Task<string> CommitIsolatedAsync(IsolatedConfirmation confirmation, DateTimeOffset now, Func<DateTimeOffset> clock, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation); ArgumentNullException.ThrowIfNull(clock);
        var flow = confirmation.Owner;
        await flow.EnterAsync(ct);
        try
        {
            await gate.WaitAsync(ct);
            try
            {
                ThrowIfDisposed(); RejectUnresolvedPrepareTest();
                var captured = await ReadPendingGrantSnapshotAsync(ct);
                var pending = captured.Document ?? throw new InvalidOperationException("Frozen pending grant is required.");
                if (pending.Frozen is null || pending.StageId != flow.Stage || pending.Identity != flow.Identity || pending.Offer != flow.Offer || FrozenMetadata(pending) != flow.Frozen)
                    throw new InvalidOperationException("Isolated confirmation does not match the frozen grant.");
                var previous = now;
                void Check()
                {
                    var current = clock();
                    if (current < previous || current < pending.CreatedAtUtc) throw new InvalidOperationException("Clock moved backwards.");
                    ValidatePendingBinding(pending.Identity, pending.Offer, current);
                    previous = current; flow.Recheck(); ct.ThrowIfCancellationRequested();
                }
                Check();
                var current = vaultSnapshot;
                var key = new IsolatedGrantKey(pending.Identity, pending.StageId, pending.Offer.Platform, pending.Slot,
                    pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token));
                var prior = current.Transactions.Where(x => x.TransactionId == pending.Identity.TransactionId || x.StageId == pending.StageId ||
                    x.SessionId == pending.Identity.SessionId || x.OfferNonce == pending.Identity.OfferNonce).ToArray();
                if (prior.Length != 0)
                {
                    if (prior.Length != 1 || prior[0].State != "committed" || !MatchesKey(prior[0], key) ||
                        !current.Devices.Any(x => x.Name == pending.Identity.PeerName && x.Fingerprint == pending.Identity.PeerFingerprint &&
                            x.Platform == pending.Offer.Platform && x.Slot == pending.Slot && FixedDigestEquals(VaultTokenDigest(x.Token), key.CredentialDigest)))
                        throw new InvalidOperationException("Existing transaction does not match the current grant.");
                    Check();
                    return "committed";
                }
                if (current.Transactions.Length >= MaximumVaultTransactions || current.Devices.Length >= MultiDeviceCredentials.MaximumPeers ||
                    current.Devices.Any(x => string.Equals(x.Name, pending.Identity.PeerName, StringComparison.OrdinalIgnoreCase) ||
                        x.Fingerprint == pending.Identity.PeerFingerprint || x.Slot == pending.Slot))
                    throw new InvalidOperationException("Device, slot or ledger capacity conflict.");
                var devices = current.Devices.Select(x => x with { Token = CanonicalToken(x.Token) }).Append(
                    new Entry(pending.Identity.PeerName, pending.Identity.PeerFingerprint, pending.Token, pending.Offer.Platform, pending.Slot)).ToArray();
                var tx = new VaultTransaction(pending.Identity.TransactionId, pending.StageId, pending.Identity.SessionId,
                    pending.Identity.OfferNonce, pending.Identity.HostName, pending.Identity.HostFingerprint,
                    pending.Identity.PeerName, pending.Identity.PeerFingerprint, pending.Offer.Platform, pending.Slot,
                    pending.Frozen.InvitationDigest, VaultTokenDigest(pending.Token), "committed", pending.Identity.ExpiresAtUtc);
                var next = new VaultSnapshot(2, devices, current.Transactions.Append(tx).ToArray());
                ValidateVaultSnapshot(next);
                await WriteIsolatedSnapshotAsync(next, captured.Ciphertext!, Check, ct);
                try { AfterIsolatedMove?.Invoke(); Volatile.Write(ref vaultSnapshot, next); }
                catch { Volatile.Write(ref writeUncertain, 1); throw; }
                return "committed";
            }
            finally { gate.Release(); }
        }
        finally { flow.Exit(); }
    }

    private static string CanonicalToken(string value)
    {
        var token = Convert.FromBase64String(value);
        try { return Convert.ToBase64String(token); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private async Task WriteIsolatedSnapshotAsync(VaultSnapshot next, byte[] expectedPending, Action check, CancellationToken ct)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new VaultV2Document(2, next.Devices, next.Transactions), StrictVaultJson);
        byte[] encrypted;
        try { encrypted = protector.Protect(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Protected device trust is invalid or oversized.");
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            RejectLinks(directory); RejectLinks(file); RejectLinks(temp);
            await File.WriteAllBytesAsync(temp, encrypted, ct);
            RejectLinks(file);
            var latest = await ReadPendingGrantSnapshotAsync(ct);
            if (latest.Ciphertext is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(expectedPending), SHA256.HashData(latest.Ciphertext)))
                throw new InvalidOperationException("Pending grant changed.");
            check();
            try { File.Move(temp, file, true); }
            catch { Volatile.Write(ref writeUncertain, 1); throw; }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
