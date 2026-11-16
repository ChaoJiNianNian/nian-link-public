using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.App.DeskLink;

public sealed partial class WindowsCollaborationHost
{
    private CollaborationServer RequireServer() => Volatile.Read(ref disposed) != 0 ? throw new ObjectDisposedException(nameof(WindowsCollaborationHost)) : server ?? throw new InvalidOperationException("先在工具箱开始共享并完成 Mac 配对。");
    private async Task<T> WithEndpointAsync<T>(Func<CollaborationServer, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await endpointGate.WaitAsync(linked.Token);
        try { return await operation(RequireServer(), linked.Token); }
        finally { endpointGate.Release(); }
    }
    public async Task SendFilesAsync(IReadOnlyList<string> paths, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        if (paths.Count == 0) return;
        await WithEndpointAsync((owned, ct) => owned.QueueFilesAsync(paths, ct), linked.Token);
        Publish(Snapshot.Preview, Snapshot.ConnectionMessage);
    }
    public Task<SharedListing> ListSharedAsync(string? rootId = null, string relativePath = "", CancellationToken token = default)
        => WithEndpointAsync((owned, ct) => Task.FromResult(owned.ListLocalShared(rootId, relativePath)), token);
    public async Task<TransferReceipt> DownloadSharedAsync(string rootId, string relativePath, CancellationToken token = default)
    {
        if (settings.ReceiveDirectory.Length == 0) throw new InvalidOperationException("请先在小窗设置中选择本机收件目录。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        return await WithEndpointAsync((owned, ct) => owned.CopyLocalSharedAsync(rootId, relativePath, settings.ReceiveDirectory, ct), linked.Token);
    }
    public Task<SharedPreview> GetSharedPreviewAsync(string rootId, string relativePath, CancellationToken token = default) => WithEndpointAsync((owned, ct) => owned.GetLocalSharedPreviewAsync(rootId, relativePath, ct), token);
    public async Task SetReceiveDirectoryAsync(string path, CancellationToken token = default)
    {
        var next = (settings with { ReceiveDirectory = path }).Validate();
        if (path.Length != 0 && !Directory.Exists(path)) throw new DirectoryNotFoundException("请选择已存在的收件目录。");
        await endpointGate.WaitAsync(token);
        try
        {
            server?.UpdateReceiveDirectory(NullIfEmpty(path));
            try { await SaveAsync(value => value with { ReceiveDirectory = next.ReceiveDirectory }, token); }
            catch { server?.UpdateReceiveDirectory(NullIfEmpty(settings.ReceiveDirectory)); throw; }
        }
        finally { endpointGate.Release(); }
        Publish(Snapshot.Preview, Snapshot.ConnectionMessage);
    }
    public async Task SetSharedDirectoryAsync(string path, CancellationToken token = default)
    {
        var next = (settings with { SharedDirectory = path }).Validate();
        if (path.Length != 0 && !Directory.Exists(path)) throw new DirectoryNotFoundException("请选择已存在的共享目录。");
        await endpointGate.WaitAsync(token);
        try
        {
            server?.UpdateSharedDirectories(Shares(next));
            try { await SaveAsync(value => value with { SharedDirectory = next.SharedDirectory }, token); }
            catch { server?.UpdateSharedDirectories(Shares(settings)); throw; }
        }
        finally { endpointGate.Release(); }
        Publish(Snapshot.Preview, Snapshot.ConnectionMessage);
    }
}
