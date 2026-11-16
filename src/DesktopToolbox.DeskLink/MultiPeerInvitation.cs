using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Private invitation prototype; it contains a bearer secret.</summary>
public sealed record MultiPeerInvitation
{
    private const int MaximumBytes = 4096;
    public int Version { get; init; } = 1;
    public required string Host { get; init; }
    public int Port { get; init; }
    public int KeyboardPort { get; init; } = 24800;
    public int DataPort { get; init; } = 24802;
    public required string HostName { get; init; }
    public required string PeerName { get; init; }
    public required string CertificateSha256 { get; init; }
    public required string Token { get; init; }

    public void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            Host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            Uri.CheckHostName(Host) == UriHostNameType.Unknown || Port is < 1 or > 65535 || KeyboardPort is < 1 or > 65535 || DataPort is < 1 or > 65535)
            throw new ArgumentException("Invalid host or protocol.");
        DeskflowProfile.ValidateName(HostName);
        DeskflowProfile.ValidateName(PeerName);
        if (string.Equals(HostName, PeerName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Device names must differ.");
        DeskLinkFingerprint.Normalize(CertificateSha256);
        byte[] token;
        try { token = Convert.FromBase64String(Token ?? ""); }
        catch (FormatException) { throw new ArgumentException("Invalid device token."); }
        try { if (token.Length != 32) throw new ArgumentException("Invalid device token."); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    public string ToJson()
    {
        Validate();
        var json = JsonSerializer.Serialize(this, JsonLineProtocol.Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new ArgumentException("Invitation too large.");
        return json;
    }

    public static MultiPeerInvitation Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new ArgumentException("Invalid invitation.");
        var invitation = JsonSerializer.Deserialize<MultiPeerInvitation>(json, JsonLineProtocol.Json)
            ?? throw new ArgumentException("Empty invitation.");
        invitation.Validate();
        return invitation;
    }

    public override string ToString() => $"MultiPeerInvitation {{ HostName = {HostName}, PeerName = {PeerName}, Token = [redacted] }}";
}
