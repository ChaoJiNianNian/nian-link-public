#if NIAN_FENCE_RESEARCH
using System.Security.Cryptography;
using System.Globalization;
using System.Text;

namespace DesktopToolbox.DeskLink;

// Research-only status: never a network response or a production authorization API.
internal enum ResearchGrantAuthorization { Denied, Ambiguous, Committed }

// Shared only by test-compiled research. This is not a persistent grant epoch.
internal static class ResearchGrantMarker
{
    internal static byte[] Derive(byte[] token, TrustedDeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(identity);
        if (token.Length != 32) throw new ArgumentException("Invalid synthetic credential length.");
        DeskflowProfile.ValidateName(identity.Name);
        var info = Encoding.UTF8.GetBytes("nian-link/research/grant-identity/v1\0" + identity.Name.ToUpperInvariant() + "\0" +
            identity.Fingerprint + "\0" + identity.Platform + "\0" + identity.Slot.ToString(CultureInfo.InvariantCulture));
        try { return HMACSHA256.HashData(token, info); }
        finally { CryptographicOperations.ZeroMemory(info); }
    }
}

public sealed partial class MultiDeviceCredentialVault
{
    // The callback receives only this already-authenticated, non-token snapshot.
    // It must be short, synchronous, and must not do I/O or reenter the vault.
    internal async Task<ResearchGrantAuthorization> TryCommitUniqueResearchAuthorizedAsync(
        string name, byte[] expectedToken, Action<TrustedDeviceIdentity> commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedToken);
        ArgumentNullException.ThrowIfNull(commit);
        if (string.IsNullOrEmpty(name) || expectedToken.Length != 32) return ResearchGrantAuthorization.Denied;
        var supplied = expectedToken.ToArray();
        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                if (Volatile.Read(ref writeUncertain) != 0) return ResearchGrantAuthorization.Denied;
                var entries = vaultSnapshot.Devices;
                var requested = entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
                if (requested is null) return ResearchGrantAuthorization.Denied;
                var matching = 0; var requestedMatches = false;
                foreach (var entry in entries)
                {
                    var current = Convert.FromBase64String(entry.Token);
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(current, supplied)) continue;
                        matching++;
                        if (ReferenceEquals(entry, requested)) requestedMatches = true;
                    }
                    finally { CryptographicOperations.ZeroMemory(current); }
                }
                if (!requestedMatches) return ResearchGrantAuthorization.Denied;
                if (matching != 1) return ResearchGrantAuthorization.Ambiguous;
                ct.ThrowIfCancellationRequested();
                commit(new TrustedDeviceIdentity(requested.Name, requested.Fingerprint, requested.Platform, requested.Slot));
                return ResearchGrantAuthorization.Committed;
            }
            finally { gate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }
}
#endif
