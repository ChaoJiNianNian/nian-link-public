using DesktopToolbox.DeskLink.Coordination;

namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Paired peer polls only while explicitly allowed; acknowledgements may be retried without repeating a write.</summary>
public sealed class MultiScreenDisplayWorker
{
    private readonly CollaborationClient client;
    private readonly MultiPeerDisplayExecutor executor;
    private readonly Func<bool> permitted;
    private readonly SemaphoreSlim gate = new(1, 1);
    private (Guid Id, string Status)? pendingAcknowledgement;

    public MultiScreenDisplayWorker(CollaborationClient client, MultiPeerDisplayExecutor executor, Func<bool> permitted)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.permitted = permitted ?? throw new ArgumentNullException(nameof(permitted));
    }

    public async Task PollAsync(CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            await AcknowledgeAsync(ct);
            var status = await client.GetStatusAsync(ct, displayEpoch: IsPermitted() ? executor.Epoch : Guid.Empty);
            var request = status.DisplayRequest;
            if (request is null) return;
            try
            {
                var result = await executor.ExecuteAsync(request, ct, IsPermitted);
                pendingAcknowledgement = (request.Id, result.Status);
            }
            catch (OperationCanceledException)
            {
                pendingAcknowledgement = (request.Id, "failed");
                throw;
            }
            await AcknowledgeAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await AcknowledgeAsync(ct); await client.GetStatusAsync(ct, displayEpoch: Guid.Empty); }
        finally { gate.Release(); }
    }

    private async Task AcknowledgeAsync(CancellationToken ct)
    {
        if (pendingAcknowledgement is not { } acknowledgement) return;
        await client.CompleteDisplayRequestAsync(acknowledgement.Id, acknowledgement.Status, ct);
        pendingAcknowledgement = null;
    }
    private bool IsPermitted() { try { return permitted(); } catch (Exception) { return false; } }
}
