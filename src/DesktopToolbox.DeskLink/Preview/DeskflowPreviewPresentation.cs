using Nian.Input.Control;

namespace DesktopToolbox.DeskLink.Preview;

/// <summary>Presentation for physical Deskflow handoff, without coordinate input or separate input pairing.</summary>
public static class DeskflowPreviewPresentation
{
    public static PreviewControlPresentation Create(DeskflowPreviewSession session,bool connected,string hint,string viewer,string target)
    {
        var phase=session.Phase;
        var shortcut=session.ReturnShortcut;
        return phase switch
        {
            PreviewControlPhase.Preparing=>new("准备交接","取消","正在准备新画面，只交接键鼠…",session.CanStop,false,false,false),
            PreviewControlPhase.Controlling=>new("键鼠操作","归还键鼠",$"键鼠已交给 {target} · 屏幕留在 {viewer}\n按 {shortcut} 收回键鼠，鼠标沿远端位置移动",session.CanStop,false,true,false),
            PreviewControlPhase.Stopping=>new("正在归还",session.CanStop?"重试归还":"正在归还…",session.Status,session.CanStop,false,false,false),
            PreviewControlPhase.Closed=>new("已关闭","开始操作","预览已关闭",false,false,false,false),
            _=>new("仅观看","开始操作",session.StartHint??DescribeStop(session.Status)??(!connected||!session.Supported?hint:$"开始后只交接键鼠，屏幕保持不变 · {shortcut} 归还"),connected&&session.CanStart,false,false,false)
        };
    }

    // Only display known local reasons: backend/exception text may contain private data.
    // The session resets its status as soon as another operation begins.
    private static string? DescribeStop(string status)=>status switch
    {
        "窗口已失焦，操作已停止" or "窗口已失焦，正在收回键鼠" => "操作已结束：预览窗口失去焦点。",
        "窗口已隐藏，操作已停止" or "预览已隐藏，鼠标控制已停止" => "操作已结束：预览窗口已隐藏。",
        "窗口已最小化，操作已停止" => "操作已结束：预览窗口已最小化。",
        "画面超过 8 秒未更新，正在收回键鼠" => "操作已结束：画面超过 8 秒未更新。",
        "画面已断开，正在收回键鼠" or "画面连接中断，鼠标控制已停止" => "操作已结束：画面连接中断。",
        "画面已关闭，正在收回键鼠" or "画面已关闭，鼠标控制已停止" => "操作已结束：画面已关闭。",
        "没有可操作画面，鼠标控制已停止" or "画面暂不可用，操作已停止" => "操作已结束：没有可操作画面。",
        "实时画面已断开，正在收回键鼠" => "操作已结束：实时画面连接中断。",
        "实时画面权限不可用，正在收回键鼠" => "操作已结束：实时画面访问未获允许。",
        "实时画面不可用，正在收回键鼠" => "操作已结束：实时画面不可用。",
        "实时画面无法显示，正在收回键鼠" => "操作已结束：实时画面无法显示。",
        "对方画面已关闭或不可用，正在收回键鼠" or "对方画面不可用，正在收回键鼠" => "操作已结束：对方画面已关闭或不可用。",
        "来源状态不可用，正在收回键鼠" => "操作已结束：画面来源状态不可用。",
        "设备连接已断开，正在收回键鼠" => "操作已结束：设备连接已断开。",
        "连接暂不可用，正在收回键鼠" => "操作已结束：控制状态检查暂不可用。",
        "Windows 已锁屏，鼠标控制已停止" => "操作已结束：Windows 已锁屏。",
        "配对已移除，鼠标控制已停止" => "操作已结束：配对已移除。",
        "配对已变化，鼠标控制已停止" => "操作已结束：配对已变化。",
        "键鼠正在归还，请稍候" => "操作已结束：键鼠状态发生变化。",
        "未能交接键鼠，请检查共享与画面连接" => "未能交接键鼠，请检查共享与画面连接。",
        "正在收回键鼠…" or "已取消，正在收回键鼠" or "已停止操作，仅观看画面" or "已退出操作，仅观看画面" => "键鼠已归还，当前仅观看。",
        "键鼠已返回，当前仅观看" => "键鼠已返回观看端，当前仅观看。",
        "其他窗口已接管键鼠，当前仅观看" => "操作已结束：其他窗口接管了键鼠。",
        _ => null
    };
}
