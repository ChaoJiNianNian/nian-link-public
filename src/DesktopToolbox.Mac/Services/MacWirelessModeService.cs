using System.Text.RegularExpressions;
using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Services;

/// <summary>Reads the saved system keeper separately from the actual AWDL interface.</summary>
public sealed class MacWirelessModeService : IMacWirelessModeService
{
    private readonly IMacWirelessCommandRunner runner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly Regex InterfaceFlags = new(@"^awdl0:\s+flags=[0-9a-fA-F]+<(?<flags>[^>\r\n]*)>", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public MacWirelessModeService() : this(new MacWirelessCommandRunner()) { }
    internal MacWirelessModeService(IMacWirelessCommandRunner runner) => this.runner = runner;

    public async Task<MacWirelessSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CancelledSnapshot(); }
        try { return await ReadInsideAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CancelledSnapshot(); }
        try
        {
            var before = await ReadInsideAsync(cancellationToken).ConfigureAwait(false);
            if (before.KeepEnabled == enabled && before.Error.Length == 0 && (enabled || before.State == MacAwdlState.Enabled)) return before;
            if (enabled && (!before.CanChange || before.KeepEnabled is null))
                return before with { Error = before.Error.Length > 0 ? before.Error : "暂未确认自动保持设置，请刷新后重试。" };
            // Turning off must remain possible when an interface or policy read fails.
            // The fixed native transaction validates and removes only our own system job.
            MacWirelessCommandResult result;
            try
            {
                result = await runner.RunAsync(enabled ? MacWirelessCommand.EnableKeep : MacWirelessCommand.DisableKeep, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { result = new(-1, Cancelled: true); }
            catch (Exception) { result = new(-1, Error: "无法启动系统授权。"); }

            // A privileged transaction may finish after its caller is cancelled.
            // Read both the saved choice and interface, without guessing the outcome.
            var after = await ReadInsideAsync(CancellationToken.None).ConfigureAwait(false);
            var error = OperationError(result);
            if (error.Length != 0) return after with { Error = error };
            if (after.KeepEnabled is null) return after with { Error = "切换后未能确认自动保持设置，请刷新后重试。" };
            if (after.KeepEnabled != enabled) return after with { Error = "系统未保存目标设置，请刷新后重试。" };
            if (!enabled && after.State != MacAwdlState.Enabled && after.Error.Length == 0)
                return after with { Error = "自动保持已关闭，暂未确认苹果互联通道恢复，请刷新确认。" };
            return after;
        }
        finally { gate.Release(); }
    }

    private async Task<MacWirelessSnapshot> ReadInsideAsync(CancellationToken token)
    {
        MacWirelessCommandResult choice;
        try { choice = await runner.RunAsync(MacWirelessCommand.ReadKeepState, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CancelledSnapshot(); }
        catch (Exception) { choice = new(-1, Error: "读取自动保持设置失败，请刷新重试。"); }
        if (choice.Unsupported) return new(MacAwdlState.Unavailable, "此功能仅支持 macOS");
        var actual = await ReadInterfaceAsync(token).ConfigureAwait(false);
        var choiceError = choice.Cancelled ? "操作已取消，请刷新确认当前设置。"
            : choice.TimedOut ? "读取自动保持设置超时，请刷新重试。"
            : choice.Error.Length > 0 ? choice.Error
            : choice.ExitCode != 0 || choice.KeepEnabled is null ? "暂未确认自动保持设置，请刷新重试。" : "";
        return actual with
        {
            KeepEnabled = choice.KeepEnabled,
            Error = string.Join(" ", new[] { choiceError, actual.Error }.Where(value => value.Length > 0).Distinct())
        };
    }

    private async Task<MacWirelessSnapshot> ReadInterfaceAsync(CancellationToken token)
    {
        MacWirelessCommandResult result;
        try { result = await runner.RunAsync(MacWirelessCommand.ReadState, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CancelledSnapshot(); }
        catch (Exception) { return new(MacAwdlState.Unknown, "无线状态未确认", "读取无线状态失败，请刷新重试。"); }
        if (result.Unsupported) return new(MacAwdlState.Unavailable, "此功能仅支持 macOS");
        if (result.Cancelled) return CancelledSnapshot();
        if (result.TimedOut) return new(MacAwdlState.Unknown, "无线状态未确认", "读取无线状态超时，请刷新重试。");
        if (result.ExitCode != 0)
        {
            if (result.Error.Contains("interface awdl0 does not exist", StringComparison.OrdinalIgnoreCase))
                return new(MacAwdlState.Unavailable, "当前 Mac 没有可用的苹果设备互联通道");
            return new(MacAwdlState.Unknown, "无线状态未确认", "读取无线状态失败，请刷新重试。");
        }
        var match = InterfaceFlags.Match(result.Output);
        if (!match.Success) return new(MacAwdlState.Unknown, "无线状态未确认", "无法识别无线状态，请刷新重试。");
        var up = match.Groups["flags"].Value.Split(',').Contains("UP", StringComparer.Ordinal);
        return up ? new(MacAwdlState.Enabled, "苹果设备互联已开启")
            : new(MacAwdlState.Disabled, "苹果设备互联已暂停");
    }

    private static MacWirelessSnapshot CancelledSnapshot() => new(MacAwdlState.Unknown, "无线状态未确认", "操作已取消，请刷新确认当前状态。");
    private static string OperationError(MacWirelessCommandResult result)
    {
        if (result.AuthorizationPending) return "系统授权尚未结束，请关闭 Mac 上的授权窗口后刷新确认。";
        if (result.Cancelled || result.Error.Contains("(-128)", StringComparison.Ordinal)) return "已取消切换，请根据当前状态确认。";
        if (result.TimedOut) return "系统授权超时，请根据当前状态确认后重试。";
        if (result.Unsupported) return "此功能仅支持 macOS。";
        if (result.ExitCode == 0) return "";
        if (result.Error.Contains("(-60007)", StringComparison.Ordinal)) return "无法显示系统授权，请在 Mac 本机应用内操作。";
        return "切换失败，请在 Mac 本机完成系统授权后重试。";
    }
}
