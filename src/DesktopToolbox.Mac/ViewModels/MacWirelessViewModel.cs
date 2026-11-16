using System.ComponentModel;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.ViewModels;

/// <summary>Displays the confirmed user choice and the interface recovery state.</summary>
public sealed class MacWirelessViewModel(IMacWirelessModeService service, Action<Action>? dispatch = null) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Action<Action> dispatch = dispatch ?? (action => action());
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource lifetime = new();
    private MacWirelessSnapshot snapshot = new(MacAwdlState.Unknown);
    private bool busy;
    private bool disposed;
    private string error = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsBusy => busy;
    public bool IsSmoothEnabled => snapshot.IsSmoothEnabled;
    public bool CanChange => !disposed && !busy && snapshot.CanChange;
    public bool NeedsRestoreRetry => snapshot.KeepEnabled == false && snapshot.State != MacAwdlState.Enabled;
    public bool CanRetryRestore => !disposed && !busy && NeedsRestoreRetry;
    public Task RetryRestoreAsync() => ChangeSmoothModeAsync(false, retryRestore: true);
    public string ErrorMessage => error;
    public string StatusMessage => busy ? "请在 Mac 系统窗口中授权…" : (snapshot.KeepEnabled, snapshot.State) switch
    {
        (true, MacAwdlState.Disabled) => "已开启",
        (true, MacAwdlState.Enabled) => "已开启，正在恢复…",
        (true, _) => "已开启，等待恢复…",
        (false, MacAwdlState.Enabled) => "已关闭",
        (false, _) => "已关闭，互联尚未恢复",
        (_, MacAwdlState.Disabled) => "已开启",
        (_, MacAwdlState.Enabled) => "已关闭",
        (_, MacAwdlState.Unavailable) => "此 Mac 暂不支持此模式",
        _ => "正在确认实际状态…"
    };

    public async Task RefreshAsync()
    {
        if (disposed || !await gate.WaitAsync(0)) return;
        try
        {
            var value = await service.RefreshAsync(lifetime.Token);
            Apply(value, preserveError: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { Apply(new(MacAwdlState.Unknown, Error: "无法读取无线模式状态，请稍后重试。")); }
        finally { gate.Release(); Notify(); }
    }

    public Task SetSmoothModeAsync(bool enabled) => ChangeSmoothModeAsync(enabled, retryRestore: false);

    private async Task ChangeSmoothModeAsync(bool enabled, bool retryRestore)
    {
        if (retryRestore ? !CanRetryRestore : !CanChange || enabled == IsSmoothEnabled) return;
        if (!await gate.WaitAsync(0)) return;
        busy = true; error = ""; Notify();
        try { Apply(await service.SetSmoothModeAsync(enabled, lifetime.Token)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            await ReadActualAfterFailureAsync();
            error = "授权已取消，已按实际状态显示。";
        }
        catch
        {
            await ReadActualAfterFailureAsync();
            error = "切换未完成，请重试并在 Mac 上完成系统授权。";
        }
        finally { busy = false; gate.Release(); Notify(); }
    }

    private async Task ReadActualAfterFailureAsync()
    {
        try { Apply(await service.RefreshAsync(lifetime.Token)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { Apply(new(MacAwdlState.Unknown)); }
    }
    private void Apply(MacWirelessSnapshot value, bool preserveError = false)
    {
        if (disposed) return;
        // A failed state read is not a new user choice. Only an explicit preference
        // returned by the service may replace an already confirmed keep setting.
        if (value.KeepEnabled is null && snapshot.KeepEnabled is not null)
            value = value with { KeepEnabled = snapshot.KeepEnabled };
        var restored = value.KeepEnabled is bool keep && (keep ? value.State == MacAwdlState.Disabled : value.State == MacAwdlState.Enabled);
        if (!preserveError || value.State != snapshot.State || value.KeepEnabled != snapshot.KeepEnabled || value.Error.Length > 0 || restored) error = value.Error;
        snapshot = value;
    }
    private void Notify() => dispatch(() => { if (!disposed) PropertyChanged?.Invoke(this, new(null)); });
    public async Task CancelPendingAsync()
    {
        if (disposed) return;
        var pending = lifetime;
        await pending.CancelAsync();
        await gate.WaitAsync();
        try
        {
            if (!disposed && ReferenceEquals(lifetime, pending))
            {
                lifetime = new();
                pending.Dispose();
            }
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        await gate.WaitAsync();
        gate.Release();
        lifetime.Dispose();
    }
}