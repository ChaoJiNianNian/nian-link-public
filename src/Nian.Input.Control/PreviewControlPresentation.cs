namespace Nian.Input.Control;

/// <summary>Shared viewer labels and affordances; this type never grants input permission.</summary>
public sealed record PreviewControlPresentation(string ModeLabel,string ActionLabel,string Message,
    bool ActionEnabled,bool CanUseKeys,bool EmphasizeViewport,bool ShowPairing)
{
    public static PreviewControlPresentation Create(PreviewControlPhase phase,bool hasSession,bool connectionReady,
        string connectionHint="",string sessionStatus="") => phase switch
        {
            PreviewControlPhase.Preparing=>new("准备中","取消","正在获取可操作的新画面…",hasSession,false,false,false),
            PreviewControlPhase.Controlling=>new("操作中","停止操作","可点击、拖动和滚动 · Esc 停止",hasSession,true,true,false),
            PreviewControlPhase.Stopping=>new("正在停止","正在停止…","正在释放操作，请稍候…",false,false,false,false),
            PreviewControlPhase.Closed=>new("已关闭","开始操作","预览窗口已关闭",false,false,false,false),
            _=>new("仅观看","开始操作",!hasSession||!connectionReady
                ?(string.IsNullOrWhiteSpace(connectionHint)?"连接设备后，可在画面内操作":connectionHint)
                :string.IsNullOrWhiteSpace(sessionStatus)||sessionStatus is "鼠标控制已关闭" or "操作已关闭"
                    ?"点击开始操作，可在画面内点击、拖动和滚动":FriendlyStatus(sessionStatus),
                hasSession&&connectionReady,false,false,true)
        };
    private static string FriendlyStatus(string status)=>status switch
    {
        "未能开启鼠标控制，请检查配对与画面权限"=>"未能开始操作，请检查设备连接与画面权限",
        "控制已停止，刷新档位恢复失败"=>"操作已停止，画面刷新暂未恢复",
        "画面过期或连接中断，控制已停止"=>"画面暂未更新或连接已断开，操作已停止",
        _=>status
    };
}

/// <summary>Approved mini-window colors, shared by the native WPF and Avalonia viewers.</summary>
public sealed record PreviewControlPalette(string Glass,string Surface,string Soft,string Line,string Text,string Muted,
    string Accent,string Button,string ButtonText,string Shadow)
{
    public static PreviewControlPalette ForDark(bool dark)=>dark
        ?new("#253044","#303E55","#1E2A3E","#46536A","#EDF3FC","#A6B5CA","#98BAFF","#A7C4FF","#142B50","#080D1D")
        :new("#EAF0F8","#F7F9FD","#DDE7F4","#C8D5E6","#233247","#586B83","#2467DF","#3979EC","#FFFFFF","#35527B");
}
