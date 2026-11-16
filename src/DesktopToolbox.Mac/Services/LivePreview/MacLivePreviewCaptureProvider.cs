using System.Runtime.CompilerServices;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;

namespace DesktopToolbox.Mac.Services.LivePreview;

public sealed class MacLivePreviewCaptureProvider : ILivePreviewCaptureProvider
{
    private readonly Func<LivePreviewCaptureOptions,CancellationToken,ValueTask<IMacLivePreviewHelper>> start;
    private readonly Func<bool> supported;
    public MacLivePreviewCaptureProvider(string? executable = null)
    {
        var path=executable??Path.Combine(AppContext.BaseDirectory,"DeskLinkMedia");
        supported=()=>OperatingSystem.IsMacOS()&&File.Exists(path);
        start=(options,token)=>MacLivePreviewProcess.StartAsync(path,options,token);
    }
    internal MacLivePreviewCaptureProvider(Func<LivePreviewCaptureOptions,CancellationToken,ValueTask<IMacLivePreviewHelper>> start,Func<bool> supported)
    {this.start=start;this.supported=supported;}
    public bool IsSupported => supported();
    public async IAsyncEnumerable<LivePreviewCapturePacket> CaptureAsync(LivePreviewCaptureOptions options,[EnumeratorCancellation]CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(options);options.Validate();cancellationToken.ThrowIfCancellationRequested();
        if(!IsSupported)
        {yield return new(){Kind=LivePreviewCaptureKind.Unavailable,Sequence=1,FailureCode="unsupported"};yield break;}
        await using var helper=await start(options,cancellationToken).ConfigureAwait(false);
        long sequence=0;
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame=await MacLivePreviewNativeProtocol.ReadAsync(helper.Output,cancellationToken).ConfigureAwait(false);
            if(frame is null)
            {yield return new(){Kind=LivePreviewCaptureKind.Unavailable,Sequence=sequence+1,FailureCode="stopped"};yield break;}
            if(frame.Sequence<=sequence||frame.Sequence==long.MaxValue)throw new InvalidDataException("Mac实时媒体序号没有前进。");
            if(frame.Kind==LivePreviewCaptureKind.Image&&(frame.Width>options.MaxWidth||frame.Height>options.MaxHeight))
                throw new InvalidDataException("Mac实时画面超出本轮协商尺寸。");
            sequence=frame.Sequence;yield return frame;
            if(frame.Kind==LivePreviewCaptureKind.Unavailable)yield break;
        }
    }
}

internal interface IMacLivePreviewHelper : IAsyncDisposable
{
    Stream Output { get; }
}
