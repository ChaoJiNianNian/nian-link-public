using DesktopToolbox.Windows.DeskSwitch;

namespace NianLink.Windows;

internal static class DisplayMappingGuard
{
    internal static void EnsureUnchangedForStart(DeskSwitchSettings pending, DeskSwitchSettings saved)
    {
        if (pending.LinkDisplay != saved.LinkDisplay || pending.MonitorId != saved.MonitorId ||
            pending.LocalInput != saved.LocalInput || pending.RemoteInput != saved.RemoteInput)
            throw new InvalidOperationException("显示器接口有未保存的更改，请先保存设置，再重新发送配对文件给 Mac。");
    }
}
