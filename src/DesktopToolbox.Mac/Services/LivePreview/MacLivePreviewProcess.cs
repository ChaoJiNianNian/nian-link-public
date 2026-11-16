using System.Diagnostics;
using System.Text.Json;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.Mac.Services.LivePreview;

/// <summary>A distinct stream-mode child; stopping it cannot terminate the JSON/clipboard helper.</summary>
internal sealed class MacLivePreviewProcess : IMacLivePreviewHelper
{
    private readonly Process process;
    private int disposed;
    private MacLivePreviewProcess(Process process){this.process=process;}
    public Stream Output=>process.StandardOutput.BaseStream;

    internal static async ValueTask<IMacLivePreviewHelper> StartAsync(string executable,LivePreviewCaptureOptions options,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var process=new Process{StartInfo=new(executable)
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        process.StartInfo.ArgumentList.Add("--realtime-stream");
        if(!process.Start()){process.Dispose();throw new IOException("无法启动Mac实时媒体组件。");}
        var owned=new MacLivePreviewProcess(process);_=DrainAsync(process.StandardError);
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(6));
            var request=JsonSerializer.Serialize(new{action="start",fps=options.FramesPerSecond,width=options.MaxWidth,height=options.MaxHeight,quality=options.JpegQuality});
            await process.StandardInput.WriteLineAsync(request.AsMemory(),deadline.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();return owned;
        }
        catch{await owned.DisposeAsync().ConfigureAwait(false);throw;}
    }

    private static async Task DrainAsync(StreamReader stderr)
    {
        try{var buffer=new char[1024];while(await stderr.ReadAsync(buffer).ConfigureAwait(false)>0){}}
        catch(Exception error)when(error is IOException or ObjectDisposedException){ }
    }

    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try
        {
            try
            {
                if(!process.HasExited)
                {
                    using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await process.StandardInput.WriteLineAsync("{\"action\":\"stop\"}".AsMemory(),deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
            }
            catch(Exception error)when(error is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException){ }
            try{process.StandardInput.Close();}catch(IOException){ }
            // Break a backpressured native stdout write before waiting for its graceful stop.
            try{process.StandardOutput.Dispose();}catch(IOException){ }
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);}
            catch(TimeoutException)
            {
                if(!process.HasExited)process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        finally{process.Dispose();}
    }
}
