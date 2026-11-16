using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DesktopToolbox.DeskLink;

internal static class PrivateFiles
{
    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static async Task WriteAsync(string path, string contents, CancellationToken ct)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static X509Certificate2 LoadCertificate(string path) => X509Certificate2.CreateFromPemFile(path, path);
    public static X509Certificate2 LoadTlsCertificate(string path)
    {
        using var pem = LoadCertificate(path);
        if (!OperatingSystem.IsWindows()) return LoadCertificate(path);
        // Schannel cannot use PEM's ephemeral key handle. Import a temporary user-key
        // PKCS#12; no PersistKeySet, so disposal removes the imported key container.
        var pfx = pem.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public static async Task EnsureCertificateAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path)) return;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Nian DeskLink", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
        await WriteAsync(path, cert.ExportCertificatePem() + "\n" + rsa.ExportRSAPrivateKeyPem() + "\n", ct);
    }
}

public static class DeskLinkFingerprint
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var clean = value.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (clean.Length != 64 || clean.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("A SHA-256 certificate fingerprint must contain 64 hexadecimal digits.", nameof(value));
        return clean.ToUpperInvariant();
    }
    public static string OfCertificate(X509Certificate2 certificate) => Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
    public static bool Matches(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Normalize(expected)), Convert.FromHexString(Normalize(actual)));
}
