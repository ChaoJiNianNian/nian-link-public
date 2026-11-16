using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Isolated, per-device data service prototype; not wired to the production UI.</summary>
public static class MultiPeerCollaborationEndpoint
{
    public static CollaborationServer Create(MultiDeviceCredentialVault vault, string peer, string root,
        string certificateFile, IPAddress bindAddress, int port, string? receiveDirectory = null,
        IReadOnlyList<SharedDirectory>? shares = null) =>
        CreateCore(vault, peer, root, certificateFile, bindAddress, port, receiveDirectory, shares, null, false);

    internal static CollaborationServer CreateForLan(MultiDeviceCredentialVault vault, string peer, string root,
        string certificateFile, VerifiedLanBinding binding, int port, string? receiveDirectory = null,
        IReadOnlyList<SharedDirectory>? shares = null) =>
        CreateCore(vault, peer, root, certificateFile, binding.Address, port, receiveDirectory, shares, binding, false);

    internal static CollaborationServer CreateForReverseShareTest(MultiDeviceCredentialVault vault, string peer, string root, string certificateFile) =>
        CreateCore(vault, peer, root, certificateFile, IPAddress.Loopback, 0, null, null, null, true);

    private static CollaborationServer CreateCore(MultiDeviceCredentialVault vault, string peer, string root,
        string certificateFile, IPAddress bindAddress, int port, string? receiveDirectory,
        IReadOnlyList<SharedDirectory>? shares, VerifiedLanBinding? binding, bool reverseShareTest)
    {
        ArgumentNullException.ThrowIfNull(vault);
        DeskflowProfile.ValidateName(peer);
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(certificateFile))
            throw new ArgumentException("Absolute private paths are required.");
        if (bindAddress is null || !IPAddress.IsLoopback(bindAddress) && (binding is null || !binding.Address.Equals(bindAddress)))
            throw new ArgumentException("A verified LAN interface is required.", nameof(bindAddress));
        if (binding is not null && !binding.IsStillAvailable())
            throw new InvalidOperationException("The approved LAN interface is unavailable.");
        // Each grant owns its own persisted queue. Re-pairing the same name
        // must never expose a previous grant's pending files.
        var initialToken = vault.ReadTokenForEndpoint(peer);
        byte[] tokenHash;
        string identifier;
        try
        {
            tokenHash = SHA256.HashData(initialToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes("nian-link-data-v1:" + peer.ToUpperInvariant() + ":"));
            hash.AppendData(initialToken);
            identifier = Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(initialToken); }
        var profile = Path.Combine(Path.GetFullPath(root), identifier);
        RejectLinks(profile);
        RejectLinks(certificateFile);
        Directory.CreateDirectory(profile);
        RejectLinks(profile);
        return new CollaborationServer(new()
        {
            ProfileDirectory = profile,
            CertificateFile = certificateFile,
            TokenProvider = ct =>
            {
                ct.ThrowIfCancellationRequested();
                if (binding is not null && !binding.IsStillAvailable())
                    throw new UnauthorizedAccessException("The approved LAN interface is unavailable.");
                try
                {
                    var current = vault.ReadTokenForEndpoint(peer);
                    var currentHash = SHA256.HashData(current);
                    if (!CryptographicOperations.FixedTimeEquals(currentHash, tokenHash))
                    {
                        CryptographicOperations.ZeroMemory(current);
                        throw new UnauthorizedAccessException("The device pairing has changed.");
                    }
                    return Task.FromResult(current);
                }
                catch (ObjectDisposedException error) { throw new UnauthorizedAccessException("Device trust is unavailable.", error); }
            },
            ReverseShareTestVault = reverseShareTest ? vault : null,
            ReverseShareTestPeer = reverseShareTest ? peer : null,
            BindAddress = bindAddress,
            Port = port,
            ReceiveDirectory = receiveDirectory,
            SharedDirectories = shares ?? []
        });
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Data endpoint paths cannot contain links."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
