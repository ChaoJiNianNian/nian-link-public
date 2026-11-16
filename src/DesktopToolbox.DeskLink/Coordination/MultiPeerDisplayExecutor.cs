namespace DesktopToolbox.DeskLink.Coordination;

public sealed record DisplayInputObservation(uint? CurrentInput, bool Reliable);

/// <summary>
/// Device implementations must retain their operation until the physical driver has stopped;
/// cancellation cannot be reported as completion while a native write may still occur.
/// A verified outcome requires reliable physical confirmation, not just an accepted write.
/// </summary>
public interface IMultiPeerDisplayDevice
{
    Task<DisplayInputObservation> ReadAsync(string monitor, CancellationToken ct);
    Task<DisplaySwitchOutcome> WriteAsync(string monitor, uint input, CancellationToken ct);
}

/// <summary>Locally validates an authenticated request before allowing a display write.</summary>
public sealed class MultiPeerDisplayExecutor(MultiPeerDisplayCommandGuard guard, IMultiPeerDisplayDevice device)
{
    private readonly MultiPeerDisplayCommandGuard guard = guard ?? throw new ArgumentNullException(nameof(guard));
    private readonly IMultiPeerDisplayDevice device = device ?? throw new ArgumentNullException(nameof(device));
    public Guid Epoch => guard.Epoch;

    public async Task<DisplaySwitchOutcome> ExecuteAsync(MultiPeerDisplayRequest request, CancellationToken ct = default, Func<bool>? permitted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await guard.Operation.WaitAsync(0, ct)) return new("failed", "上一次显示器操作尚未完成。");
        var admitted = false;
        try
        {
            if (!IsPermitted(permitted)) return new("failed", "显示器切换权限已关闭。");
            guard.ValidateBeforeRead(request);
            var observed = await device.ReadAsync(request.MonitorId, ct);
            if (!observed.Reliable || observed.CurrentInput is null || !IsPermitted(permitted))
                return new("failed", "无法确认显示器当前输入，未执行切换。 ");
            guard.Begin(request, observed.CurrentInput);
            admitted = true;
            if (!IsPermitted(permitted)) return new("failed", "显示器切换权限已关闭。");
            var result = await device.WriteAsync(request.MonitorId, request.TargetInput, ct);
            return result.Status is "verified" or "acceptedUnverified" or "failed"
                ? result : new("failed", "显示器切换结果无法识别。");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException)
        { return new("failed", admitted ? "显示器操作未确认，请检查实际画面。" : "显示器切换未通过本机检查。"); }
        finally { try { if (admitted) guard.Complete(request.Id); } finally { guard.Operation.Release(); } }
    }

    private static bool IsPermitted(Func<bool>? permitted)
    { try { return permitted?.Invoke() != false; } catch (Exception) { return false; } }
}
