using System.Security.Cryptography;
using DesktopToolbox.DeskLink;

namespace VaultGrantIdentity.Research;

internal enum ResearchResult { Accepted, Rejected, Unauthorized }

// In-memory research model only. Never used by an app or exposed over a network.
// All identities, tokens and vault files supplied by its fixture are synthetic.
internal sealed class GrantIdentityFence : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, byte[]> sealedOperations = [];
    private bool disposed;
    private const int Capacity = 64;
    // A blocking test hook deliberately forces the vault-lock ordering test.
    // There is no such callback in a product request path.
    internal Action? ResearchOnlyBeforePublish { get; set; }
    internal Task<ResearchResult> SealAsync(MultiDeviceCredentialVault vault, string peer, byte[] token, Guid operation) => ApplyAsync(vault, peer, token, operation, true);
    internal Task<ResearchResult> BeginAsync(MultiDeviceCredentialVault vault, string peer, byte[] token, Guid operation) => ApplyAsync(vault, peer, token, operation, false);
    // Pure test-only comparison of synthetic pending inputs; it grants no access.
    internal static bool ResearchOnlySameCandidate(byte[] first, byte[] second, TrustedDeviceIdentity identity)
    {
        var left = ResearchGrantMarker.Derive(first, identity); var right = ResearchGrantMarker.Derive(second, identity);
        try { return CryptographicOperations.FixedTimeEquals(left, right); }
        finally { CryptographicOperations.ZeroMemory(left); CryptographicOperations.ZeroMemory(right); }
    }

    private async Task<ResearchResult> ApplyAsync(MultiDeviceCredentialVault vault, string peer, byte[] token, Guid operation, bool seal)
    {
        ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(token);
        if (operation == Guid.Empty) return ResearchResult.Rejected;
        if (token.Length != 32) return ResearchResult.Unauthorized;
        // The verified bytes and derived identity must not be mutable by the caller
        // after validation. Only synthetic tokens are accepted by this fixture.
        var supplied = token.ToArray();
        try
        {
            var result = ResearchResult.Unauthorized;
            var authorized = await vault.TryCommitUniqueResearchAuthorizedAsync(peer, supplied, identity =>
            {
                // The snapshot arrives from the vault under its gate; this callback
                // never calls back into the vault or performs I/O.
                var marker = ResearchGrantMarker.Derive(supplied, identity);
                try
                {
                    ResearchOnlyBeforePublish?.Invoke();
                    lock (gate)
                    {
                        ObjectDisposedException.ThrowIf(disposed, this);
                        if (sealedOperations.TryGetValue(operation, out var prior))
                        {
                            result = seal && CryptographicOperations.FixedTimeEquals(prior, marker)
                                ? ResearchResult.Accepted : ResearchResult.Rejected;
                            return;
                        }
                        if (!seal) { result = ResearchResult.Accepted; return; } // No native action exists here.
                        if (sealedOperations.Count >= Capacity) { result = ResearchResult.Rejected; return; }
                        sealedOperations.Add(operation, marker.ToArray());
                        result = ResearchResult.Accepted;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(marker); }
            }).ConfigureAwait(false);
            return authorized switch
            {
                ResearchGrantAuthorization.Committed => result,
                ResearchGrantAuthorization.Ambiguous => ResearchResult.Rejected,
                _ => ResearchResult.Unauthorized
            };
        }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var value in sealedOperations.Values) CryptographicOperations.ZeroMemory(value);
            sealedOperations.Clear();
        }
    }
}
