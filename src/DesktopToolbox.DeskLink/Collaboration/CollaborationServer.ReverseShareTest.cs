using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink.Collaboration;

// Isolated transport experiment only: it does not grant access to a peer's files.
public sealed partial class CollaborationServer
{
    private readonly object reverseTestGate = new();
    private readonly string reverseTestScope = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private ReverseShareQueue? reverseTestQueue;
    private Guid reverseTestAuthorization;
    private bool reverseTestStopped;
    private long reverseTestDeliverySequence;
    private const int ReverseTestMaximumResponseBytes = 65536;

    private bool HasReverseTest => options.ReverseShareTestVault is not null && options.ReverseShareTestPeer is not null;

    private async Task ReverseTestWithGrantAsync(Action action, CancellationToken ct)
    {
        if (!HasReverseTest) throw new NotSupportedException("Reverse-share test is disabled.");
        var token = await ReadTokenAsync(ct);
        try
        {
            if (!await options.ReverseShareTestVault!.TryCommitAuthorizedAsync(options.ReverseShareTestPeer!, token, action, ct))
                throw new UnauthorizedAccessException("Device trust was revoked.");
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    internal async Task EnableReverseShareTestAsync(CancellationToken ct = default)
    {
        await ReverseTestWithGrantAsync(() =>
        {
            lock (reverseTestGate)
            {
                if (reverseTestStopped || running is null) throw new InvalidOperationException("Test service is not running.");
                reverseTestQueue?.Dispose(); reverseTestQueue = new(reverseTestScope);
                reverseTestAuthorization = reverseTestQueue.Authorize(reverseTestScope);
            }
        }, ct);
    }

    internal async Task<ReverseShareRequest> EnqueueReverseShareTestAsync(ReverseShareOperation operation, string? root, string relative, TimeSpan lifetime, CancellationToken ct = default)
    {
        ReverseShareRequest? result = null;
        await ReverseTestWithGrantAsync(() => { lock (reverseTestGate) result = (reverseTestQueue ?? throw new InvalidOperationException("Test is not enabled.")).Enqueue(reverseTestScope, reverseTestAuthorization, operation, root, relative, lifetime); }, ct);
        return result!;
    }

    internal async Task<byte[]?> TakeReverseShareTestResultAsync(ReverseShareRequest request, CancellationToken ct = default)
    {
        byte[]? result = null;
        await ReverseTestWithGrantAsync(() => { lock (reverseTestGate) result = reverseTestQueue?.TakeResult(reverseTestScope, request.Authorization, request.Id); }, ct);
        return result;
    }

    // Caller holds the vault authorization and live-preview generation guards.
    private (bool Supported, ReverseShareRequest? Request, long Sequence) DeliverReverseShareTest()
    {
        lock (reverseTestGate)
        {
            if (reverseTestQueue is null || reverseTestStopped) return (false, null, 0);
            var request = reverseTestQueue.Deliver(reverseTestScope, reverseTestAuthorization);
            return (true, request, request is null ? 0 : checked(++reverseTestDeliverySequence));
        }
    }

    private void CompleteReverseShareTest(ReverseShareTestCompletion result)
    {
        if (result.Data is null || result.Data.Length > ReverseTestMaximumResponseBytes) throw new ArgumentException("Test response exceeds the limit.");
        lock (reverseTestGate)
            if (reverseTestQueue is null || reverseTestStopped || !reverseTestQueue.Complete(reverseTestScope, result.Authorization, result.RequestId, result.Data))
                throw new InvalidOperationException("Test request is no longer active.");
    }

    private void StopReverseShareTest()
    {
        lock (reverseTestGate) { reverseTestStopped = true; reverseTestAuthorization = Guid.Empty; reverseTestQueue?.Dispose(); reverseTestQueue = null; }
    }

    private void StartReverseShareTest()
    {
        lock (reverseTestGate) reverseTestStopped = false;
    }
}
