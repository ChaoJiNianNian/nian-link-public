using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MacSyntheticMtlsProbe;

// The probe never accepts a certificate, key or credential from its caller.
public static class SyntheticCertificate
{
    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Nian Synthetic TLS Probe", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        var pem = X509Certificate2.CreateFromPem(generated.ExportCertificatePem(), rsa.ExportRSAPrivateKeyPem());
        if (!OperatingSystem.IsWindows()) return pem;
        using (pem)
        {
            var pfx = pem.Export(X509ContentType.Pkcs12);
            try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }
}

public static class CertificatePin
{
    public static string Get(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    public static bool IsValid(string? pin) => pin is { Length: 64 } && pin.All(Uri.IsHexDigit);
    public static bool Matches(string? expected, X509Certificate? certificate)
    {
        if (!IsValid(expected) || certificate is null) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected!), SHA256.HashData(certificate.GetRawCertData()));
    }
}
