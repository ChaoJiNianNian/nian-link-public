namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>Returns only after the owned local/remote display operation has truly finished.</summary>
public interface IMultiScreenDisplayRouter
{
    Task<DisplaySwitchOutcome> ExecuteAsync(MultiScreenPendingSwitch command);
}

/// <summary>Prevents shutdown while a dispatched or manually pending display change is unresolved.</summary>
public interface IDisplayOperationLifetime
{
    bool HasUnresolvedOperation { get; }
}

/// <summary>Applies explicitly enabled screen linkage after keyboard focus is confirmed.</summary>
public sealed class MultiScreenDisplayCoordinator : IUnifiedDisplayControl, IDisplayOperationLifetime
{
    private readonly MultiScreenTopology topology;
    private readonly MultiScreenDisplayState screens;
    private readonly Func<CancellationToken, Task<DeskLinkState>> keyboard;
    private readonly IMultiScreenDisplayRouter router;
    private readonly TimeSpan wait;

    public bool HasUnresolvedOperation => screens.Pending is not null;

    public MultiScreenDisplayCoordinator(MultiScreenTopology topology, MultiScreenDisplayState screens,
        Func<CancellationToken, Task<DeskLinkState>> keyboard, IMultiScreenDisplayRouter router, TimeSpan wait)
    {
        this.topology = topology ?? throw new ArgumentNullException(nameof(topology));
        this.screens = screens ?? throw new ArgumentNullException(nameof(screens));
        if (!ReferenceEquals(screens.Topology, topology)) throw new ArgumentException("屏幕状态与配置不一致。", nameof(screens));
        this.keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        this.router = router ?? throw new ArgumentNullException(nameof(router));
        if (wait <= TimeSpan.Zero || wait > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(wait));
        this.wait = wait;
    }

    public async Task<DisplaySwitchOutcome> ApplyAsync(string target, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> affected;
        try { affected = topology.AutomaticScreensForDevice(target); }
        catch (ArgumentException) { return new("failed", "目标电脑未在屏幕配置中。"); }
        if (affected.Count == 0) return new("disabled");
        var focus = await keyboard(cancellationToken);
        if (focus.ProcessState != DeskLinkProcessState.Running || !focus.NativeControlAvailable ||
            focus.Revision < 0 || focus.ActiveComputer is not { } active || !string.Equals(active, target, StringComparison.Ordinal))
            return new("failed", "键鼠目标尚未确认，未切换显示器。");
        var uncertain = false;
        var firstScreen = true;
        foreach (var screen in affected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!firstScreen)
            {
                var latest = await keyboard(cancellationToken);
                if (latest.ProcessState != DeskLinkProcessState.Running || !latest.NativeControlAvailable ||
                    latest.ActiveComputer != active || latest.Revision != focus.Revision)
                    return new("failed", "键鼠状态已变化，停止后续显示器切换。");
            }
            firstScreen = false;
            MultiScreenPendingSwitch? command;
            try { command = screens.Begin(screen, target, active, focus.Revision); }
            catch (InvalidOperationException error) { return new("failed", error.Message); }
            if (command is null) continue;
            if (cancellationToken.IsCancellationRequested)
            {
                screens.CancelBeforeDispatch(command.Id);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (command.Plan.RequiresManualSwitch)
                return new("acceptedUnverified", "请用显示器按键切换输入，再确认画面。");
            Task<DisplaySwitchOutcome> work;
            try { work = router.ExecuteAsync(command) ?? throw new InvalidOperationException("显示器操作未启动。"); }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Net.Sockets.SocketException)
            { return new("failed", "显示器操作未确认，已暂停后续切屏。"); }
            DisplaySwitchOutcome outcome;
            try { outcome = await work.WaitAsync(wait, cancellationToken); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
                _ = CompleteWhenFinishedAsync(command.Id, work);
                return new("acceptedUnverified", "显示器操作仍在等待确认，暂不重复切屏。");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Net.Sockets.SocketException)
            { return new("failed", "显示器操作未确认，已暂停后续切屏。"); }
            if (outcome.Status == "notAttempted")
            {
                screens.CancelBeforeDispatch(command.Id);
                return new("failed", outcome.Error ?? "显示器操作未开始。");
            }
            var status = Normalize(outcome.Status);
            screens.Complete(command.Id, status);
            if (status == "failed") return new("failed", outcome.Error ?? "显示器切换未确认。");
            if (status == "acceptedUnverified") uncertain = true;
        }
        return uncertain ? new("acceptedUnverified", "显示器已接受命令，请确认实际画面。") : new("verified");
    }

    public void ConfirmManualSwitch(Guid id, bool confirmed)
    {
        var command = screens.Pending;
        if (command is null || command.Id != id || !command.Plan.RequiresManualSwitch)
            throw new InvalidOperationException("当前没有待确认的手动切屏。");
        screens.Complete(id, confirmed ? "verified" : "failed");
    }

    private async Task CompleteWhenFinishedAsync(Guid id, Task<DisplaySwitchOutcome> work)
    {
        try
        {
            var outcome = await work.ConfigureAwait(false);
            if (outcome.Status == "notAttempted") screens.CancelBeforeDispatch(id);
            else screens.Complete(id, Normalize(outcome.Status));
        }
        catch (Exception) { /* Unknown completion must keep the screen operation blocked. */ }
    }

    private static string Normalize(string? status) => status is "verified" or "acceptedUnverified" or "failed" ? status : "failed";
}
