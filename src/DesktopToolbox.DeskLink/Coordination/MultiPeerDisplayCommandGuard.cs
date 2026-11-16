namespace DesktopToolbox.DeskLink.Coordination;

public sealed record MultiPeerDisplayRequest(Guid Id, Guid Epoch, string MonitorId, uint ExpectedInput,
    uint TargetInput, long Revision, DateTimeOffset ExpiresUtc);

/// <summary>
/// Local authorization and replay guard for a future authenticated display command endpoint.
/// The caller must authenticate the paired sender and read the local monitor input first.
/// </summary>
public sealed class MultiPeerDisplayCommandGuard
{
    private const int MaximumCommandsPerEpoch = 1024;
    private readonly object gate = new();
    private readonly string monitorId;
    private readonly HashSet<uint> approvedInputs;
    private readonly Func<DateTimeOffset> now;
    private readonly HashSet<Guid> used = [];
    private Guid? active;
    private long lastRevision = -1;
    internal SemaphoreSlim Operation { get; } = new(1, 1);
    public Guid Epoch { get; } = Guid.NewGuid();

    public MultiPeerDisplayCommandGuard(string monitorId, IEnumerable<uint> approvedInputs, Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrWhiteSpace(monitorId) || monitorId.Length > 256 || monitorId.Any(char.IsControl))
            throw new ArgumentException("显示器信息无效。", nameof(monitorId));
        ArgumentNullException.ThrowIfNull(approvedInputs);
        this.monitorId = monitorId;
        this.approvedInputs = approvedInputs.ToHashSet();
        if (this.approvedInputs.Count < 2 || this.approvedInputs.Count > 16 || this.approvedInputs.Any(input => input is 0 or > 255))
            throw new ArgumentException("显示器接口配置无效。", nameof(approvedInputs));
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // This registers the command before calling the physical driver. A failed or cancelled
    // attempt must not be retried with the same ID, because it might already have executed.
    public void Begin(MultiPeerDisplayRequest request, uint? confirmedCurrentInput)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (!Valid(request) || confirmedCurrentInput != request.ExpectedInput)
                throw new InvalidOperationException("显示器切换请求未通过本机检查。");
            used.Add(request.Id); lastRevision = request.Revision; active = request.Id;
        }
    }

    public void ValidateBeforeRead(MultiPeerDisplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate) if (!Valid(request)) throw new InvalidOperationException("显示器切换请求未通过本机检查。");
    }

    private bool Valid(MultiPeerDisplayRequest request)
    {
        var timestamp = now();
        return active is null && request.Id != Guid.Empty && request.Epoch == Epoch && !used.Contains(request.Id) &&
            used.Count < MaximumCommandsPerEpoch && request.Revision > lastRevision &&
            request.ExpiresUtc > timestamp && request.ExpiresUtc <= timestamp.AddSeconds(10) &&
            string.Equals(request.MonitorId, monitorId, StringComparison.Ordinal) &&
            request.ExpectedInput != request.TargetInput && approvedInputs.Contains(request.ExpectedInput) && approvedInputs.Contains(request.TargetInput);
    }

    // Call only after the hardware driver has actually finished, even if it failed.
    public void Complete(Guid id)
    {
        lock (gate)
        {
            if (active != id || id == Guid.Empty) throw new InvalidOperationException("显示器操作已经过期。");
            active = null;
        }
    }
}
