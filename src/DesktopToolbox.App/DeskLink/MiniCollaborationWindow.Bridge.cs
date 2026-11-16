using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DesktopToolbox.App.Hosting;

namespace DesktopToolbox.App.DeskLink;

public sealed partial class MiniCollaborationWindow
{
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    private async Task<object?> InvokePageAsync(MiniBridgeRequest request,IReadOnlyList<string> dropped,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var payload=request.Payload;
        switch(request.Action)
        {
            case "switch":await service.SwitchAsync(payload.GetProperty("platform").GetString()!,token);break;
            case "device.select":
                recipientBarrier.Publish(null);
                try{if(detached is not null)await detached.CloseForDeviceChangeAsync();await service.SelectDeviceAsync(payload.GetProperty("name").GetString()!,token);}
                finally{UpdateFromService();}
                break;
            case "device.rendered":
                var rendered=payload.GetProperty("name").GetString()!;
                var selected=service.Snapshot;
                if(!selected.MultiDeviceMode||selected.SelectedDevice!=rendered||!selected.Devices.Any(device=>device.Name==rendered)||
                    !recipientBarrier.Acknowledge(rendered,payload.GetProperty("version").GetInt64()))
                    throw new InvalidOperationException("设备状态已变化，请稍后再试。");
                break;
            case "connection.start":await service.StartSharingAsync(token);break;
            case "connection.stop":await service.StopSharingAsync(token);break;
            case "connection.openSettings":
                if(ConnectionSettingsRequested is null)throw new InvalidOperationException("连接设置暂不可用，请稍后重试。");
                ConnectionSettingsRequested.Invoke();break;
            case "preview.setEnabled":await service.SetPreviewEnabledAsync(payload.GetProperty("enabled").GetBoolean(),token);UpdateFromService();break;
            case "preview.setRefreshSeconds":await service.SetPreviewRefreshSecondsAsync(payload.GetProperty("seconds").GetInt32(),token);UpdateFromService();break;
            case "preview.open":if(service.Snapshot.PreviewEnabled)OpenDetachedPreview();break;
            case "clipboard.setEnabled":await service.SetClipboardEnabledAsync(payload.GetProperty("enabled").GetBoolean(),token);UpdateFromService();break;
            case "window.setPinned":preferences=preferences with {Pinned=payload.GetProperty("pinned").GetBoolean()};await SavePreferencesAsync();UpdateFromService();break;
            case "window.collapse":SetCollapsed(true);break;
            case "window.expand":SetCollapsed(false);break;
            case "window.close":Close();break;
            case "window.resize":
                var width=payload.GetProperty("width").GetDouble();if(width!=(preferences.Collapsed?83:398))break;
                var maximum=MiniNativeWindow.WorkArea(MiniNativeWindow.Screen(handle,preferences.Display)).Height/(MiniNativeWindow.Dpi(handle)/96d)-16;
                Width=width;Height=Math.Clamp(payload.GetProperty("height").GetDouble(),100,Math.Max(100,maximum));UpdateSizeAndPlacement();break;
            case "window.drag":
                if(preferences.Pinned||(GetAsyncKeyState(1)&0x8000)==0)break;
                try{dragging=true;NativeMaterial.Drag(this);preferences=MiniNativeWindow.Remember(handle,preferences);}
                finally{dragging=false;UpdateSizeAndPlacement();await SavePreferencesAsync();}break;
            case "files.drop":var dropTarget=CaptureRecipient();await Task.Run(()=>SendToRecipientAsync(dropTarget,dropped,token),token);return new {queued=true};
            case "files.choose":
                var fileTarget=CaptureRecipient();var files=new Microsoft.Win32.OpenFileDialog{Title="选择要发送的文件",Multiselect=true,CheckFileExists=true};if(files.ShowDialog(this)!=true)return new {cancelled=true};await Task.Run(()=>SendToRecipientAsync(fileTarget,files.FileNames,token),token);return new {queued=true};
            case "files.chooseFolder":
                var folderTarget=CaptureRecipient();var folder=new OpenFolderDialog{Title="选择要发送的文件夹",Multiselect=false};if(folder.ShowDialog(this)!=true)return new {cancelled=true};await Task.Run(()=>SendToRecipientAsync(folderTarget,[folder.FolderName],token),token);return new {queued=true};
            case "shared.list":return await service.ListSharedAsync(payload.GetProperty("rootId").GetString(),payload.GetProperty("relativePath").GetString()!,token);
            case "shared.preview":return await service.GetSharedPreviewAsync(payload.GetProperty("rootId").GetString()!,payload.GetProperty("relativePath").GetString()!,token);
            case "shared.download":return await service.DownloadSharedAsync(payload.GetProperty("rootId").GetString()!,payload.GetProperty("relativePath").GetString()!,token);
            case "directory.choose":
                var kind=payload.GetProperty("kind").GetString();var directory=new OpenFolderDialog{Title=kind=="receive"?"选择本机收件文件夹":"选择供 Mac 查看资料的文件夹",Multiselect=false};
                if(directory.ShowDialog(this)==true){if(kind=="receive")await service.SetReceiveDirectoryAsync(directory.FolderName,token);else await service.SetSharedDirectoryAsync(directory.FolderName,token);UpdateFromService();}break;
            case "inbox.open":
                var path=service.Snapshot.ReceiveDirectory;if(!Path.IsPathFullyQualified(path)||!Directory.Exists(path))throw new DirectoryNotFoundException("本机收件文件夹不存在，请先在设置中选择。");
                Process.Start(new ProcessStartInfo(Path.GetFullPath(path)){UseShellExecute=true});break;
            case "shared.stop":await service.SetSharedDirectoryAsync("",token);UpdateFromService();break;
            default:throw new ArgumentException("此小窗不支持该操作。");
        }
        return null;
    }
    private string? CaptureRecipient()
    {
        var state=service.Snapshot;
        if(!state.MultiDeviceMode)return null;
        if(!recipientBarrier.TryGetRecipient(out var recipient)||recipient!=state.SelectedDevice||!state.Devices.Any(device=>device.Name==recipient))
            throw new InvalidOperationException("设备界面正在更新，请稍后再发送。");
        return recipient;
    }
    private Task SendToRecipientAsync(string? recipient,IReadOnlyList<string> paths,CancellationToken token)=>
        recipient is null?service.SendFilesAsync(paths,token):service.SendFilesToDeviceAsync(recipient,paths,token);
}

