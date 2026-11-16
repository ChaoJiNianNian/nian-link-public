namespace NianLink.Windows;

/// <summary>Serializes form writes and both keyboard-start paths through completion.</summary>
internal sealed class StandaloneOperationGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task RunAsync(Func<Task> operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await gate.WaitAsync(token);
        try { await operation(); }
        finally { gate.Release(); }
    }
}
