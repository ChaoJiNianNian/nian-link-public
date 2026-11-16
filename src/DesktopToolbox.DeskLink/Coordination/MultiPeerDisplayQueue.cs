namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>One in-flight display command for one separately authenticated peer.</summary>
public sealed class MultiPeerDisplayQueue
{
    private sealed class Pending(MultiPeerDisplayRequest request)
    {
        public MultiPeerDisplayRequest Request { get; } = request;
        public bool Delivered { get; set; }
        public TaskCompletionSource<DisplaySwitchOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<Guid, string> completed = [];
    private readonly Queue<Guid> completionOrder = new();
    private Pending? pending;
    public MultiPeerDisplayQueue(Func<DateTimeOffset>? now = null) => this.now = now ?? (() => DateTimeOffset.UtcNow);
    public bool HasUnresolvedCommand { get { lock (gate) return pending is not null; } }

    public Task<DisplaySwitchOutcome> Enqueue(MultiPeerDisplayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            var timestamp = now();
            if (pending is not null || request.Id == Guid.Empty || completed.ContainsKey(request.Id) || request.Epoch == Guid.Empty || request.Revision < 0 ||
                request.ExpiresUtc <= timestamp || request.ExpiresUtc > timestamp.AddSeconds(10))
                throw new InvalidOperationException("显示器命令无效或上一条命令尚未结束。");
            pending = new(request);
            _ = ExpireUndeliveredAsync(request.Id);
            return pending.Result.Task;
        }
    }

    // Caller must first authenticate the peer. At-most-once delivery is intentional:
    // if the reply is lost, wait for a definitive result rather than risk a second write.
    public MultiPeerDisplayRequest? Deliver(Guid peerEpoch)
    {
        lock (gate)
        {
            if (pending is null || pending.Request.Epoch != peerEpoch) return null;
            if (pending.Request.ExpiresUtc <= now())
            {
                if (!pending.Delivered)
                {
                    pending.Result.TrySetResult(new("notAttempted", "切屏命令已过期，未发送。"));
                    pending = null;
                }
                return null;
            }
            if (pending.Delivered) return null;
            pending.Delivered = true;
            return pending.Request;
        }
    }

    public bool CancelBeforeDelivery(Guid id)
    {
        lock (gate)
        {
            if (pending is null || pending.Request.Id != id || pending.Delivered) return false;
            pending.Result.TrySetResult(new("notAttempted", "切屏命令已取消，未发送。"));
            pending = null;
            return true;
        }
    }

    private async Task ExpireUndeliveredAsync(Guid id)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (gate)
            {
                if (pending is null || pending.Request.Id != id || pending.Delivered) return;
                remaining = pending.Request.ExpiresUtc - now();
                if (remaining <= TimeSpan.Zero)
                {
                    pending.Result.TrySetResult(new("notAttempted", "切屏命令已过期，未发送。"));
                    pending = null;
                    return;
                }
            }
            await Task.Delay(remaining > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : remaining).ConfigureAwait(false);
        }
    }

    // Caller must authenticate the same peer and establish that its driver operation has finished.
    public void Complete(Guid id, string status)
    {
        lock (gate)
        {
            if (completed.TryGetValue(id, out var previous))
            {
                if (previous != status) throw new InvalidOperationException("显示器命令结果不一致。");
                return;
            }
            if (pending is null || !pending.Delivered || pending.Request.Id != id ||
                status is not ("verified" or "acceptedUnverified" or "failed"))
                throw new InvalidOperationException("显示器命令结果无效或已过期。");
            completed.Add(id, status); completionOrder.Enqueue(id);
            if (completionOrder.Count > 128) completed.Remove(completionOrder.Dequeue());
            pending.Result.TrySetResult(new(status));
            pending = null;
        }
    }
}
