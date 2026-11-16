using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace DesktopToolbox.App.Hosting;

public sealed class SingleInstance : IAsyncDisposable
{
    private readonly Mutex mutex;
    private readonly string name;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private int disposed;
    public bool IsOwner { get; }

    public SingleInstance(string directory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..20];
        name = "DesktopToolbox-" + hash;
        // Keeping the named handle open is the process lease; no thread owns the mutex.
        mutex = new Mutex(false, @"Local\" + name, out var created);
        IsOwner = created;
    }

    public async Task ActivateExistingAsync()
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            await client.ConnectAsync(1500);
            await client.WriteAsync(new byte[] { 1 });
        }
        catch (Exception exception) when (exception is IOException or TimeoutException) { }
    }

    public void Listen(Action activate)
    {
        if (!IsOwner || listener is not null) throw new InvalidOperationException("只有主实例可以监听一次。");
        listener = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    var buffer = new byte[1];
                    if (await pipe.ReadAsync(buffer, lifetime.Token) == 1) activate();
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { await Task.Delay(200, lifetime.Token).ConfigureAwait(false); }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        try
        {
            if (listener is not null)
                try { await listener; } catch (OperationCanceledException) { }
        }
        finally { mutex.Dispose(); lifetime.Dispose(); }
    }
}
