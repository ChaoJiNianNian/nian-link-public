using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nian.Input.Security;
namespace DesktopToolbox.DeskLink;

/// <summary>Secret pairing material. Export/import only to private files after explicit user approval.</summary>
public sealed record DeskLinkPairingBundle
{
    private const int MaximumJsonBytes = 8192;
    public int Version { get; init; } = 1;
    public required string Host { get; init; }
    public int Port { get; init; } = 24801;
    public required string CertificateSha256 { get; init; }
    public required string Token { get; init; }
    public required string WindowsName { get; init; }
    public required string MacName { get; init; }
    public DeskLinkDisplayBinding? Display { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? InputInvitation { get; init; }
    public int DataPort { get; init; } = 24802;
    public void Validate()
    {
        if (Version is not (1 or 2)) throw new ArgumentException("Unsupported pairing version.");
        if (DataPort is <1 or >65535) throw new ArgumentException("Invalid paired data port.");
        if (Version==1 && InputInvitation is not null) throw new ArgumentException("Legacy data pairing cannot grant native input control.");
        if (Version==2)
        {
            DeviceInvitation invitation;
            try { invitation=DeviceInvitation.Parse(InputInvitation!); }
            catch(Exception error) when(error is InvalidDataException or ArgumentException) {throw new ArgumentException("Invalid native input invitation.",error);}
            if(invitation.Host!=Host || invitation.Permissions!=(DevicePermission.Control|DevicePermission.PreviewControl))
                throw new ArgumentException("Native pairing requires the same host and explicit keyboard plus preview control permissions.");
        }
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || Uri.CheckHostName(Host) == UriHostNameType.Unknown) throw new ArgumentException("Invalid paired Windows host.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Invalid paired control port.");
        DeskLinkFingerprint.Normalize(CertificateSha256);
        byte[] token;
        try { token = Convert.FromBase64String(Token); } catch (FormatException) { throw new ArgumentException("Invalid pairing token."); }
        if (token.Length != 32) throw new ArgumentException("Pairing token must contain 256 bits.");
        DeskflowProfile.ValidateName(WindowsName); DeskflowProfile.ValidateName(MacName);
        if (WindowsName == MacName) throw new ArgumentException("Paired computer names must differ.");
        Display?.Validate();
    }
    public string ToJson()
    {
        Validate();
        var json = JsonSerializer.Serialize(this, JsonLineProtocol.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new ArgumentException("Oversized pairing bundle.");
        return json;
    }
    public static DeskLinkPairingBundle Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new ArgumentException("Invalid or oversized pairing bundle.");
        var value = JsonSerializer.Deserialize<DeskLinkPairingBundle>(json, JsonLineProtocol.Json) ?? throw new ArgumentException("Empty pairing bundle.");
        value.Validate(); return value;
    }
    public override string ToString() => $"DeskLinkPairingBundle {{ Version = {Version}, Host = {Host}, Port = {Port}, CertificateSha256 = {CertificateSha256}, Token = [redacted], WindowsName = {WindowsName}, MacName = {MacName} }}";
}

public sealed record PairedControlServerOptions
{
    public required string ProfileDirectory { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 24801;
    public required IReadOnlyCollection<string> AllowedTargets { get; init; }
}
internal sealed record PairedControlRequest(int Version, string RequestId, string Token, string Action, string? Target, string? PreviewId = null, bool PreviewHandoffClient = false);
internal sealed record PairedControlResponse(int Version, string RequestId, bool Ok, DeskLinkState? State = null, DeskLinkSwitchResult? Result = null, string? Error = null, DeskflowPreviewState? Preview = null);
