using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Nian.Input.Security;

public sealed class DeviceIdentity : IDisposable
{
    private sealed record Metadata(int Version,string DeviceName,string Fingerprint,string Certificate,string VaultKey);
    private DeviceIdentity(X509Certificate2 certificate,string name) { Certificate=certificate; DeviceName=name; Fingerprint=Convert.ToHexString(SHA256.HashData(certificate.RawData)); }
    internal X509Certificate2 Certificate {get;}
    public string DeviceName {get;}
    public string Fingerprint {get;}
    public static async Task<DeviceIdentity> OpenAsync(string profileDirectory,string deviceName,IIdentityVault? vault=null,CancellationToken cancellationToken=default)
    {
        if(!TrustValidation.DeviceName(deviceName))throw new ArgumentException("The device name is invalid.");
        PrivateFiles.Check(profileDirectory);var path=Path.GetFullPath(profileDirectory); PrivateFiles.Directory(path);
        vault??=OperatingSystem.IsWindows()?new WindowsIdentityVault(path):OperatingSystem.IsMacOS()?new MacKeychainIdentityVault():throw new PlatformNotSupportedException("A system identity vault is required.");
        var metadataPath=Path.Combine(path,"identity.json"); var key=TrustValidation.VaultKey(path); var lockPath=Path.Combine(path,".identity.lock"); PrivateFiles.Check(lockPath);
        using var lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); PrivateFiles.Protect(lockPath);
        byte[]? secret=null; X509Certificate2? certificate=null;
        try
        {
            secret=await vault.LoadAsync(key,cancellationToken);
            if(File.Exists(metadataPath))
            {
                var metadata=JsonSerializer.Deserialize<Metadata>(PrivateFiles.Read(metadataPath),TrustJson.Options)??throw new InvalidDataException("Device identity metadata is empty.");
                if(metadata.Version!=2 || !TrustValidation.DeviceName(metadata.DeviceName) || !TrustValidation.Fingerprint(metadata.Fingerprint) || metadata.VaultKey!=key || secret is null)throw new InvalidDataException("Device identity is damaged or its private key is unavailable.");
                certificate=Load(secret);
                if(!TrustValidation.Matches(metadata.Fingerprint,certificate.RawData) || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(metadata.Certificate),certificate.RawData))throw new InvalidDataException("Device identity metadata does not match its private key.");
                var result=new DeviceIdentity(certificate,metadata.DeviceName); certificate=null;return result;
            }
            if(secret is not null)throw new InvalidDataException("The private identity exists without its metadata; recovery requires an explicit decision.");
            using var rsa=RSA.Create(2048); var request=new CertificateRequest("CN=Nian Independent Desktop Device",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new("1.3.6.1.5.5.7.3.1"),new("1.3.6.1.5.5.7.3.2")},true));
            using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2)); secret=generated.Export(X509ContentType.Pkcs12);
            await vault.StoreAsync(key,secret,cancellationToken); certificate=Load(secret);
            var created=new Metadata(2,deviceName,Convert.ToHexString(SHA256.HashData(certificate.RawData)),Convert.ToBase64String(certificate.RawData),key);
            await PrivateFiles.WriteAsync(metadataPath,JsonSerializer.SerializeToUtf8Bytes(created,TrustJson.Options),cancellationToken,false);
            var identity=new DeviceIdentity(certificate,deviceName);certificate=null;return identity;
        }
        finally { if(secret is not null)CryptographicOperations.ZeroMemory(secret);certificate?.Dispose(); }
    }
    private static X509Certificate2 Load(byte[] value)
    {
        var cert=X509CertificateLoader.LoadPkcs12(value,null,OperatingSystem.IsMacOS()||OperatingSystem.IsWindows()?X509KeyStorageFlags.UserKeySet:X509KeyStorageFlags.EphemeralKeySet);
        if(!cert.HasPrivateKey || cert.NotAfter.ToUniversalTime()<=DateTime.UtcNow || cert.NotBefore.ToUniversalTime()>DateTime.UtcNow) {cert.Dispose();throw new InvalidDataException("The device certificate is unavailable or expired.");} return cert;
    }
    public override string ToString()=> $"DeviceIdentity {{ DeviceName = {DeviceName}, Fingerprint = {Fingerprint}, PrivateKey = [system vault] }}";
    public void Dispose()=>Certificate.Dispose();
}
