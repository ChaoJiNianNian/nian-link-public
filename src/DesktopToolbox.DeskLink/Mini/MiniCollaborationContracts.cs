using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using Nian.Input.Control;

namespace DesktopToolbox.App.DeskLink;

/// <summary>One approved device available in the mini window.</summary>
public sealed record MiniDevice(string Name, string Label);

public sealed record MiniCollaborationSnapshot
{
    public bool Connected { get; init; }
    public string ConnectionMessage { get; init; } = "尚未连接 Mac";
    public string ActiveComputer { get; init; } = "";
    public bool KeyboardSharingRunning { get; init; }
    public bool KeyboardSharingConnected { get; init; }
    public bool KeyboardCanStart { get; init; }
    public bool KeyboardCanStop { get; init; }
    public string KeyboardSharingMessage { get; init; } = "键鼠共享未启动";
    public bool CanSwitchWindows { get; init; }
    public bool CanSwitchMac { get; init; }
    public bool PreviewEnabled { get; init; }
    public int PreviewRefreshSeconds { get; init; } = 30;
    public PreviewFrame Preview { get; init; } = PreviewFrame.Off();
    public Guid? PassivePreviewRequestId { get; init; }
    public PreviewSourceStatus? RemotePreviewSourceStatus { get; init; }
    public bool ClipboardEnabled { get; init; }
    public string ClipboardMessage { get; init; } = "剪贴板互通已暂停";
    public string ReceiveDirectory { get; init; } = "";
    public string SharedDirectory { get; init; } = "";
    public bool SharedFolderIsRemote { get; init; }
    public IReadOnlyList<TransferProgress> Transfers { get; init; } = [];
    public IReadOnlyList<MiniDevice> Devices { get; init; } = [];
    public bool MultiDeviceMode { get; init; }
    public string? SelectedDevice { get; init; }
    public string? LocalDeviceName { get; init; }
}

public interface IMiniCollaborationService
{
    MiniCollaborationSnapshot Snapshot { get; }
    event EventHandler? Changed;
    bool PreviewControlReady=>false;
    string PreviewControlHint=>"当前版本仅支持观看。";
    PreviewControlSession? CreatePreviewControlSession()=>null;
    DeskflowPreviewSession? CreateDeskflowPreviewSession()=>null;
    bool PreviewInputOwnsForeground=>false;
    Task RefreshAsync(CancellationToken token=default);
    Task SwitchAsync(string platform,CancellationToken token=default);
    Task SelectDeviceAsync(string name,CancellationToken token=default)=>Task.FromException(new NotSupportedException("当前版本不支持多设备选择。"));
    Task StartSharingAsync(CancellationToken token=default);
    Task StopSharingAsync(CancellationToken token=default);
    Task SetPreviewEnabledAsync(bool enabled,CancellationToken token=default);
    Task SetPreviewRefreshSecondsAsync(int seconds,CancellationToken token=default);
    void SetPreviewVisible(bool visible,bool highResolution=false);
    Task SetClipboardEnabledAsync(bool enabled,CancellationToken token=default);
    Task SendFilesAsync(IReadOnlyList<string> paths,CancellationToken token=default);
    Task SendFilesToDeviceAsync(string name,IReadOnlyList<string> paths,CancellationToken token=default)=>Task.FromException(new NotSupportedException("当前版本不支持指定设备发送。"));
    Task<SharedListing> ListSharedAsync(string? rootId=null,string relativePath="",CancellationToken token=default);
    Task<TransferReceipt> DownloadSharedAsync(string rootId,string relativePath,CancellationToken token=default);
    Task<SharedPreview> GetSharedPreviewAsync(string rootId,string relativePath,CancellationToken token=default);
    Task SetReceiveDirectoryAsync(string path,CancellationToken token=default);
    Task SetSharedDirectoryAsync(string path,CancellationToken token=default);
}
