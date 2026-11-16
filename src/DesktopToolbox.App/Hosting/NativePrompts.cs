using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopToolbox.Core;

namespace DesktopToolbox.App.Hosting;

public sealed class NativePrompts(Dispatcher dispatcher, Func<Window?> owner, Action<ProcessStartInfo>? startProcess = null) : IUserPrompts
{
    private static int activeDialogs;
    private volatile bool suspended;
    public void SuspendNewDialogs() => suspended = true;
    public static bool IsDialogOpen => Volatile.Read(ref activeDialogs) > 0;
    private readonly SemaphoreSlim dialogs = new(1, 1);
    private async Task<T> ShowAsync<T>(Func<Window?, T> action)
    {
        if (suspended) throw new OperationCanceledException("工具箱正在退出，已取消新的确认窗口。");
        await dialogs.WaitAsync();
        try { return await dispatcher.InvokeAsync(() => { if (suspended) throw new OperationCanceledException("工具箱正在退出，已取消新的确认窗口。"); Interlocked.Increment(ref activeDialogs); try { return action(owner() is { IsVisible: true } window ? window : null); } finally { Interlocked.Decrement(ref activeDialogs); } }); }
        finally { dialogs.Release(); }
    }
    public Task<bool> ConfirmAsync(string title, string message) => ShowAsync(window =>
        (window is null ? System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
        : System.Windows.MessageBox.Show(window, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) == MessageBoxResult.Yes);
    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => ShowAsync(window =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        var accepted = window is null ? dialog.ShowDialog() : dialog.ShowDialog(window);
        return accepted == true ? dialog.FolderName : null;
    });
    public Task<string?> PickFileAsync(string title, string filter) => ShowAsync(window =>
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        var accepted = window is null ? dialog.ShowDialog() : dialog.ShowDialog(window);
        return accepted == true ? dialog.FileName : null;
    });
    public Task<string?> PickSaveFileAsync(string title, string filter, string suggestedName) => ShowAsync(window =>
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title, Filter = filter, FileName = suggestedName,
            DefaultExt = Path.GetExtension(suggestedName), AddExtension = true, CheckPathExists = true, OverwritePrompt = false
        };
        dialog.FileOk += (_, args) =>
        {
            if (!File.Exists(dialog.FileName) && !Directory.Exists(dialog.FileName)) return;
            args.Cancel = true;
            if (window is null) System.Windows.MessageBox.Show("这个文件名已存在，请换一个名称。", title, MessageBoxButton.OK, MessageBoxImage.Information);
            else System.Windows.MessageBox.Show(window, "这个文件名已存在，请换一个名称。", title, MessageBoxButton.OK, MessageBoxImage.Information);
        };
        var accepted = window is null ? dialog.ShowDialog() : dialog.ShowDialog(window);
        return accepted == true ? dialog.FileName : null;
    });
    public Task RevealAsync(string path, bool selectFile = false)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath)) throw new FileNotFoundException("位置已不存在。", fullPath);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
        // Explorer parses its switch before the path; quoting both as one argument opens Documents instead.
        if (File.Exists(fullPath) || selectFile) start.Arguments = "/select, \"" + fullPath + "\"";
        else start.ArgumentList.Add(fullPath);
        if (startProcess is not null) startProcess(start);
        else { using var process = Process.Start(start); }
        return Task.CompletedTask;
    }
}
