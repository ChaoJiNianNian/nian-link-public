namespace DesktopToolbox.DeskLink.Coordination;

public sealed record LocalScreenExecutor(Guid Epoch, string LocalMonitorId, MultiPeerDisplayExecutor Executor);
public sealed record RemoteScreenQueue(Guid Epoch, string LocalMonitorId, MultiPeerDisplayQueue Queue);

/// <summary>Routes a switch to the already confirmed computer currently showing that screen.</summary>
public sealed class MultiScreenDeviceRouter : IMultiScreenDisplayRouter
{
    private readonly string host;
    private readonly Func<string, LocalScreenExecutor?> local;
    private readonly Func<string, RemoteScreenQueue?> remote;
    private readonly Func<DateTimeOffset> now;

    public MultiScreenDeviceRouter(string host, Func<string, LocalScreenExecutor?> local,
        Func<string, RemoteScreenQueue?> remote, Func<DateTimeOffset>? now = null)
    {
        DeskflowProfile.ValidateName(host); this.host = host;
        this.local = local ?? throw new ArgumentNullException(nameof(local));
        this.remote = remote ?? throw new ArgumentNullException(nameof(remote));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<DisplaySwitchOutcome> ExecuteAsync(MultiScreenPendingSwitch command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var plan = command.Plan;
        if (command.Id == Guid.Empty || command.KeyboardRevision < 0 || plan.RequiresManualSwitch ||
            string.IsNullOrWhiteSpace(plan.SourceLocalMonitorId) || plan.ExpectedInput is not { } expected ||
            plan.TargetInput is not { } target || expected == target)
            return NotAttempted();
        if (string.Equals(plan.SourceDevice, host, StringComparison.Ordinal))
        {
            var endpoint = local(plan.ScreenId);
            if (endpoint is null || endpoint.Epoch == Guid.Empty || endpoint.LocalMonitorId != plan.SourceLocalMonitorId) return NotAttempted();
            return endpoint.Executor.ExecuteAsync(Request(command, endpoint.Epoch, expected, target));
        }
        var peer = remote(plan.SourceDevice);
        if (peer is null || peer.Epoch == Guid.Empty || peer.LocalMonitorId != plan.SourceLocalMonitorId) return NotAttempted();
        try { return peer.Queue.Enqueue(Request(command, peer.Epoch, expected, target)); }
        catch (InvalidOperationException) { return NotAttempted(); }
    }

    private MultiPeerDisplayRequest Request(MultiScreenPendingSwitch command, Guid epoch, uint expected, uint target) =>
        new(command.Id, epoch, command.Plan.ScreenId, expected, target, command.KeyboardRevision, now().AddSeconds(8));
    private static Task<DisplaySwitchOutcome> NotAttempted() => Task.FromResult(new DisplaySwitchOutcome("notAttempted", "当前电脑的显示器连接尚未确认。"));
}
