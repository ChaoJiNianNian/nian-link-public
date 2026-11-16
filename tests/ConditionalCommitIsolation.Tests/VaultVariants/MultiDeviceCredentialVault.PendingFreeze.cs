using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Non-secret frozen invitation settings; the digest identifies contents, not identity or authorization.</summary>
internal sealed record PendingFrozenInvitationMetadata(Guid StageId, int Slot, string Host, int ControlPort,
    int KeyboardPort, int DataPortBase, int DataPort, string InvitationDigest)
{
    public override string ToString() => $"PendingFrozenInvitationMetadata {{ StageId = {StageId}, Slot = {Slot}, Host = [redacted] }}";
}

public sealed partial class MultiDeviceCredentialVault
{
    private sealed record FrozenInvitation(string Host, int ControlPort, int KeyboardPort, int DataPortBase,
        int DataPort, string InvitationDigest);
    private static readonly string[] FrozenFields = ["host", "controlPort", "keyboardPort", "dataPortBase", "dataPort", "invitationDigest"];

    /// <summary>The caller must separately authenticate and confirm the live session; this operation does not grant trust.</summary>
    internal Task<PendingFrozenInvitationMetadata> FreezePendingInvitationAsync(Guid stageId, PairingSessionIdentity identity,
        VerifiedLanBinding binding, int controlPort, int keyboardPort, int dataPortBase, DateTimeOffset now,
        CancellationToken ct = default, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return FreezePendingInvitationCoreAsync(stageId, identity, binding.Address, binding.IsStillAvailable,
            controlPort, keyboardPort, dataPortBase, now, ct, clock);
    }

    // Loopback-only entry for isolated tests. It opens no socket and cannot authorize a device.
    internal Task<PendingFrozenInvitationMetadata> FreezePendingInvitationForLoopbackTestAsync(Guid stageId, PairingSessionIdentity identity,
        IPAddress address, int controlPort, int keyboardPort, int dataPortBase, DateTimeOffset now,
        CancellationToken ct = default, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(address))
            throw new ArgumentException("Only IPv4 loopback is allowed for this isolated test.");
        return FreezePendingInvitationCoreAsync(stageId, identity, address, () => true,
            controlPort, keyboardPort, dataPortBase, now, ct, clock);
    }

    private async Task<PendingFrozenInvitationMetadata> FreezePendingInvitationCoreAsync(Guid stageId, PairingSessionIdentity identity,
        IPAddress address, Func<bool> bindingAvailable, int controlPort, int keyboardPort, int dataPortBase, DateTimeOffset now,
        CancellationToken ct, Func<DateTimeOffset>? clock)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(address);
        if (stageId == Guid.Empty || !IsValidFrozenAddress(address)) throw new ArgumentException("Invalid pending invitation binding.");
        ValidatePorts(controlPort, keyboardPort, dataPortBase);
        clock ??= () => DateTimeOffset.UtcNow;
        var lastCheck = now;
        void CheckDeadline(PendingGrantDocument pending)
        {
            var current = clock();
            if (current < lastCheck || current < pending.CreatedAtUtc) throw new ArgumentException("Clock moved backwards during invitation freeze.");
            ValidatePendingBinding(pending.Identity, pending.Offer, current);
            lastCheck = current;
        }
        void CheckBinding()
        {
            if (!bindingAvailable()) throw new InvalidOperationException("The approved LAN interface is unavailable.");
        }
        CheckBinding(); ct.ThrowIfCancellationRequested();
        await gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed(); RejectUnresolvedPrepareTest(); CheckBinding();
            var snapshot = await ReadPendingGrantSnapshotAsync(ct);
            var pending = snapshot.Document ?? throw new InvalidOperationException("A pending grant is required.");
            if (pending.StageId != stageId || pending.Identity != identity) throw new InvalidOperationException("The pending grant identity does not match.");
            CheckDeadline(pending); ct.ThrowIfCancellationRequested();
            if (ToMetadata(pending, lastCheck).HasConflict) throw new InvalidOperationException("The pending device or slot now conflicts with active trust.");
            var host = address.ToString();
            var dataPort = checked(dataPortBase + pending.Slot - 1);
            var invitation = ToInvitation(pending, host, controlPort, keyboardPort, dataPort);
            var frozen = new FrozenInvitation(host, controlPort, keyboardPort, dataPortBase, dataPort, PeerInvitationStore.ComputePendingDigest(invitation));
            if (pending.Frozen is not null)
            {
                if (pending.Frozen != frozen) throw new InvalidOperationException("Frozen invitation settings cannot be changed.");
                CheckDeadline(pending); CheckBinding(); ct.ThrowIfCancellationRequested();
                return FrozenMetadata(pending);
            }
            var next = pending with { Version = 2, Frozen = frozen };
            ValidatePendingGrant(next);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            byte[] ciphertext;
            try { ciphertext = protector.Protect(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            if (ciphertext.Length is <= 0 or > MaxFileBytes) throw new InvalidDataException("Protected pending grant is invalid or oversized.");
            var path = PendingGrantPath; var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                RejectLinks(directory); RejectLinks(path); RejectLinks(temp);
                await File.WriteAllBytesAsync(temp, ciphertext, ct);
                RejectLinks(path);
                var current = await ReadPendingGrantSnapshotAsync(ct);
                if (current.Ciphertext is null || snapshot.Ciphertext is null ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current.Ciphertext), SHA256.HashData(snapshot.Ciphertext)))
                    throw new InvalidOperationException("Pending grant changed during freeze.");
                CheckDeadline(pending); CheckBinding(); ct.ThrowIfCancellationRequested();
                File.Move(temp, path, true);
                return FrozenMetadata(next);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }

    private static PendingFrozenInvitationMetadata FrozenMetadata(PendingGrantDocument pending)
    {
        var frozen = pending.Frozen ?? throw new InvalidOperationException("Invitation is not frozen.");
        return new(pending.StageId, pending.Slot, frozen.Host, frozen.ControlPort, frozen.KeyboardPort,
            frozen.DataPortBase, frozen.DataPort, frozen.InvitationDigest);
    }

    private static MultiPeerInvitation ToInvitation(PendingGrantDocument pending, string host, int controlPort, int keyboardPort, int dataPort) => new()
    {
        Host = host, Port = controlPort, KeyboardPort = keyboardPort, DataPort = dataPort,
        HostName = pending.Identity.HostName, PeerName = pending.Identity.PeerName,
        CertificateSha256 = pending.Identity.HostFingerprint, Token = pending.Token
    };

    private static void ValidateFrozenInvitation(PendingGrantDocument pending)
    {
        var frozen = pending.Frozen ?? throw new InvalidDataException("Frozen invitation is missing.");
        if (!IPAddress.TryParse(frozen.Host, out var address) || !IsValidFrozenAddress(address) || address.ToString() != frozen.Host ||
            frozen.InvitationDigest is not { Length: 64 } || frozen.InvitationDigest.Any(c => !char.IsAsciiHexDigitUpper(c)))
            throw new InvalidDataException("Frozen invitation binding is invalid.");
        try
        {
            ValidatePorts(frozen.ControlPort, frozen.KeyboardPort, frozen.DataPortBase);
            if (frozen.DataPort != frozen.DataPortBase + pending.Slot - 1) throw new InvalidDataException("Frozen data port is invalid.");
            var expected = PeerInvitationStore.ComputePendingDigest(ToInvitation(pending, frozen.Host, frozen.ControlPort, frozen.KeyboardPort, frozen.DataPort));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(frozen.InvitationDigest)))
                throw new InvalidDataException("Frozen invitation digest is invalid.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("Frozen invitation is invalid.", error); }
    }

    private static bool IsValidFrozenAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(address)) return true; // Test-only records; never sufficient for production trust.
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }
    private static void ValidatePorts(int controlPort, int keyboardPort, int dataPortBase)
    {
        if (controlPort is < 1 or > 65535 || keyboardPort is < 1 or > 65535 || dataPortBase is < 1 or > 65532 ||
            controlPort == keyboardPort || controlPort >= dataPortBase && controlPort < dataPortBase + 4 ||
            keyboardPort >= dataPortBase && keyboardPort < dataPortBase + 4)
            throw new ArgumentException("Control, keyboard and data ports must be valid and not overlap.");
    }
}
