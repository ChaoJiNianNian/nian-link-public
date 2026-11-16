namespace Nian.Input.Mac;

internal readonly record struct MacDesktopWindowInfo(int? OwnerPid, MacDesktopDisplay Bounds, double? Alpha, int? WindowId = null, string? OwnerBundle = null);

/// <summary>CGWindowList is already front-to-back. No window means ordinary desktop, not an error.</summary>
internal static class MacDesktopWindowGate
{
    internal static MacDesktopGate Resolve(MacDesktopPoint point, int ownPid, IEnumerable<MacDesktopWindowInfo> windows, MacDesktopOwnUiPolicy? policy = null, bool preview = false)
    {
        if (!point.IsValid || ownPid <= 0) return MacDesktopGate.Unknown;
        foreach (var window in windows)
            if (ResolveWindow(point, ownPid, window, policy, preview) is { } result) return result;
        return MacDesktopGate.Allowed;
    }

    internal static MacDesktopGate? ResolveWindow(MacDesktopPoint point, int ownPid, MacDesktopWindowInfo window, MacDesktopOwnUiPolicy? policy = null, bool preview = false)
    {
        if (!point.IsValid || ownPid <= 0) return MacDesktopGate.Unknown;
        if (window.Alpha == 0) return null;
        if (window.Alpha is not { } alpha || !double.IsFinite(alpha) || alpha is < 0 or > 1) return MacDesktopGate.Unknown;
        var b = window.Bounds;
        if (!double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(b.Width) || !double.IsFinite(b.Height) ||
            !double.IsFinite(b.X + b.Width) || !double.IsFinite(b.Y + b.Height) || b.Width < 0 || b.Height < 0)
            return MacDesktopGate.Unknown;
        if (b.Width == 0 || b.Height == 0 || point.X < b.X || point.Y < b.Y || point.X >= b.X + b.Width || point.Y >= b.Y + b.Height)
            return null;
        if (window.OwnerPid is null or <= 0) return MacDesktopGate.Unknown;
        policy ??= MacDesktopOwnUiPolicy.Experimental;
        if (preview && policy.ExcludesPreviewOwner(window.OwnerBundle, window.OwnerPid.Value, ownPid)) return MacDesktopGate.Blocked;
        if (window.OwnerPid == ownPid) return policy.ProtectsOwnWindow(window.WindowId) ? MacDesktopGate.Blocked : MacDesktopGate.Allowed;
        if (preview && string.IsNullOrWhiteSpace(window.OwnerBundle)) return MacDesktopGate.Unknown;
        return MacDesktopGate.Allowed;
    }
}
