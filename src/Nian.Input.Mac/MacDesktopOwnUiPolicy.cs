namespace Nian.Input.Mac;

/// <summary>Thread-safe local snapshots only. A remote peer must never provide or modify these decisions.</summary>
public sealed class MacDesktopOwnUiPolicy
{
    private readonly Func<bool> protectedOwnSurfaceActive;
    private readonly Func<int, bool> protectedWindowId;
    private readonly Func<string?, int, bool> excludedOwner;
    internal static MacDesktopOwnUiPolicy Experimental { get; } = new(() => true, _ => true);

    public MacDesktopOwnUiPolicy(Func<bool> protectedOwnSurfaceActive, Func<int, bool> protectedWindowId,
        Func<string?, int, bool>? excludedOwner = null)
    {
        ArgumentNullException.ThrowIfNull(protectedOwnSurfaceActive); ArgumentNullException.ThrowIfNull(protectedWindowId);
        this.protectedOwnSurfaceActive = protectedOwnSurfaceActive; this.protectedWindowId = protectedWindowId;
        this.excludedOwner = excludedOwner ?? ((_, _) => false);
    }

    internal bool ProtectsOwnForeground
    {
        get { try { return protectedOwnSurfaceActive(); } catch { return true; } }
    }
    internal bool ProtectsOwnWindow(int? windowId)
    {
        if (windowId is null or <= 0 || ProtectsOwnForeground) return true;
        try { return protectedWindowId(windowId.Value); } catch { return true; }
    }
    internal bool ExcludesPreviewOwner(string? bundle, int ownerPid, int ownPid)
    {
        if (ownerPid <= 0 || ownerPid == ownPid) return true;
        try { return excludedOwner(bundle, ownerPid); } catch { return true; }
    }
}
