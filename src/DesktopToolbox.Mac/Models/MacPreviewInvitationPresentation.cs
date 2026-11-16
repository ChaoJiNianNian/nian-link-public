using Nian.Input.Security;

namespace DesktopToolbox.Mac.Models;

/// <summary>Parsing a pasted code is local; it never starts a connection or grants permission.</summary>
public sealed record MacPreviewInvitationPresentation(DeviceInvitation? Invitation,string Message)
{
    public static MacPreviewInvitationPresentation Read(string? code,DateTimeOffset now)
    {
        if(string.IsNullOrWhiteSpace(code))return new(null,"粘贴对方的连接码，然后核对完整安全指纹");
        try
        {
            var invitation=DeviceInvitation.Parse(code.Trim());
            if(invitation.Permissions!=DevicePermission.PreviewControl)return new(null,"此连接码不属于预览操作，请在对方的预览连接页重新生成");
            if(invitation.ExpiresAt<=now)return new(null,"连接码已过期，请让对方重新生成");
            return new(invitation,"请与另一台电脑显示的完整安全指纹逐字核对");
        }
        catch(Exception error)when(error is InvalidDataException or ArgumentException)
        {return new(null,"连接码无效，请重新复制完整内容");}
    }
}
