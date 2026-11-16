using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nian.Input.Security;

[Flags] public enum DevicePermission { None=0, Control=1, PreviewControl=2 }
public sealed record TrustedDevice(string Fingerprint,string DeviceName,DevicePermission Permissions,DateTimeOffset AddedAt);
public sealed record TrustedEndpoint(string Host,int Port,string Fingerprint,string DeviceName);
public sealed record PairingApproval(string DeviceName,string Fingerprint,DevicePermission RequestedPermission,DateTimeOffset ExpiresAt,string? SourceAddress=null,int? ReceiverPort=null);
public sealed record PairedDevice(string DeviceName,string Fingerprint,DevicePermission GrantedPermissions,string SourceAddress,int? ReceiverPort)
{
    public TrustedEndpoint? ReceiverEndpoint=>ReceiverPort is{} port?new(SourceAddress,port,Fingerprint,DeviceName):null;
}

public sealed record DeviceInvitation(string Host,int Port,string Fingerprint,string Secret,DateTimeOffset ExpiresAt,[property:JsonRequired]int Version=2,[property:JsonRequired]DevicePermission Permissions=DevicePermission.Control)
{
    public string ToCode() { Validate(); return "nian-desktop-v2:"+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this,TrustJson.Options)); }
    public static DeviceInvitation Parse(string code)
    {
        if(code is null || code.Length>4096 || !code.StartsWith("nian-desktop-v2:",StringComparison.Ordinal)) throw new InvalidDataException("The device invitation is invalid.");
        try { var result=JsonSerializer.Deserialize<DeviceInvitation>(Convert.FromBase64String(code[16..]),TrustJson.Options)??throw new InvalidDataException("The invitation is empty."); result.Validate(); return result; }
        catch(Exception e) when(e is FormatException or JsonException) { throw new InvalidDataException("The device invitation is invalid."); }
    }
    public override string ToString()=> $"DeviceInvitation {{ Host = {Host}, Port = {Port}, Fingerprint = {Fingerprint}, Secret = [redacted], ExpiresAt = {ExpiresAt:O} }}";
    internal void Validate()
    {
        if(Version!=2 || !TrustValidation.Endpoint(Host,Port,Fingerprint) || !TrustValidation.Permissions(Permissions,false) || !InputValidation.TryToken(Secret,out var secret)) throw new InvalidDataException("The device invitation is invalid.");
        CryptographicOperations.ZeroMemory(secret);
    }
}

public interface IIdentityVault
{
    Task<byte[]?> LoadAsync(string key,CancellationToken cancellationToken=default);
    Task StoreAsync(string key,ReadOnlyMemory<byte> value,CancellationToken cancellationToken=default);
}

internal static class TrustJson
{
    internal static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web) { MaxDepth=12,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,NumberHandling=JsonNumberHandling.Strict };
}
internal static class TrustValidation
{
    internal static bool Fingerprint(string? s)=>s is {Length:64} && s.All(Uri.IsHexDigit);
    internal static string Normalize(string s)=>Fingerprint(s)?s.ToUpperInvariant():throw new ArgumentException("The device fingerprint is invalid.");
    internal static bool DeviceName(string? s)=>!string.IsNullOrWhiteSpace(s) && s.Length<=80 && !s.Any(char.IsControl);
    internal static bool Permissions(DevicePermission value,bool allowNone=true)=>(allowNone||value!=DevicePermission.None)&&(value&~(DevicePermission.Control|DevicePermission.PreviewControl))==0;
    internal static bool Endpoint(string? host,int port,string? pin)=>!string.IsNullOrWhiteSpace(host) && host.Length<=253 && !host.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c)) && Uri.CheckHostName(host)!=UriHostNameType.Unknown && port is >0 and <=65535 && Fingerprint(pin);
    internal static bool Matches(string fingerprint,byte[] raw)=>CryptographicOperations.FixedTimeEquals(Convert.FromHexString(fingerprint),SHA256.HashData(raw));
    internal static string VaultKey(string path)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
}
