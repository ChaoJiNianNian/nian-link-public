namespace DesktopToolbox.DeskLink;

/// <summary>Adapts conditional native focus transfer without an unsafe fallback.</summary>
public sealed class MultiPeerNativeSwitch(NativeControlClient native)
{
    private readonly NativeControlClient native = native ?? throw new ArgumentNullException(nameof(native));

    public async Task<DeskLinkSwitchResult> SwitchAsync(string target, string expectedActive,
        long expectedRevision, CancellationToken ct)
    {
        var reply = await native.SwitchIfCurrentAsync(target, expectedActive, expectedRevision, ct);
        var confirmed = reply.Ok && string.Equals(reply.ActiveComputer, target, StringComparison.Ordinal);
        return new()
        {
            Ok = confirmed,
            Target = target,
            ActiveComputer = reply.ActiveComputer,
            InputTransferred = confirmed,
            VideoStatus = "notAttempted",
            Error = confirmed ? null : reply.Error ?? "Keyboard focus was not confirmed."
        };
    }
}
