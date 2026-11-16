namespace Nian.Input.Control;

public interface IPreviewControlChannel
{
    Task BeginAsync(CancellationToken token);
    Task SendAsync(PreviewInputCommand command,CancellationToken token);
    Task HeartbeatAsync(CancellationToken token);
    Task EndAsync(CancellationToken token);
}

/// <summary>Viewer lifecycle. Only a frame successfully displayed by the native viewer may be observed here.</summary>
public sealed class PreviewControlSession : IAsyncDisposable
{
    private sealed class Run
    {
        internal readonly CancellationTokenSource Cancel=new();
        internal readonly TaskCompletionSource FrameReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Active,StopRequested,CaptureRequested;
        internal Task? Cleanup;
        internal long Sequence;
    }
    private readonly object gate=new();
    private readonly SemaphoreSlim lifecycle=new(1,1),sending=new(1,1);
    private readonly IPreviewControlChannel channel;
    private readonly Func<bool,CancellationToken,Task> temporaryCapture;
    private readonly TimeProvider clock;
    private PreviewFrameProof? displayed;
    private long observedAt,lastTimestamp;
    private bool hasTimestamp,disposed;
    private Run? run;
    private int pending;
    private string status="鼠标控制已关闭";

    public PreviewControlSession(IPreviewControlChannel channel,Func<bool,CancellationToken,Task> temporaryCapture,TimeProvider? clock=null)
    {this.channel=channel;this.temporaryCapture=temporaryCapture;this.clock=clock??TimeProvider.System;}
    public event Action? Changed;
    public bool IsActive {get{lock(gate)return run?.Active==true;}}
    public bool IsEnabling {get{lock(gate)return run is{Active:false,StopRequested:false};}}
    public PreviewControlPhase Phase {get{lock(gate)return disposed?PreviewControlPhase.Closed:run is null?PreviewControlPhase.Viewing:
        run.StopRequested?PreviewControlPhase.Stopping:run.Active?PreviewControlPhase.Controlling:PreviewControlPhase.Preparing;}}
    public bool CanStart {get{lock(gate)return !disposed&&run is null;}}
    public bool CanStop {get{lock(gate)return !disposed&&run is{StopRequested:false};}}
    public string Status {get{lock(gate)return status;}}

    public void ObserveDisplayedFrame(PreviewFrameProof? proof,byte[] bytes,int width,int height)
    {
        bool stop=false;Run? observedRun;
        lock(gate)
        {
            if(disposed)return;
            observedRun=run;
            if(!PreviewFrameAuthority.ContentMatches(proof,bytes,width,height))
            {displayed=null;stop=run?.Active==true;}
            else
            {
                var now=ReadTimestamp();
                if(displayed is{} old&&!old.Geometry.SameSource(proof!.Geometry))stop=run is not null;
                // Repeated mailbox/UI refreshes of one captured frame cannot extend its control lifetime.
                if(displayed?.Id!=proof!.Id){displayed=proof;observedAt=now;}
                if(FreshCore())run?.FrameReady.TrySetResult();
            }
        }
        if(stop&&observedRun is not null)_=StopRunAsync(observedRun,"画面来源已变化，请重新开启控制");
    }
    public async Task EnableAsync(CancellationToken token=default)
    {
        Run current;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(run is not null)throw new InvalidOperationException("请等待上一次控制结束。");
            current=new();run=current;status="正在等待可操作的新画面…";
            if(FreshCore())current.FrameReady.TrySetResult();
        }
        Notify();
        using var start=CancellationTokenSource.CreateLinkedTokenSource(token,current.Cancel.Token);
        start.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await lifecycle.WaitAsync(start.Token).ConfigureAwait(false);
            try
            {
                current.CaptureRequested=true;
                await temporaryCapture(true,start.Token).ConfigureAwait(false);
                await current.FrameReady.Task.WaitAsync(start.Token).ConfigureAwait(false);
                lock(gate)if(!FreshCore())throw new InvalidOperationException("请等待新画面后再开启控制。");
                await channel.BeginAsync(start.Token).WaitAsync(start.Token).ConfigureAwait(false);
                lock(gate)
                {
                    start.Token.ThrowIfCancellationRequested();
                    if(!ReferenceEquals(run,current)||current.StopRequested)throw new OperationCanceledException();
                    current.Active=true;status="鼠标控制已开启 · Esc 退出";
                }
                _=WatchAsync(current);
            }
            finally{lifecycle.Release();}
            Notify();
        }
        catch{await StopRunAsync(current,"未能开启鼠标控制，请检查配对与画面权限").ConfigureAwait(false);throw;}
    }
    public async Task<bool> SendAsync(PreviewInputKind kind,double u=0,double v=0,int code=0,int delta=0,CancellationToken token=default)
    {
        Run? current;
        lock(gate){current=run;if(current?.Active!=true)return false;pending++;}
        try
        {
            if(Volatile.Read(ref pending)>32){await StopRunAsync(current,"操作积压，控制已停止").ConfigureAwait(false);return false;}
            using var request=CancellationTokenSource.CreateLinkedTokenSource(token,current.Cancel.Token);
            await sending.WaitAsync(request.Token).ConfigureAwait(false);
            try
            {
                PreviewInputCommand command;
                lock(gate)
                {
                    if(!ReferenceEquals(run,current)||!current.Active||!FreshCore())throw new InvalidOperationException("画面已过期。");
                    command=new(++current.Sequence,displayed!.Id,displayed.ContentSha256,kind,u,v,code,delta);
                    if(!PreviewInputValidation.Command(command))throw new ArgumentException("操作无效。");
                }
                await channel.SendAsync(command,request.Token).WaitAsync(request.Token).ConfigureAwait(false);return true;
            }
            finally{sending.Release();}
        }
        catch{if(current is not null)await StopRunAsync(current,"控制已停止，请等待新画面或重新连接").ConfigureAwait(false);return false;}
        finally{Interlocked.Decrement(ref pending);}
    }
    public Task StopAsync(string reason)
    {
        Run? current;lock(gate)current=run;
        return current is null?Task.CompletedTask:StopRunAsync(current,reason);
    }
    private Task StopRunAsync(Run expected,string reason)
    {
        Task result;
        lock(gate)
        {
            if(run is not{} current||!ReferenceEquals(current,expected))return Task.CompletedTask;
            if(current.Cleanup is{} existing)return existing;
            current.StopRequested=true;current.Active=false;status=reason;current.Cancel.Cancel();
            current.Cleanup=CleanupAsync(current);result=current.Cleanup;
        }
        Notify();return result;
    }
    private async Task CleanupAsync(Run current)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try{await channel.EndAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);}catch{}
            if(current.CaptureRequested)
            {
                try{await temporaryCapture(false,CancellationToken.None).ConfigureAwait(false);}catch{lock(gate)status="控制已停止，刷新档位恢复失败";}
            }
        }
        finally
        {
            lock(gate)if(ReferenceEquals(run,current))run=null;
            current.Cancel.Dispose();lifecycle.Release();Notify();
        }
    }
    private async Task WatchAsync(Run current)
    {
        var token=current.Cancel.Token;
        try
        {
            while(true)
            {
                await Task.Delay(250,token).ConfigureAwait(false);
                lock(gate)if(!ReferenceEquals(run,current)||!current.Active||!FreshCore())throw new InvalidOperationException("画面已过期。");
                await channel.HeartbeatAsync(token).WaitAsync(token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch{await StopRunAsync(current,"画面过期或连接中断，控制已停止").ConfigureAwait(false);}
    }
    private bool FreshCore()
    {
        var now=ReadTimestamp();
        if(displayed is null)return false;
        var age=clock.GetElapsedTime(observedAt,now);return age>=TimeSpan.Zero&&age<=TimeSpan.FromSeconds(2);
    }
    private long ReadTimestamp()
    {
        var now=clock.GetTimestamp();if(hasTimestamp&&now<lastTimestamp)displayed=null;
        hasTimestamp=true;lastTimestamp=now;return now;
    }
    private void Notify(){try{Changed?.Invoke();}catch{}}
    public async ValueTask DisposeAsync()
    {
        lock(gate){if(disposed)return;disposed=true;}
        await StopAsync("窗口已关闭").ConfigureAwait(false);
        if(channel is IAsyncDisposable owned)await owned.DisposeAsync().ConfigureAwait(false);
    }
}
