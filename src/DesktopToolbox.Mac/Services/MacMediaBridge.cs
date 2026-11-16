using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nian.Input.Control;

namespace DesktopToolbox.Mac.Services;

/// <summary>One owned native helper. It captures only in response to an explicit request.</summary>
public sealed class MacMediaBridge : IMacMediaBridge
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string executable;
    private Process? child;
    private bool disposed;
    public MacMediaBridge(string? executable = null) => this.executable = executable ?? Path.Combine(AppContext.BaseDirectory, "DeskLinkMedia");

    public async Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("媒体桥仅在Mac运行。");
            if (child is null || child.HasExited)
            {
                child?.Dispose();
                if (!File.Exists(executable)) throw new FileNotFoundException("Mac媒体组件缺失，请重新安装完整客户端。");
                child = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                if (!child.Start()) throw new IOException("无法启动Mac媒体组件。");
                // Drain native diagnostics without retaining user clipboard or screen information.
                _ = DrainAsync(child.StandardError);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
                await child.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                var line = await ReadBoundedLineAsync(child.StandardOutput, deadline.Token).ConfigureAwait(false);
                return JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            }
            catch
            {
                await StopChildAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { gate.Release(); }
    }
    public async Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default)
    {
        using var document = await RequestAsync(new { action = "capture", width, height, windowID = windowId }, cancellationToken).ConfigureAwait(false);
        return MacScreenCaptureReply.Parse(document.RootElement.GetRawText());
    }
    public async Task<PreviewSourceGeometry?> ReadPreviewGeometryAsync(int imageWidth = 1,int imageHeight = 1,CancellationToken cancellationToken = default)
    {
        if(imageWidth is <1 or >1920||imageHeight is <1 or >1080)throw new ArgumentOutOfRangeException(nameof(imageWidth));
        using var document=await RequestAsync(new {action="sourceGeometry",width=imageWidth,height=imageHeight},cancellationToken).ConfigureAwait(false);
        return MacScreenCaptureReply.ParseSourceGeometry(document.RootElement.GetRawText(),imageWidth,imageHeight);
    }
    private static async Task DrainAsync(StreamReader reader) { try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer) > 0) { } } catch (IOException) { } catch (ObjectDisposedException) { } }
    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            var newline = Array.IndexOf(buffer, '\n', 0, count);
            var length = newline >= 0 ? newline : count;
            if (text.Length + length > 6 * 1024 * 1024) throw new IOException("Mac媒体回复超过限制。");
            text.Append(buffer, 0, length);
            if (newline >= 0) return text.ToString();
        }
        throw new IOException("Mac媒体组件已退出。");
    }
    private async Task StopChildAsync()
    {
        if (child is null) return;
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        finally { child.Dispose(); child = null; }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (disposed) return; disposed = true; await StopChildAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
