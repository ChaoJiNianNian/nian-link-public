using System.Text;
namespace DesktopToolbox.DeskLink;

internal sealed class DeskflowCoreIpcClient(Stream stream) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private Task reader = Task.CompletedTask;
    public event EventHandler<KeyValuePair<string,string>>? Message;
    public event EventHandler<Exception>? Disconnected;
    public async Task StartAsync(CancellationToken ct)
    {
        await SendAsync("hello=1.27.0+081f6478", ct);
        reader = ReadAsync();
    }
    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (text.Length > 4096 || text.Any(c => c is '\r' or '\n')) throw new InvalidDataException("Invalid upstream IPC command.");
        await writer.WaitAsync(ct);
        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), ct); await stream.FlushAsync(ct); }
        finally { writer.Release(); }
    }
    private async Task ReadAsync()
    {
        var mismatchCount = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await JsonLineProtocol.ReadAsync(stream, lifetime.Token) ?? throw new EndOfStreamException("Deskflow IPC disconnected.");
                var index = line.IndexOf('=');
                var command = index < 0 ? line : line[..index];
                var argument = index < 0 ? "" : line[(index + 1)..];
                if (command == "versionMismatch")
                {
                    if (++mismatchCount > 1 || argument.Length > 128 || argument.Any(char.IsControl)) throw new InvalidDataException("Deskflow IPC version negotiation failed.");
                    await SendAsync("hello=" + argument, lifetime.Token);
                }
                else if (command == "bye") throw new EndOfStreamException("Deskflow status channel closed by the core.");
                else if (command is not ("hello" or "ok")) Message?.Invoke(this, new(command, argument));
            }
        }
        // Any failure in protocol processing or a message consumer also ends the
        // channel: its last reported connection must not remain authoritative.
        catch (Exception error)
        { if (!lifetime.IsCancellationRequested) Disconnected?.Invoke(this, error); }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); stream.Dispose();
        try { await reader; } catch (OperationCanceledException) { }
        finally { lifetime.Dispose(); writer.Dispose(); }
    }
}
