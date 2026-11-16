using DesktopToolbox.DeskLink;

namespace DesktopToolbox.App.DeskLink;

/// <summary>Maps one paired client and its host to the existing mini window without inventing focus.</summary>
public static class PeerMiniState
{
    public static MiniCollaborationSnapshot Create(string local, string host, DeskLinkState keyboard, bool dataConnected)
    {
        DeskflowProfile.ValidateName(local);
        DeskflowProfile.ValidateName(host);
        ArgumentNullException.ThrowIfNull(keyboard);
        if (string.Equals(local, host, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("本机和主控名称不能相同。", nameof(host));

        var running = keyboard.ProcessState == DeskLinkProcessState.Running;
        var keyboardReady = running && keyboard.ConnectionState == DeskLinkConnectionState.Connected && keyboard.PendingPeerFingerprint is null;
        var connected = keyboardReady && dataConnected;
        var active = string.Equals(keyboard.ActiveComputer, local, StringComparison.Ordinal) ||
                     string.Equals(keyboard.ActiveComputer, host, StringComparison.Ordinal) ? keyboard.ActiveComputer! : "";
        return new MiniCollaborationSnapshot
        {
            MultiDeviceMode = true,
            Devices = [new MiniDevice(host, "主控 · " + host)],
            SelectedDevice = host,
            LocalDeviceName = local,
            SharedFolderIsRemote = true,
            Connected = connected,
            ConnectionMessage = connected ? "主控已连接" : "等待主控连接",
            ActiveComputer = active,
            KeyboardSharingRunning = running,
            KeyboardSharingConnected = keyboardReady,
            KeyboardCanStart = !running,
            KeyboardCanStop = running,
            KeyboardSharingMessage = keyboard.PendingPeerFingerprint is not null ? "请先核对键鼠指纹" :
                keyboardReady ? "键鼠共享已连接" : running ? "等待键鼠连接" : "键鼠共享未启动",
            CanSwitchWindows = connected,
            CanSwitchMac = connected
        };
    }
}
