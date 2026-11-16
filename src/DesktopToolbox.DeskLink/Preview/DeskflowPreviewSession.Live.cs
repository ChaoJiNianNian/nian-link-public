using DesktopToolbox.DeskLink.Collaboration.LivePreview;
namespace DesktopToolbox.DeskLink.Preview;

public sealed partial class DeskflowPreviewSession
{
    private readonly IControlLivePreviewSource? liveSource;
    private bool liveSourceFailed;
    private sealed partial class Run
    {
        internal readonly Guid CaptureEpoch=Guid.NewGuid();
        internal readonly SortedDictionary<long,long> ImageReceivedAt=new();
        internal long LastLiveSequence,LastImageSequence,LastPresentedSequence,LiveHealthAt,PresentedAt;
        internal bool HasLiveHealth,HasPresentedImage;
    }
    public DeskflowPreviewSession(IDeskflowPreviewControl backend, IControlLivePreviewSource liveSource, TimeProvider? clock = null)
        : this(backend, (_, _) => Task.CompletedTask, clock)
    {
        ArgumentNullException.ThrowIfNull(liveSource);
        this.liveSource=liveSource;liveSource.PacketReceived+=ObserveLivePacket;liveSource.StreamClosed+=ObserveLiveClosed;
    }
    public event Action<Guid, LivePreviewCapturePacket>? LivePacketReceived;
    public bool UsesLivePreview => liveSource is not null;
    // Business polling and media transport have separate lifetimes. During bounded
    // preparation or a healthy active stream, a transient poll failure is not a media failure.
    public bool HasProtectedLiveOperation
    {get{lock(gate)return !disposed&&liveSource is not null&&run is{} current&&!current.StopRequested&&(!current.Active||LiveFresh(current,TimeSpan.FromSeconds(8)));}}
    public bool LiveSourceFailed {get{lock(gate)return liveSourceFailed;}}
    public Task StopAfterLiveFailureAsync(string reason)
    {Run? current;lock(gate)current=run;return current is null?Task.CompletedTask:StopRunAfterLiveFailureAsync(current,reason);}
    private Task StopRunAfterLiveFailureAsync(Run current,string reason)
    {lock(gate)if(ReferenceEquals(run,current)&&liveSource is not null)liveSourceFailed=true;return StopRunAsync(current,reason);}
    public Guid? LiveOperationId {get{lock(gate)return liveSource is null?null:run?.CaptureEpoch;}}
    public void NotifyLiveFramePresented(Guid operationId, LivePreviewCapturePacket image)
    {
        lock(gate)
        {
            if(disposed||run is not{} current||current.CaptureEpoch!=operationId||current.StopRequested||
                image.Kind!=LivePreviewCaptureKind.Image||image.Sequence<=current.LastPresentedSequence||image.Sequence>current.LastImageSequence)return;
            if(!current.ImageReceivedAt.TryGetValue(image.Sequence,out var receivedAt))return;
            var age=clock.GetElapsedTime(receivedAt,clock.GetTimestamp());
            if(age<TimeSpan.Zero||age>TimeSpan.FromSeconds(2))return;
            current.LastPresentedSequence=image.Sequence;current.PresentedAt=clock.GetTimestamp();current.HasPresentedImage=true;
            foreach(var old in current.ImageReceivedAt.Keys.TakeWhile(key=>key<=image.Sequence).ToArray())current.ImageReceivedAt.Remove(old);
            if(LiveFresh(current,TimeSpan.FromSeconds(2)))current.FrameReady.TrySetResult();
        }
    }
    private void ObserveLivePacket(Guid epoch,LivePreviewCapturePacket packet)
    {
        Run? stop=null;bool forward=false;
        lock(gate)
        {
            if(disposed||run is not{} current||current.CaptureEpoch!=epoch||current.StopRequested||packet.Sequence<=current.LastLiveSequence)return;
            current.LastLiveSequence=packet.Sequence;
            if(packet.Kind==LivePreviewCaptureKind.Unavailable)stop=current;
            else if(packet.Kind==LivePreviewCaptureKind.Image&&packet.Width>0&&packet.Height>0&&packet.JpegBytes.Length>0&&packet.CapturedAtUtc!=default)
            {
                current.LastImageSequence=packet.Sequence;current.LiveHealthAt=clock.GetTimestamp();current.HasLiveHealth=true;
                current.ImageReceivedAt[packet.Sequence]=current.LiveHealthAt;
                while(current.ImageReceivedAt.Count>256)current.ImageReceivedAt.Remove(current.ImageReceivedAt.First().Key);
                forward=true;
            }
            else if(packet.Kind==LivePreviewCaptureKind.HealthyIdle&&packet.JpegBytes.Length==0&&packet.CapturedAtUtc==default)
            {current.LiveHealthAt=clock.GetTimestamp();current.HasLiveHealth=true;}
            else stop=current;
        }
        if(stop is not null)_=StopRunAfterLiveFailureAsync(stop,"实时画面不可用，正在收回键鼠");
        if(forward&&LivePacketReceived is{} callbacks)
            foreach(Action<Guid,LivePreviewCapturePacket> callback in callbacks.GetInvocationList())try{callback(epoch,packet);}catch{}
    }
    private void ObserveLiveClosed(Guid epoch,string failureCode)
    {
        Run? current;lock(gate)current=!disposed&&run?.CaptureEpoch==epoch&&run.StopRequested==false?run:null;
        if(current is not null)_=StopRunAfterLiveFailureAsync(current,failureCode=="unauthorized"?
            "实时画面权限不可用，正在收回键鼠":"实时画面已断开，正在收回键鼠");
    }
    private bool LiveFresh(Run current,TimeSpan maximumAge)
    {
        if(!current.HasLiveHealth||!current.HasPresentedImage)return false;
        var healthAge=clock.GetElapsedTime(current.LiveHealthAt,clock.GetTimestamp());
        if(healthAge<TimeSpan.Zero||healthAge>maximumAge)return false;
        // A static healthy source needs no invented image timestamp. New images
        // waiting behind a stalled renderer must not keep remote input armed.
        var presentationAge=clock.GetElapsedTime(current.PresentedAt,clock.GetTimestamp());
        return current.LastImageSequence<=current.LastPresentedSequence||presentationAge>=TimeSpan.Zero&&presentationAge<=maximumAge;
    }
}
