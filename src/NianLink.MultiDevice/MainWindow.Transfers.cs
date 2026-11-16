using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private Task? transferTask;
    private CancellationTokenSource? transferCancellation;
    private long transferGeneration;
    private long lastAutomaticReceiveAttempt;

    private void BeginHostTransfer(MultiPeerRuntime current, string device, string[] paths)
    {
        BeginTransfer(HostFilesStatus, async (token, _) =>
        {
            await current.QueueFilesAsync(device, paths, token);
            return "文件已加入 " + DisplayDeviceName(device) + " 的待收列表。";
        });
        ShowSettingsPage("HostFilesCard");
    }

    private void BeginPeerTransfer(CollaborationClient data, string[] paths)
    {
        BeginTransfer(PeerFilesStatus, async (token, progress) =>
        {
            var receipt = await data.SendFilesAsync(paths, progress, token);
            return $"已发送 {receipt.SavedPaths.Count} 个项目到主控。";
        });
        ShowSettingsPage("PeerFilesCard");
    }

    private void BeginPeerReceive(CollaborationClient data, string directory) =>
        BeginTransfer(PeerFilesStatus, async (token, progress) =>
        {
            var receipts = await data.ReceivePendingAsync(directory, progress, token);
            lastAutomaticReceiveAttempt = 0;
            return receipts.Count == 0 ? "暂无待收文件。" : $"已接收 {receipts.Count} 项，保存在 {directory}。";
        });

    private void BeginPeerDownload(CollaborationClient data, SharedItem item, string directory) =>
        BeginTransfer(PeerFilesStatus, async (token, progress) =>
        {
            var receipt = await data.DownloadSharedAsync(item.RootId, item.RelativePath, directory, progress, token);
            return $"已保存 {receipt.SavedPaths.Count} 个项目到 {directory}。";
        });

    private void BeginTransfer(TextBlock status, Func<CancellationToken, IProgress<TransferProgress>, Task<string>> action)
    {
        if (transferTask is { IsCompleted: false }) throw new InvalidOperationException("已有文件正在传输，请先等待或取消。");
        var cancellation = new CancellationTokenSource();
        transferCancellation = cancellation;
        var generation = ++transferGeneration;
        var progress = new Progress<TransferProgress>(value =>
        {
            if (generation == transferGeneration && ReferenceEquals(transferCancellation, cancellation)) UpdateTransferProgress(status, value);
        });
        status.Text = "正在准备文件…";
        transferTask = RunTransferAsync(status, action, cancellation, progress);
    }

    private async Task RunTransferAsync(TextBlock status, Func<CancellationToken, IProgress<TransferProgress>, Task<string>> action,
        CancellationTokenSource cancellation, IProgress<TransferProgress> progress)
    {
        try { status.Text = await action(cancellation.Token, progress); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { status.Text = "传输已取消，原文件保留。"; }
        catch (Exception error) { status.Text = "传输未完成：" + UserErrorMessage.For(error); ShowFailure(error, "传输"); }
        finally
        {
            if (ReferenceEquals(transferCancellation, cancellation)) transferCancellation = null;
            cancellation.Dispose();
        }
    }

    private static void UpdateTransferProgress(TextBlock status, TransferProgress value)
    {
        var percent = value.TotalBytes > 0 ? Math.Clamp(value.BytesTransferred * 100.0 / value.TotalBytes, 0, 100) : 0;
        status.Text = $"{value.Name} · {percent:0}%";
    }

    private async void CancelTransfer_Click(object sender, RoutedEventArgs e) => await CancelTransferAsync();

    private void MaybeReceivePending(CollaborationClient data, CollaborationStatus status)
    {
        if (status.PendingTransfers.Count == 0 || transferTask is { IsCompleted: false }) return;
        if (peerReceiveDirectory is null || !Directory.Exists(peerReceiveDirectory))
        { PeerFilesStatus.Text = "主控有待收文件，请先选择可用的收件文件夹。"; return; }
        if (lastAutomaticReceiveAttempt != 0 && Stopwatch.GetElapsedTime(lastAutomaticReceiveAttempt) < TimeSpan.FromSeconds(30)) return;
        lastAutomaticReceiveAttempt = Stopwatch.GetTimestamp();
        BeginPeerReceive(data, peerReceiveDirectory);
    }
    private async Task CancelTransferAsync()
    {
        var cancellation = transferCancellation;
        if (cancellation is null) return;
        cancellation.Cancel();
        if (transferTask is { } task) await task;
    }
}
