using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.Mac.Models;

public sealed record MacCollaborationSettings
{
    public bool PreviewEnabled { get; init; }
    // Old files only contain PreviewEnabled, which previously authorized both directions.
    // A new explicit choice is always serialized and no longer follows the viewing toggle.
    private bool? shareScreenEnabled;
    public bool ShareScreenEnabled { get => shareScreenEnabled ?? PreviewEnabled; init => shareScreenEnabled = value; }
    public int PreviewRefreshSeconds { get; init; } = PreviewCaptureCadence.DefaultRefreshSeconds;
    public bool ClipboardEnabled { get; init; }
    public bool Pinned { get; init; }
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public string DockEdge { get; init; } = "";
    public string ReceiveDirectory { get; init; } = "";
    public string[] SharedDirectories { get; init; } = [];
    public MacPendingSend? PendingSend { get; init; }
}

public sealed record MacPendingSend(string Id, string[] Paths);

public static class MacPreviewPresentation
{
    public static bool ShouldSubscribe(bool manuallyEnabled, bool miniVisible, bool detachedVisible, bool peerOnline) => manuallyEnabled && peerOnline && (miniVisible || detachedVisible);
    public static (double Width, double Height) MiniSize(bool previewEnabled, bool detachedVisible) => (398, previewEnabled ? 548 : 309);
}




public sealed record MacCollaborationSnapshot
{
    public MacCollaborationSettings Settings { get; init; } = new();
    public bool IsOnline { get; init; }
    public bool RemoteClipboardEnabled { get; init; }
    public string? ClipboardError { get; init; }
    public bool ReceiveSetupRequired { get; init; }
    public string? ReceiveError { get; init; }
    public PreviewFrame Preview { get; init; } = PreviewFrame.Off();
    public bool PassivePreviewGenerationSupported { get; init; }
    public Guid? PassivePreviewRequestId { get; init; }
    public PreviewSourceStatus? RemotePreviewSourceStatus { get; init; }
    public TransferProgress? Transfer { get; init; }
    public IReadOnlyList<TransferReceipt> Receipts { get; init; } = [];
    public string Status { get; init; } = "等待连接Windows协同服务";
    public string? Error { get; init; }
}

public static class MacMiniPlacement
{
    public static (int X, int Y, string Edge) Resolve(int x, int y, int width, int height, int left, int top, int workWidth, int workHeight, int snapDistance)
    {
        var right = left + Math.Max(0, workWidth - width); var bottom = top + Math.Max(0, workHeight - height);
        var edge = Math.Abs((long)x - left) <= snapDistance ? "left" : Math.Abs((long)x - right) <= snapDistance ? "right" : "";
        return (edge == "left" ? left : edge == "right" ? right : Math.Clamp(x, left, right), Math.Clamp(y, top, bottom), edge);
    }
}

