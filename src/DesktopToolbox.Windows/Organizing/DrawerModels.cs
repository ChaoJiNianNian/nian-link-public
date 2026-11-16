namespace DesktopToolbox.Windows.Organizing;

public sealed record DrawerProject(string Id, string Name, string Folder, DateTimeOffset CreatedAt);
public sealed record DrawerBehavior(bool Pinned = false, bool HideDuringFullscreen = true, IReadOnlyList<string>? QuietApplications = null, string DockSide = "right", double DockOffset = .5, string? DockDisplay = null)
{
    public static DrawerBehavior Default() => new(false, true, []);
}
internal sealed record DrawerProjectRegistration(string Id, string Name, string Folder, DateTimeOffset CreatedAt, string RootDirectory)
{
    public DrawerProject Public => new(Id, Name, Folder, CreatedAt);
}
internal sealed record DrawerSettings(string? ChosenRoot, string? CurrentProjectId, List<DrawerProjectRegistration> Projects, DrawerBehavior Behavior)
{
    public static DrawerSettings Default() => new(null, null, [], DrawerBehavior.Default());
}
public sealed record DrawerState(bool Configured, string SourceDirectory, string RootDirectory, bool Enabled, bool Monitoring, string? LastError, string? CurrentProjectId, IReadOnlyList<DrawerProject> Projects, DrawerBehavior Behavior);
public sealed record DrawerItem(string Id, string? OperationId, string Name, string Path, string SourcePath, string? ProjectId, string ProjectName, string Category, string Extension, long Size, DateTimeOffset ReceivedAt, string Status, string? Reason, bool CanUndo, bool Modified = false, string Verification = "metadata", bool CanConfirmCad = false);
public sealed record DrawerCounts(int Stored, int Pending);
public sealed record DrawerList(IReadOnlyList<DrawerItem> Items, DrawerCounts Counts, int Total = 0, bool Truncated = false, int Limit = 100);
internal sealed record DrawerRoute(string Id, string Name, string Folder);



