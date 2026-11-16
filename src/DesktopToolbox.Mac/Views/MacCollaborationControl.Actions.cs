using System.Text.Json;
using Avalonia;
using Avalonia.Platform.Storage;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Views;

public sealed partial class MacCollaborationControl
{
    private async Task<object?> DispatchActionAsync(MacWebRequest request)
    {
        var value = request.Payload; var ct = lifetime.Token;
        switch (request.Action)
        {
            case "device.select" when link is DesktopToolbox.Mac.ViewModels.MacMultiMiniLink:
                if (Text(value, "name") != link.WindowsName) throw new InvalidOperationException("只能选择已配对的主控。");
                break;
            case "connection.start":
                await link.StartAsync();
                if (link.ErrorMessage.Length > 0) throw new InvalidOperationException(link.ErrorMessage);
                break;
            case "connection.stop":
                await link.StopAsync();
                if (link.ErrorMessage.Length > 0) throw new InvalidOperationException(link.ErrorMessage);
                break;
            case "switch":
                var platform = Text(value, "platform");
                if (platform == "Mac" && link.CanSwitch) await link.SwitchAsync(link.MacName);
                else if (platform == "Windows" && link.CanReturnToWindows) await link.SwitchAsync(link.WindowsName);
                else throw new InvalidOperationException("请先在连接设置中开始共享，等待键鼠连接就绪。");
                if (link.ErrorMessage.Length > 0) throw new InvalidOperationException(link.ErrorMessage);
                break;
            case "preview.setEnabled":
                // The multi-device mini switch shows the remote screen only; it must never change Mac capture permission.
                if (link is DesktopToolbox.Mac.ViewModels.MacMultiMiniLink) await service.SetViewPreviewEnabledAsync(Boolean(value, "enabled"), ct);
                else await service.SetPreviewEnabledAsync(Boolean(value, "enabled"), ct);
                break;
            case "preview.setRefreshSeconds": await service.SetPreviewRefreshSecondsAsync(value.GetProperty("seconds").GetInt32(), ct); break;
            case "clipboard.setEnabled": await service.SetClipboardEnabledAsync(Boolean(value, "enabled"), ct); break;
            case "preview.open": OpenDetached(); break;
            case "window.setPinned": await service.SetPinnedAsync(Boolean(value, "pinned"), ct); break;
            case "window.collapse": collapsed = true; requested = new Size(83, 164); RefreshViewerVisibility(); LayoutRequested?.Invoke(); break;
            case "window.expand": collapsed = false; var expanded = MacPreviewPresentation.MiniSize(service.Snapshot.Settings.PreviewEnabled, detached?.IsVisible == true); requested = new Size(expanded.Width, expanded.Height); RefreshViewerVisibility(); LayoutRequested?.Invoke(); break;
            case "window.resize":
                var width = value.GetProperty("width").GetDouble(); var height = value.GetProperty("height").GetDouble();
                if (!MacWebRequest.IsValidSize(width, height) || (collapsed ? width != 83 : width != 398)) throw new ArgumentException("小窗尺寸无效。");
                requested = new Size(width, height); LayoutRequested?.Invoke(); break;
            case "window.drag":
                if (!service.Snapshot.Settings.Pinned)
                { moving = true; if (!BeginNativeDrag()) moving = false; else { placementTimer.Stop(); placementTimer.Start(); } }
                break;
            case "window.close": owner.Hide(); RefreshViewerVisibility(); break;
            case "files.choose":
                var files = await owner.StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = true, Title = "发送文件到 Windows" }); await SendStorageAsync(files, ct); break;
            case "files.chooseFolder":
                var folders = await owner.StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true, Title = "发送文件夹到 Windows" }); await SendStorageAsync(folders, ct); break;
            case "files.drop": throw new InvalidOperationException("请从访达直接拖入文件或文件夹。");
            case "files.nativeDrop" when request.Native:
                var paths = value.GetProperty("paths").EnumerateArray().Select(path => path.GetString() ?? "").ToArray();
                if (paths.Length is 0 or > 128 || paths.Any(path => path.Length > 4096 || !Path.IsPathFullyQualified(path))) throw new ArgumentException("拖入的文件路径无效。");
                await service.SendFilesAsync(paths, ct); break;
            case "files.pause": service.PauseSending(); break;
            case "files.retry": await service.RetrySendAsync(ct); break;
            case "shared.list": return await service.ListSharedAsync(OptionalText(value, "rootId"), OptionalText(value, "relativePath") ?? "", ct);
            case "shared.preview": return await service.PreviewSharedAsync(SharedReference(value), ct);
            case "shared.download": await service.DownloadSharedAsync(SharedReference(value), ct); return new { saved = true };
            case "directory.choose":
                if (Text(value, "kind") != "receive") throw new InvalidOperationException("请在 Windows 端选择需要共享的文件夹。");
                var destination = await owner.StorageProvider.OpenFolderPickerAsync(new() { Title = "选择 Mac 收件文件夹" });
                if (destination.Count > 0 && destination[0].TryGetLocalPath() is { } path) await service.SetReceiveDirectoryAsync(path, ct);
                break;
            case "inbox.open":
                var directory = service.Snapshot.Settings.ReceiveDirectory;
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) throw new InvalidOperationException("请先选择 Mac 收件文件夹。");
                var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add(directory); using (System.Diagnostics.Process.Start(start)) { } break;
            case "connection.openSettings": OpenSettings?.Invoke(); break;
            case "wireless.setEnabled": await wireless.SetSmoothModeAsync(Boolean(value, "enabled")); if (wireless.ErrorMessage.Length > 0) throw new InvalidOperationException(wireless.ErrorMessage); break;
            case "wireless.retry": await wireless.RetryRestoreAsync(); if (wireless.ErrorMessage.Length > 0) throw new InvalidOperationException(wireless.ErrorMessage); break;
            default: throw new ArgumentException("不支持此项操作。");
        }
        return new { };
    }
    private Task SendStorageAsync(IEnumerable<IStorageItem> items, CancellationToken ct)
    {
        var paths = items.Select(item => item.TryGetLocalPath()).Where(path => path is not null).Cast<string>().ToArray();
        return paths.Length == 0 ? Task.CompletedTask : service.SendFilesAsync(paths, ct);
    }
    private static SharedItem SharedReference(JsonElement value)
    {
        var root = Text(value, "rootId"); var path = Text(value, "relativePath");
        if (root.Length is 0 or > 128 || path.Length > 4096) throw new ArgumentException("共享资料路径无效。");
        return new SharedItem(root, path, "", false, 0, DateTimeOffset.MinValue);
    }
    private static string? OptionalText(JsonElement value, string property) => value.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;
    private static string Text(JsonElement value, string property) => OptionalText(value, property) ?? throw new ArgumentException("操作参数不完整。");
    private static bool Boolean(JsonElement value, string property) => value.GetProperty(property).GetBoolean();
}

