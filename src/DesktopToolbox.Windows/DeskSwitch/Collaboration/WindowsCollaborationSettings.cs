namespace DesktopToolbox.Windows.DeskSwitch.Collaboration;

public sealed record WindowsCollaborationSettings
{
    public bool PreviewEnabled { get; init; }
    public int PreviewRefreshSeconds { get; init; } = 30;
    public bool ClipboardEnabled { get; init; }
    public string ReceiveDirectory { get; init; } = "";
    public string SharedDirectory { get; init; } = "";
    public string OriginId { get; init; } = Guid.NewGuid().ToString("N");

    public WindowsCollaborationSettings Validate()
    {
        DesktopToolbox.DeskLink.Collaboration.PreviewCaptureCadence.ValidateRefreshSeconds(PreviewRefreshSeconds);
        foreach (var path in new[] { ReceiveDirectory, SharedDirectory })
            if (path is null || path.Length > 4096 || path.Any(char.IsControl) || (path.Length != 0 && !Path.IsPathFullyQualified(path)))
                throw new ArgumentException("请选择有效的完整收件或共享目录。");
        if (OriginId is null || OriginId.Length != 32 || !OriginId.All(Uri.IsHexDigit))
            throw new ArgumentException("本机协同标识无效。");
        return this;
    }
}
