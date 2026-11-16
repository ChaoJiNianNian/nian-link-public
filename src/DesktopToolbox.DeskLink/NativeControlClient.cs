using System.Collections.Concurrent;
using System.Text.Json;
namespace DesktopToolbox.DeskLink;

/// <summary>Client for the opt-in current-user-only native control endpoint. No synthetic keystrokes.</summary>
public sealed class NativeControlClient : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<NativeControlReply>> pending = new(StringComparer.Ordinal);
    private readonly Task reader;
    private IOException? terminalError;
    private long revision = -1;
    private int disposed;
    internal bool IsDisconnected => Volatile.Read(ref terminalError) is not null || Volatile.Read(ref disposed) != 0 || reader.IsCompleted;
    public event EventHandler<FocusChangedEvent>? FocusChanged;
    public event EventHandler<Exception>? Disconnected;
    public NativeControlClient(Stream stream) { this.stream = stream ?? throw new ArgumentNullException(nameof(stream)); reader = ReadLoopAsync(); }
    public static async Task<NativeControlClient> ConnectAsync(int processId, CancellationToken ct = default)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        return new(await LocalControlTransport.ConnectAsync("nian-deskflow-control-" + processId, ct, processId));
    }
    public Task<NativeControlReply> GetStateAsync(CancellationToken ct = default) => RequestAsync("state", null, ct);
    public Task<NativeControlReply> SwitchAsync(string target, CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(target); return RequestAsync("switch", target, ct);
    }
    public Task<NativeControlReply> SwitchIfCurrentAsync(string target, string expectedActive, long expectedRevision, CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(target);
        DeskflowProfile.ValidateName(expectedActive);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        return RequestAsync("switch-if-current", target, ct, expectedActive, expectedRevision);
    }
    private void ThrowIfDisconnected()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lifetime.Token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref terminalError) is { } error) throw new IOException("Native control is disconnected.", error);
    }
    private async Task<NativeControlReply> RequestAsync(string action, string? target, CancellationToken ct,
        string? expectedActive = null, long? expectedRevision = null)
    {
        ThrowIfDisconnected();
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<NativeControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(requestId, completion)) throw new InvalidOperationException("Duplicate request identifier.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await writeLock.WaitAsync(timeout.Token);
            try
            {
                ThrowIfDisconnected();
                if (expectedRevision is null)
                    await JsonLineProtocol.WriteAsync(stream, new { version = 1, requestId, action, target }, timeout.Token);
                else
                    await JsonLineProtocol.WriteAsync(stream, new { version = 1, requestId, action, target, expectedActive, expectedRevision }, timeout.Token);
            }
            finally { writeLock.Release(); }
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally { pending.TryRemove(requestId, out _); }
    }
    private static void ValidateReply(NativeControlReply reply)
    {
        if (reply.RequestId is not { Length: 32 } id || id.Any(c => !Uri.IsHexDigit(c)) || reply.Revision < 0)
            throw new InvalidDataException("Invalid native response identifier or revision.");
        if (reply.ActiveComputer is { } active) DeskflowProfile.ValidateName(active);
        if (reply.ConnectedComputers is null || reply.ConnectedComputers.Length > 16) throw new InvalidDataException("Invalid native connected computer list.");
        foreach (var name in reply.ConnectedComputers) DeskflowProfile.ValidateName(name);
        if (reply.ConnectedComputers.Distinct(StringComparer.Ordinal).Count() != reply.ConnectedComputers.Length)
            throw new InvalidDataException("Duplicate native connected computer.");
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await JsonLineProtocol.ReadAsync(stream, lifetime.Token) ?? throw new EndOfStreamException("Native control disconnected.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1) throw new InvalidDataException("Unsupported native control protocol version.");
                if (root.TryGetProperty("event", out var eventName))
                {
                    if (eventName.GetString() == "focusChanged")
                    {
                        var active = root.GetProperty("activeComputer").GetString() ?? throw new InvalidDataException("Missing active computer.");
                        DeskflowProfile.ValidateName(active);
                        var next = root.GetProperty("revision").GetInt64();
                        if (next < 0) throw new InvalidDataException("Invalid native focus revision.");
                        if (next > Interlocked.Read(ref revision))
                        {
                            Interlocked.Exchange(ref revision, next);
                            var isolated = root.TryGetProperty("previewInputIsolated", out var capability) && capability.GetBoolean();
                            FocusChanged?.Invoke(this, new(active, next) { PreviewInputIsolated = isolated });
                        }
                    }
                    continue;
                }
                var reply = root.Deserialize<NativeControlReply>(JsonLineProtocol.Json) ?? throw new InvalidDataException("Empty native control response.");
                // Legacy core rejects unsupported requests with an empty active
                // computer; preserve the rejection without treating it as focus.
                if (!reply.Ok && reply.ActiveComputer == "") reply = reply with { ActiveComputer = null };
                ValidateReply(reply);
                if (pending.TryRemove(reply.RequestId!, out var completion)) completion.TrySetResult(reply);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or ArgumentException or OperationCanceledException or ObjectDisposedException or KeyNotFoundException or FormatException)
        {
            var failure = error as IOException ?? new IOException("Native control protocol failed.", error);
            Volatile.Write(ref terminalError, failure);
            foreach (var completion in pending.Values) completion.TrySetException(failure);
            if (!lifetime.IsCancellationRequested) Disconnected?.Invoke(this, failure);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); stream.Dispose();
        try { await reader; } catch (OperationCanceledException) { }
        lifetime.Dispose();
        // Concurrent requests can still release writeLock while cancellation unwinds.
    }
}
