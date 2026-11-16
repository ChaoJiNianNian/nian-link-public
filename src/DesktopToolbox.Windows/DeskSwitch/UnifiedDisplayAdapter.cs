using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Verifies DDC readback where the active video path permits it; never infers it from a write.</summary>
public sealed class UnifiedDisplayAdapter : IUnifiedDisplayControl
{
    private readonly IDisplayBridge bridge;
    private readonly string configurationFile;
    private readonly DeskSwitchSettings settings;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public UnifiedDisplayAdapter(IDisplayBridge bridge, string configurationFile, DeskSwitchSettings settings,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        this.configurationFile = configurationFile;
        this.settings = (settings with { LinkDisplay = true }).Validate();
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public async Task<DisplaySwitchOutcome> ApplyAsync(string computer, CancellationToken cancellationToken)
    {
        var local = string.Equals(computer, settings.LocalName, StringComparison.OrdinalIgnoreCase);
        if (!local && !string.Equals(computer, settings.RemoteName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("显示器目标不在当前两机配置中。", nameof(computer));
        var target = local ? "local" : "remote";
        var expected = local ? settings.LocalInput : settings.RemoteInput;
        // The old hook starts before Deskflow has finished leaving the local screen.
        // Wait for the confirmed handoff to settle before asking the display driver.
        await delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await bridge.ApplyAsync(configurationFile, target, false, cancellationToken);
            await delay(TimeSpan.FromMilliseconds(450), cancellationToken);
            DisplayMonitorInfo[] monitors;
            try { monitors = await bridge.ListAsync(cancellationToken); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            { return new("acceptedUnverified", "显示器已接受切换命令，暂时无法读取输入源确认：" + error.Message); }
            var matches = monitors.Where(monitor => string.Equals(monitor.Id, settings.MonitorId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || !matches[0].CanReadInput || matches[0].CurrentInput is null)
                return new("acceptedUnverified", "键鼠已交接，显示器已接受命令；当前视频路径无法读取输入源，请以实际画面为准。");
            if (matches[0].CurrentInput == expected)
                // In the real DP2 -> DP1 test this inactive Windows path returned 15
                // while the monitor still showed Mac. A matching value is not physical proof.
                return local
                    ? new("acceptedUnverified", "Windows 显示接口报告 DP1，实际回切还需 Mac 当前视频链路执行，请以画面为准。")
                    : new("verified");
            // Retry only if the device positively reports that the requested source was not selected.
            if (attempt == 1)
                return new("failed", $"显示器仍报告输入 {matches[0].CurrentInput}，预期为 {expected}；已停止重试。");
        }
        throw new InvalidOperationException("显示器切换验证未完成。");
    }
}