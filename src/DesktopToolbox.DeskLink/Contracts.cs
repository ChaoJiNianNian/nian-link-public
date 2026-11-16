using System.Text.Json.Serialization;
namespace DesktopToolbox.DeskLink;
public enum DeskLinkRole { Server, Client }
public enum DeskLinkProcessState { Stopped, Starting, Running, Stopping, Faulted }
public enum DeskLinkConnectionState { Disconnected, Connecting, Listening, Connected }
public sealed record DeskflowSessionOptions
{
    public required string EnginePath { get; init; }
    public required string ProfileDirectory { get; init; }
    public required string LocalName { get; init; }
    public required string RemoteName { get; init; }
    // Additional names do not grant trust or enable multi-device routing.
    public IReadOnlyList<string> AdditionalRemoteNames { get; init; } = [];
    public DeskLinkRole Role { get; init; }
    public string? ServerAddress { get; init; }
    public int Port { get; init; } = 24800;
    public bool UseNativeControl { get; init; } = true;
    public bool ClipboardEnabled { get; init; }
    public string Protocol { get; init; } = "barrier";
    public string? CertificatePath { get; init; }
    public string? TrustDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? AdvancedSettings { get; init; }
}
public sealed record DeskLinkState
{
    // Local session notification ordering only; never part of the control protocol.
    [JsonIgnore]
    public long SessionStateVersion { get; init; }
    public DeskLinkProcessState ProcessState { get; init; }
    public DeskLinkConnectionState ConnectionState { get; init; }
    public string? ActiveComputer { get; init; }
    public IReadOnlyList<string> ConnectedComputers { get; init; } = [];
    public string? PendingPeerFingerprint { get; init; }
    public string? LocalFingerprint { get; init; }
    public string? LastError { get; init; }
    public bool NativeControlAvailable { get; init; }
    public bool PreviewInputIsolated { get; init; }
    public bool PreviewHandoffSupported { get; init; }
    public DeskflowPreviewState? InputPreview { get; init; }
    public bool DisplaySuppressed { get; init; }
    public long DisplaySuppressedThroughRevision { get; init; } = -1;
    public int? ProcessId { get; init; }
    public long Revision { get; init; }
}
public sealed record FocusChangedEvent(string ActiveComputer, long Revision)
{
    public bool PreviewInputIsolated { get; init; }
}
public sealed record DeskflowProfileInfo(string SettingsPath, string CertificatePath, string LocalFingerprint, string TrustDirectory);
public sealed record NativeControlReply
{
    public int Version { get; init; } = 1;
    public string? RequestId { get; init; }
    public long Revision { get; init; }
    public bool Ok { get; init; }
    public bool PreviewInputIsolated { get; init; }
    public string? ActiveComputer { get; init; }
    public string[] ConnectedComputers { get; init; } = [];
    public string? Error { get; init; }
}
public sealed record DeskLinkSwitchResult
{
    public bool Ok { get; init; }
    public string? Target { get; init; }
    public string? ActiveComputer { get; init; }
    public bool InputTransferred { get; init; }
    public string VideoStatus { get; init; } = "notAttempted";
    public string? Error { get; init; }
}
/// <summary>Explicit optional monitor identity and input mapping approved with the pairing file.</summary>
public sealed record DeskLinkDisplayBinding
{
    public bool Enabled { get; init; }
    public uint VendorId { get; init; }
    public uint ProductId { get; init; }
    public uint WindowsInput { get; init; }
    public uint MacInput { get; init; }
    public void Validate()
    {
        if (!Enabled) return;
        if (VendorId is 0 or > ushort.MaxValue || ProductId is 0 or > ushort.MaxValue)
            throw new ArgumentException("An enabled display binding requires nonzero 16-bit vendor and product identifiers.");
        if (WindowsInput is 0 or > byte.MaxValue || MacInput is 0 or > byte.MaxValue || WindowsInput == MacInput)
            throw new ArgumentException("An enabled display binding requires two different inputs from 1 to 255.");
    }
}
