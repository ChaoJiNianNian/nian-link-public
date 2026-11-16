using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Mac.Models;

public sealed record MacConnectionSettings
{
    public string WindowsHost { get; init; } = "";
    public string WindowsName { get; init; } = "Win-PC";
    public string MacName { get; init; } = "My-Mac";
    public string EnginePath { get; init; } = "/Applications/Deskflow.app/Contents/MacOS/deskflow-core";

    public void ValidateApprovedPairing(string approvedHost, string approvedWindowsName, string approvedMacName)
    {
        if (!string.Equals(WindowsHost, approvedHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(WindowsName, approvedWindowsName, StringComparison.Ordinal) ||
            !string.Equals(MacName, approvedMacName, StringComparison.Ordinal))
            throw new InvalidOperationException("键鼠连接资料与已确认的联合切换配对不一致。请从对应的 Windows 重新导入并确认配对文件。");
    }
}

public enum MacLinkBackend { Deskflow, Native }

public sealed record MacLinkSnapshot
{
    // Monotonic within one local service instance; never part of a wire protocol.
    [System.Text.Json.Serialization.JsonIgnore]
    public long LocalSnapshotVersion { get; init; }
    /// <summary>The latest successfully authenticated Windows focus observation; UI refreshes retain its original reference.</summary>
    public DeskLinkState? PreviewControlState { get; init; }
    /// <summary>Actual owned client state, keeping stopped distinct from connecting or failed.</summary>
    public DeskLinkState PreviewLocalCoreState { get; init; } = new();
    public bool PreviewHandoffSupported {get;init;}
    public DeskflowPreviewState InputPreview {get;init;}=new();
    public MacLinkBackend Backend { get; init; } = MacLinkBackend.Deskflow;
    public MacConnectionSettings Settings { get; init; } = new();
    public bool IsRunning { get; init; }
    public bool HasPendingDisplayCleanup { get; init; }
    public bool IsConnected { get; init; }
    public bool ControlConnected { get; init; }
    public bool HasApprovedPairing { get; init; }
    public string ActiveComputer { get; init; } = "";
    public string StatusMessage { get; init; } = "尚未开始共享";
    public string PendingPeerFingerprint { get; init; } = "";
    public string LocalFingerprint { get; init; } = "";
    public string Error { get; init; } = "";
    public string DisplayRecoveryMessage { get; init; } = "";
    public bool HasDisplayRecoveryError { get; init; }
}

/// <summary>Only the authenticated, active Windows-to-Mac preview may offer a local return button.</summary>
public static class MacRemotePreviewReturn
{
    public static string? CurrentId(MacLinkSnapshot snapshot)
    {
        var preview=snapshot.InputPreview;
        return snapshot.Backend==MacLinkBackend.Deskflow && snapshot.HasApprovedPairing && snapshot.PreviewHandoffSupported &&
            snapshot.IsRunning && snapshot.IsConnected && snapshot.ControlConnected && snapshot.PreviewControlState is not null &&
            preview.Phase is DeskflowPreviewPhase.Active or DeskflowPreviewPhase.Recovery && Guid.TryParseExact(preview.Id,"N",out _) &&
            preview.ViewerComputer==snapshot.Settings.WindowsName && preview.TargetComputer==snapshot.Settings.MacName &&
            (preview.Phase==DeskflowPreviewPhase.Recovery || snapshot.ActiveComputer==snapshot.Settings.MacName) ? preview.Id : null;
    }
}

public sealed record PairingPreview(string Id, string Host, int Port, string WindowsName, string MacName, string CertificateSha256, string DisplayDescription = "");
public sealed record MacSwitchOutcome(bool Ok, string ActiveComputer, bool InputTransferred, string VideoStatus, string Error);
