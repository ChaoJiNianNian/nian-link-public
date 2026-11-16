using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

public sealed partial class MacNativeLinkService
{
    public async Task<MacLinkSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return Snapshot();
        try
        {
            ThrowIfDisposed(); await LoadAsync(cancellationToken); controlReady = false;
            if (Volatile.Read(ref running) != 0 && !desktop.IsGuardHealthy) { StopLocal("local-guard-failed"); error = "Mac 本机输入保护已失效，共享已停止。"; }
            if (Volatile.Read(ref running) != 0 && approved is not null)
            {
                try
                {
                    windowsState = await WithWindowsClientAsync((client, token) => client.GetStateAsync(token), cancellationToken);
                    ValidateWindowsNames(windowsState); controlReady = windowsState.Available; error = windowsState.Error ?? "";
                    if (controlReady)
                    {
                        var observed = await displaySwitch.ObserveAsync(ToDisplayState(windowsState), cancellationToken);
                        if (observed is not null) SetDisplayMessage(observed);
                    }
                    else displaySwitch.ResetBaseline();
                }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested && IsControlError(failure))
                { windowsState = null; error = "无法连接已批准 Windows 的原生控制通道。"; displaySwitch.ResetBaseline(); }
            }
            return Publish();
        }
        finally { gate.Release(); }
    }

    public async Task<MacSwitchOutcome> SwitchAsync(string target, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref running) == 0 || pairing is null || approved is null) throw new InvalidOperationException("请先开始原生共享。");
            if (target != pairing.WindowsName && target != pairing.MacName) throw new ArgumentException("只能切到已批准的两台电脑。");
            if (displaySwitch.IsWriteInFlight || displays?.HasUnfinishedChildren == true) throw new InvalidOperationException("上一条 Mac 显示命令尚未结束。");
            return await RequestSwitchInsideAsync(target, cancellationToken);
        }
        finally { gate.Release(); }
    }
    private async Task<MacSwitchOutcome> RequestSwitchInsideAsync(string target, CancellationToken cancellationToken, bool allowStoppedReturn = false)
    {
        if (allowStoppedReturn && target != settings.WindowsName) throw new InvalidOperationException("停止期间只能请求回 Windows。");
        var result = await WithWindowsClientAsync(async (client, token) =>
        {
            var before = await client.GetStateAsync(token); ValidateWindowsNames(before);
            if (!before.Available && target != settings.WindowsName) throw new InvalidOperationException("Windows 原生切换尚未就绪。");
            return await client.FullSwitchAsync(new(Interlocked.Increment(ref switchSequence), target == settings.WindowsName ? DesktopSwitchTarget.Local : DesktopSwitchTarget.Remote, before.DisplayIntentRevision), token);
        }, cancellationToken, allowStoppedReturn);
        ValidateWindowsNames(result.State); windowsState = result.State; controlReady = result.State.Available;
        if (target == settings.WindowsName && result.InputTransferred)
        {
            // A physical handoff confirmation and an explicit display intent
            // must agree. Temporary preview ownership can never satisfy this.
            if (result.State.InputActive || result.State.ActiveComputer != target || result.State.DisplayIntentComputer != target)
                return new(false, result.State.ActiveComputer ?? "", false, "notAttempted", "Windows 尚未确认实体控制与显式显示回切。");
            var local = await displaySwitch.ReturnAfterConfirmedFocusAsync(ToDisplayState(result.State), cancellationToken); SetDisplayMessage(local);
            Publish();
            var video = local.VideoStatus == "disabled" ? result.VideoStatus : local.VideoStatus;
            return new(result.Ok && video is "disabled" or "verified", target, true, video, local.Error ?? result.Error ?? "");
        }
        Publish(); return new(result.Ok, result.State.ActiveComputer ?? "", result.InputTransferred, result.VideoStatus, result.Error ?? "");
    }
    private async Task<T> WithWindowsClientAsync<T>(Func<DesktopInputClient, CancellationToken, Task<T>> request, CancellationToken token, bool allowStoppedReturn = false)
    {
        var endpoint = ApprovedEndpoint(DevicePermission.Control, allowStoppedReturn) ?? throw new InvalidOperationException("此 Windows 的本机持续允许已撤销。");
        await using var client = new DesktopInputClient(identity!, endpoint); await client.ConnectAsync(token);
        var result = await request(client, token);
        if (endpoint != ApprovedEndpoint(DevicePermission.Control, allowStoppedReturn)) throw new InvalidOperationException("配对设备或本机允许已变化。");
        return result;
    }
    private TrustedEndpoint? ApprovedEndpoint(DevicePermission permission, bool allowStoppedReturn = false)
    {
        var saved = Volatile.Read(ref approved);
        return (allowStoppedReturn || Volatile.Read(ref running) != 0 && desktop.IsGuardHealthy) && Volatile.Read(ref disposed) == 0 && saved is not null &&
            (saved.LocalPermissions & permission) == permission && trust?.Allows(saved.Endpoint.Fingerprint, permission) == true ? saved.Endpoint : null;
    }
    private DesktopControlState ReadLocalState()
    {
        var activity = input.ReadActivity();
        return new(Volatile.Read(ref running) != 0 && desktop.IsGuardHealthy, settings.WindowsName, settings.MacName,
            activity.Active ? settings.MacName : settings.WindowsName, activity.Active, activity.Revision, 0);
    }
    private void ValidateWindowsNames(DesktopControlState value)
    {
        if (value.LocalName != settings.WindowsName || value.RemoteName != settings.MacName) throw new InvalidDataException("Windows 原生状态与本机配对名称不符。");
    }
    private static DeskLinkState ToDisplayState(DesktopControlState state) => new()
    {
        ProcessState = state.Available ? DeskLinkProcessState.Running : DeskLinkProcessState.Stopped,
        NativeControlAvailable = state.Available, ProcessId = 1,
        // This coordinator consumes display intent exclusively, never input focus/revision.
        ActiveComputer = state.DisplayIntentComputer, Revision = state.DisplayIntentRevision
    };
    private void ConfigureDisplay()
    {
        displaySwitch.Configure(pairing?.Display, settings.WindowsName, settings.MacName); displayMessage = DescribeDisplay(pairing?.Display); displayError = false;
    }
    private static string DescribeDisplay(DeskLinkDisplayBinding? binding) => binding is { Enabled: true }
        ? $"Mac 本地回切已启用（{binding.VendorId:X4}:{binding.ProductId:X4}，Windows 输入 {binding.WindowsInput}）；实际画面仍需确认。"
        : "此配对未启用 Mac 本地显示回切。";
    private void SetDisplayMessage(MacLocalDisplaySwitchResult result)
    {
        displayMessage = result.VideoStatus == "acceptedUnverified" ? "Mac 已接受回 Windows 的切屏命令，实际画面仍待确认。" : result.Error ?? DescribeDisplay(pairing?.Display);
        displayError = result.NeedsAttention;
    }
    private static bool IsControlError(Exception error) => error is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or
        OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or TimeoutException;
}
