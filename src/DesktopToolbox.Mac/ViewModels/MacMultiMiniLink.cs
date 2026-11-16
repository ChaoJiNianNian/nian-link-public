using System.ComponentModel;
using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Mac.ViewModels;

/// <summary>Maps an explicitly approved multi-device peer to the shared mini controls.</summary>
public sealed class MacMultiMiniLink : IMacMiniLink
{
    private readonly Func<CancellationToken, Task> stop;
    private readonly Func<bool, Task> switchTarget;
    private DeskLinkState state = new();
    private bool dataConnected;
    private bool controlUnconfirmed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string WindowsName { get; }
    public string MacName { get; }
    public string ErrorMessage => controlUnconfirmed ? "键鼠归还尚未确认，请检查键鼠所在设备；归还尝试不能确认旧请求已经停止。" : "";
    public bool IsSharingRunning => state.ProcessState == DeskLinkProcessState.Running;
    public bool IsInputConnected => IsSharingRunning && state.ConnectionState == DeskLinkConnectionState.Connected && state.PendingPeerFingerprint is null;
    public bool CanStart => false; // The settings window owns creation of the current connection.
    public bool CanStop => IsSharingRunning;
    public bool CanSwitch => IsInputConnected && dataConnected && !controlUnconfirmed;
    public bool CanReturnToWindows => IsInputConnected;
    public bool IsMacActive => CanSwitch && state.ActiveComputer == MacName;
    public bool IsWindowsActive => CanSwitch && state.ActiveComputer == WindowsName;

    public MacMultiMiniLink(string host, string mac, Func<CancellationToken, Task> stop, Func<bool, Task> switchTarget)
    {
        DeskflowProfile.ValidateName(host);
        DeskflowProfile.ValidateName(mac);
        if (string.Equals(host, mac, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("本机和主控名称不能相同。");
        WindowsName = host; MacName = mac;
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
        this.switchTarget = switchTarget ?? throw new ArgumentNullException(nameof(switchTarget));
    }

    public void Update(DeskLinkState state, bool dataConnected, bool controlUnconfirmed = false)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.dataConnected = dataConnected;
        this.controlUnconfirmed = controlUnconfirmed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public Task StartAsync() => throw new InvalidOperationException("请在连接设置中启动共享。");
    public Task StopAsync() => CanStop ? stop(CancellationToken.None) : throw new InvalidOperationException("共享尚未启动。");
    public Task SwitchAsync(string target)
    {
        if (target != MacName && target != WindowsName || target == MacName && !CanSwitch || target == WindowsName && !CanReturnToWindows)
            throw new InvalidOperationException("请先连接并核对主控，仅可切换本机或已配对主控。");
        return switchTarget(target == MacName);
    }
}
