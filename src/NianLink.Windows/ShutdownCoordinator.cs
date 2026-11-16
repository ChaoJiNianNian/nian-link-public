namespace NianLink.Windows;

internal readonly record struct ShutdownPreparation(bool CanExit, bool Busy, string? Error);

/// <summary>Keep every UI and ownership lease alive until the owned keyboard session has stopped.</summary>
internal sealed class ShutdownCoordinator
{
    private int preparing;

    public async Task<ShutdownPreparation> PrepareAsync(Func<bool> isRunning, Func<Task<string?>> stop)
    {
        if (Interlocked.CompareExchange(ref preparing, 1, 0) != 0) return new(false, true, null);
        try
        {
            if (!isRunning()) return new(true, false, null);
            var warning = await stop();
            if (isRunning()) return new(false, false, warning ?? "键鼠共享尚未停止，请重试。");
            if (!string.IsNullOrWhiteSpace(warning))
                return new(false, false, "停止共享时出现问题：" + warning + "\n请确认键鼠和显示器已回到 Windows，再次点击退出。 ");
            return new(true, false, null);
        }
        catch (Exception error)
        {
            return new(false, false, "未能安全停止键鼠共享：" + error.Message + "\n请检查连接后重试退出。");
        }
        finally { Volatile.Write(ref preparing, 0); }
    }
}
