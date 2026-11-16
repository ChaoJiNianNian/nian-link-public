namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Throttles image capture only; connection, clipboard and demand renewal keep their own polling cadence.</summary>
public sealed class PreviewCaptureCadence(TimeProvider? clock = null)
{
    public const int DefaultRefreshSeconds = 30;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private bool active;
    private long capturedAt;
    private int seconds, width, height;

    public static int NormalizeRefreshSeconds(int seconds) => seconds is 1 or 5 or 10 or 30 or 60 ? seconds : DefaultRefreshSeconds;
    public static void ValidateRefreshSeconds(int seconds)
    {
        if (NormalizeRefreshSeconds(seconds) != seconds) throw new ArgumentException("请选择 1、5、10、30 或 60 秒的预览刷新间隔。", nameof(seconds));
    }

    /// <summary>Reserves a capture attempt when due. A changed size, interval or renewed visibility captures immediately.</summary>
    public bool ShouldCapture(bool requested, int refreshSeconds, int width = 960, int height = 540)
    {
        ValidateRefreshSeconds(refreshSeconds);
        lock (gate)
        {
            if (!requested) { active = false; return false; }
            var now = clock.GetTimestamp();
            if (active && seconds == refreshSeconds && this.width == width && this.height == height && clock.GetElapsedTime(capturedAt, now) < TimeSpan.FromSeconds(refreshSeconds)) return false;
            active = true; capturedAt = now; seconds = refreshSeconds; this.width = width; this.height = height;
            return true;
        }
    }
    public void Reset() { lock (gate) active = false; }
}
