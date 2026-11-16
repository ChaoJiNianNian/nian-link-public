using System.IO;
using System.Security.Cryptography;
using System.Text;
using DesktopToolbox.DeskLink;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

/// <summary>Encrypts local multi-device credentials with a per-profile Keychain key.</summary>
public sealed class MacMultiPeerProtector : IMultiDeviceSecretProtector, IDisposable
{
    private static readonly byte[] Header = [0x4e, 0x4c, 0x4d, 1];
    private readonly byte[] key, associated;
    private bool disposed;

    private MacMultiPeerProtector(byte[] key, byte[] associated) { this.key = key; this.associated = associated; }

    public static async Task<MacMultiPeerProtector> OpenAsync(string directory, IIdentityVault vault, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vault);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("A private absolute directory is required.");
        directory = Path.GetFullPath(directory); RejectLinks(directory); Directory.CreateDirectory(directory); RejectLinks(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = Path.Combine(directory, ".keychain-key.lock"); RejectLinks(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var associated = SHA256.HashData(Encoding.UTF8.GetBytes("nian-link-multi-device-v1:" + directory));
        var vaultKey = Convert.ToHexString(associated);
        var secret = await vault.LoadAsync(vaultKey, ct);
        if (secret is null)
        {
            if (File.Exists(Path.Combine(directory, "peer-invitation.bin")) || File.Exists(Path.Combine(directory, "device-settings.bin")) ||
                File.Exists(Path.Combine(directory, "peer-pending-invitation.bin")) || Directory.Exists(Path.Combine(directory, "peer-pending-invitation.bin")))
                throw new CryptographicException("Keychain key is unavailable; existing pairing data was retained.");
            secret = RandomNumberGenerator.GetBytes(32);
            try { await vault.StoreAsync(vaultKey, secret, ct); }
            catch { CryptographicOperations.ZeroMemory(secret); throw; }
        }
        if (secret.Length != 32) { CryptographicOperations.ZeroMemory(secret); throw new CryptographicException("Keychain key is invalid."); }
        return new MacMultiPeerProtector(secret, associated);
    }

    public byte[] Protect(byte[] plaintext)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(plaintext);
        var output = new byte[checked(Header.Length + 12 + 16 + plaintext.Length)];
        Header.CopyTo(output, 0);
        var nonce = output.AsSpan(Header.Length, 12); RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(32), output.AsSpan(16, 16), associated);
        return output;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(ciphertext);
        if (ciphertext.Length < 32 || !ciphertext.AsSpan(0, 4).SequenceEqual(Header)) throw new CryptographicException("Protected pairing data is invalid.");
        var plain = new byte[ciphertext.Length - 32];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(ciphertext.AsSpan(4, 12), ciphertext.AsSpan(32), ciphertext.AsSpan(16, 16), plain, associated);
            return plain;
        }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Keychain profile cannot use links."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    public void Dispose() { if (!disposed) { disposed = true; CryptographicOperations.ZeroMemory(key); } }
}
