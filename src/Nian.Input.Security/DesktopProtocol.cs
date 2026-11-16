using System.Security.Cryptography.X509Certificates;
using Nian.Input.Control;

namespace Nian.Input.Security;

internal sealed record DesktopRequest(int Version,string Action,string Session,string? DeviceName=null,string? Invitation=null,InputPacket? Packet=null,PreviewInputCommand? Preview=null,DevicePermission? Permissions=null,int? ReceiverPort=null,DesktopFullSwitchRequest? FullSwitch=null)
{
    public override string ToString()=> $"DesktopRequest {{ Version = {Version}, Action = {Action}, Session = {Session}, Invitation = [redacted] }}";
}
internal sealed record PairingReply(int Version,bool Ok,string Action,string ClientFingerprint,string DeviceName,DevicePermission Permissions=DevicePermission.Control);
internal sealed record DesktopControlRpcReply(int Version,bool Ok,string Action,string Session,DesktopControlState? State=null,DesktopSwitchResult? SwitchResult=null,string? Error=null);
internal static class DesktopProtocol
{
    internal const int Version=2;
    internal static bool CertificateValid(X509Certificate? certificate)
    {
        if(certificate is null)return false;
        using var parsed=X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if(parsed.NotBefore.ToUniversalTime()>DateTime.UtcNow||parsed.NotAfter.ToUniversalTime()<=DateTime.UtcNow)return false;
        var usage=parsed.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        return usage is not null && usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o=>o.Value=="1.3.6.1.5.5.7.3.2") && usage.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o=>o.Value=="1.3.6.1.5.5.7.3.1");
    }
}
