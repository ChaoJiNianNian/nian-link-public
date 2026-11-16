namespace Nian.Input.Mac;

/// <summary>Own injected click sequence only; never observes physical click contents.</summary>
internal sealed class MacDesktopClickState
{
    private int lastButton = -1, count;
    private MacDesktopPoint lastPoint;
    private TimeSpan lastAt;
    private bool dragged;

    internal int Press(int button, MacDesktopPoint point, TimeSpan at, TimeSpan interval)
    {
        var elapsed = at - lastAt;
        var nearby = Math.Abs(point.X - lastPoint.X) <= 4 && Math.Abs(point.Y - lastPoint.Y) <= 4;
        count = !dragged && button == lastButton && elapsed >= TimeSpan.Zero && elapsed <= interval && nearby
            ? Math.Min(count + 1, 3) : 1;
        lastButton = button;
        lastPoint = point;
        lastAt = at;
        dragged = false;
        return count;
    }

    internal void MovedWhilePressed(MacDesktopPoint point)
    {
        if (Math.Abs(point.X - lastPoint.X) > 4 || Math.Abs(point.Y - lastPoint.Y) > 4) dragged = true;
    }

    internal void Reset()
    {
        lastButton = -1;
        count = 0;
        lastPoint = default;
        lastAt = default;
        dragged = false;
    }
}
