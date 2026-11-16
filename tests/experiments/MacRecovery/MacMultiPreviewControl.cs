using DesktopToolbox.DeskLink;

namespace DesktopToolbox.Mac.Services;

/// <summary>Tracks multi-device preview handoffs through confirmed keyboard return.</summary>
public sealed class MacMultiPreviewControl : IDeskflowPreviewControl
{
    private sealed class Pending(MacMultiPeerClient.CollaborationOperation operation, string? id)
    {
        internal readonly MacMultiPeerClient.CollaborationOperation Operation = operation;
        internal readonly string? Id = id;
    }
    private readonly MacMultiPeerClient.CollaborationLease lease;
    private readonly IDeskflowPreviewControl inner;
    private readonly object gate = new();
    private readonly List<Pending> pending = [];
    private readonly Dictionary<string, Task<DeskflowPreviewState>> returns = new(StringComparer.Ordinal);
    private readonly CancellationTokenRegistration cancellation;
    private int inFlightHandoffs;

    public MacMultiPreviewControl(MacMultiPeerClient.CollaborationLease lease, IDeskflowPreviewControl inner)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        // Cancellation callbacks may run synchronously; schedule return outside
        // the owner's short authorization gate and never wait in the callback.
        cancellation = lease.Cancellation.Register(() => _ = Task.Run(ReturnKnownAsync));
    }

    public bool PreviewHandoffSupported => lease.IsActive && inner.PreviewHandoffSupported;
    public string PreviewReturnShortcut => inner.PreviewReturnShortcut;
    public string PreviewHandoffHint => inner.PreviewHandoffHint;
    public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default) => HandoffAsync(null, cancellationToken);
    public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default)
    { ArgumentException.ThrowIfNullOrWhiteSpace(id); return HandoffAsync(id, cancellationToken); }

    private async Task<DeskflowPreviewState> HandoffAsync(string? renewalId, CancellationToken ct)
    {
        if (!lease.TryEnter(out var operation) || operation is null)
            throw new InvalidOperationException("协作连接已暂停，请先归还键鼠并重新连接。");
        if (!lease.TryCommit(() => { lock (gate) inFlightHandoffs++; }))
        {
            operation.Dispose();
            throw new InvalidOperationException("协作连接已暂停，请先归还键鼠并重新连接。");
        }
        var retained = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
            DeskflowPreviewState result;
            try
            {
                result = renewalId is null ? await inner.BeginPreviewAsync(linked.Token) : await inner.RenewPreviewAsync(renewalId, linked.Token);
            }
            catch
            {
                // A failed response does not prove that the remote handoff was
                // rejected. Keep it owned until End or Read confirms return.
                retained = Hold(operation, renewalId);
                if (!lease.IsActive && renewalId is not null) _ = Task.Run(() => ReturnOneAsync(renewalId));
                throw;
            }

            if (result.HasSession && result.Id is { Length: > 0 } id)
            {
                retained = Hold(operation, id);
                if (!lease.IsActive || ct.IsCancellationRequested)
                {
                    do
                    {
                        var returned = await ReturnOneAsync(id);
                        if (!Returned(returned)) throw new InvalidOperationException("键鼠归还尚未完成，请继续归还。");
                    } while (retained && IsHeld(operation));
                    throw new OperationCanceledException("连接已停止，键鼠已请求归还。", linked.Token);
                }
            }
            else if (!Returned(result))
            {
                retained = Hold(operation, renewalId);
                throw new InvalidOperationException("无法确认键鼠是否已归还，请检查连接状态。");
            }
            return result;
        }
        finally
        {
            lock (gate) inFlightHandoffs--;
            if (!retained) operation.Dispose();
        }
    }

    private bool Hold(MacMultiPeerClient.CollaborationOperation operation, string? id)
    {
        lock (gate)
        {
            // A settled renewal of an already-owned session adds no new
            // outstanding work. Do not grow the retained set on every renewal.
            // A concurrent End snapshots its own set, so later responses must
            // stay separate until a subsequent confirmed return.
            if (id is not null && !returns.ContainsKey(id) && pending.Any(item => item.Id == id)) return false;
            pending.Add(new(operation, id));
            return true;
        }
    }

    private bool IsHeld(MacMultiPeerClient.CollaborationOperation operation)
    { lock (gate) return pending.Any(item => ReferenceEquals(item.Operation, operation)); }

    private async Task ReturnKnownAsync()
    {
        string[] ids;
        lock (gate) ids = pending.Select(item => item.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in ids)
            try { await ReturnOneAsync(id); } catch { /* Keep the registration for a later explicit return. */ }
    }

    private Task<DeskflowPreviewState> ReturnOneAsync(string id)
    {
        TaskCompletionSource<DeskflowPreviewState>? completion = null;
        Task<DeskflowPreviewState> task;
        Pending[] observed;
        lock (gate)
        {
            if (returns.TryGetValue(id, out task!)) return task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            returns.Add(id, task);
            observed = pending.Where(item => item.Id == id).ToArray();
        }
        _ = CompleteReturnAsync(id, observed, completion);
        return task;
    }

    private async Task CompleteReturnAsync(string id, Pending[] observed, TaskCompletionSource<DeskflowPreviewState> completion)
    {
        DeskflowPreviewState? result = null;
        Exception? failure = null;
        try
        {
            result = await inner.EndPreviewAsync(id, CancellationToken.None);
            if (Returned(result)) ReleaseReturned(observed);
        }
        catch (Exception error) { failure = error; }
        finally { lock (gate) returns.Remove(id); }
        if (failure is not null) completion.TrySetException(failure);
        else completion.TrySetResult(result!);
    }

    private static bool Returned(DeskflowPreviewState state) => state.Phase == DeskflowPreviewPhase.Viewing && state.Id is null;

    private void ReleaseReturned(Pending[] observed)
    {
        var release = new List<Pending>();
        lock (gate)
        {
            foreach (var item in observed) if (pending.Remove(item)) release.Add(item);
        }
        foreach (var item in release) item.Operation.Dispose();
    }

    public async Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return await ReturnOneAsync(id).WaitAsync(cancellationToken);
    }

    public async Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default)
    {
        Pending[] observed;
        lock (gate) observed = pending.ToArray();
        var result = await inner.ReadPreviewAsync(cancellationToken);
        if (Returned(result))
        {
            Pending[] release;
            lock (gate)
            {
                // Do not mistake a still-running End for drained work.
                release = observed.Where(item => item.Id is null || !returns.ContainsKey(item.Id)).ToArray();
                foreach (var item in release) pending.Remove(item);
            }
            foreach (var item in release) item.Operation.Dispose();
        }
        return result;
    }

    /// <summary>Explicitly rechecks and returns an uncertain handoff after authorization was revoked.</summary>
    public async Task<DeskflowPreviewState> RecoverReturnAsync(CancellationToken cancellationToken = default)
    {
        if (lease.IsActive) throw new InvalidOperationException("请先停止连接，再检查未完成的键鼠归还。");
        var state = await ReadPreviewAsync(cancellationToken);
        if (state.Id is { Length: > 0 } id)
        {
            state = await EndPreviewAsync(id, cancellationToken);
            if (!Returned(state)) throw new InvalidOperationException("键鼠归还尚未完成，请继续归还。");
            state = await ReadPreviewAsync(cancellationToken);
        }
        if (!Returned(state)) throw new InvalidOperationException("无法确认键鼠是否已归还，请继续检查。");
        lock (gate)
            if (pending.Count != 0 || returns.Count != 0 || inFlightHandoffs != 0)
                throw new InvalidOperationException("键鼠归还仍在处理，请稍后重新检查。");
        return state;
    }
}
