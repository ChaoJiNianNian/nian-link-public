using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Nian.Input;

internal sealed class InputProfile : IDisposable
{
    private readonly FileStream lease;
    public X509Certificate2 Certificate { get; }
    public byte[] Token { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData));
    private InputProfile(FileStream lease, X509Certificate2 certificate, byte[] token)
    { this.lease = lease; Certificate = certificate; Token = token; }
    public static async Task<InputProfile> OpenAsync(string path, CancellationToken ct)
    {
        CheckPath(path); Directory.CreateDirectory(path); Protect(path, directory: true);
        var leasePath = Path.Combine(path, ".receiver.lock"); CheckPath(leasePath);
        var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        X509Certificate2? certificate = null;
        try
        {
            Protect(lease.Name);
            var certPath = Path.Combine(path, "receiver.pfx"); CheckPath(certPath);
            if (!File.Exists(certPath))
            {
                using var key = RSA.Create(2048);
                var request = new CertificateRequest("CN=Nian Input Lab", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
                await WritePrivateAsync(certPath, generated.Export(X509ContentType.Pfx), ct);
            }
            Protect(certPath);
            // Schannel requires a temporary container; macOS does not support EphemeralKeySet.
            // Without PersistKeySet, the platform removes temporary key storage on disposal.
            var keyStorage = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
            certificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, null, keyStorage);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) throw new InvalidDataException("The isolated receiver certificate is unavailable or expired.");
            var tokenPath = Path.Combine(path, "receiver-token"); CheckPath(tokenPath);
            if (!File.Exists(tokenPath)) await WritePrivateAsync(tokenPath, System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), ct);
            Protect(tokenPath);
            if (!InputValidation.TryToken(await File.ReadAllTextAsync(tokenPath, ct), out var token)) throw new InvalidDataException("The isolated receiver token is invalid.");
            return new(lease, certificate, token);
        }
        catch { certificate?.Dispose(); lease.Dispose(); throw; }
    }
    public static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken ct)
    {
        CheckPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("The export directory must already exist.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            { Protect(temporary); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void CheckPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) == Path.GetFullPath(path)) throw new ArgumentException("Choose an absolute isolated profile or export path.");
        for (string? cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Input credentials cannot use linked paths.");
    }
    private static void Protect(string path, bool directory = false)
    {
        if (OperatingSystem.IsWindows()) ProtectWindows(path, directory);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : 0));
    }
    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); var user = identity.User ?? throw new IOException("The current user identity is unavailable.");
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security); else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
    public void Dispose() { Certificate.Dispose(); CryptographicOperations.ZeroMemory(Token); lease.Dispose(); }
}
