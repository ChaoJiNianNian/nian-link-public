using System.Runtime.ExceptionServices;

namespace NianLink.PrivateUdpProbe;

internal static class ProbeChildCleanup
{
    internal static async Task CompleteAsync(bool identityVerified, Func<bool> identityMatches, Action kill, Func<CancellationToken, Task> wait)
    {
        Exception? failure = null;
        try { if (identityVerified && identityMatches()) kill(); }
        catch (Exception error) { failure = error; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await wait(deadline.Token);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
