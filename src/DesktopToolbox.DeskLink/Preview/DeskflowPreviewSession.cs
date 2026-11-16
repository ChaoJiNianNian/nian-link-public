using DesktopToolbox.DeskLink.Collaboration;
using Nian.Input.Control;

namespace DesktopToolbox.DeskLink.Preview;

/// <summary>One viewer's Deskflow handoff. It never injects coordinates or owns the shared backend.</summary>
public sealed partial class DeskflowPreviewSession : IAsyncDisposable
{
    private sealed partial class Run
    {
        internal readonly CancellationTokenSource Preparation=new();
        internal readonly TaskCompletionSource FrameReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource BeginSettled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<DeskflowPreviewState>? Beginning;
        internal Task? Cleanup;
        internal readonly SemaphoreSlim CleanupGate=new(1,1);
        internal string? Id;
        internal bool Active,StopRequested,Recovery,CaptureRequested,CaptureRestored,ReturnConfirmed,UncertainOperation;
    }
    private readonly object gate=new();
    private readonly IDeskflowPreviewControl backend;
    private readonly Func<bool,CancellationToken,Task> temporaryCapture;
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private Run? run;
    private bool disposed,live;
    private DateTimeOffset capturedAt;
    private long observedAt;
    private int checking;
    private string status="查看画面，开始后只交接键鼠";
    private string? startHint;
    public DeskflowPreviewSession(IDeskflowPreviewControl backend,Func<bool,CancellationToken,Task> temporaryCapture,TimeProvider? clock=null)
    {
        ArgumentNullException.ThrowIfNull(backend);ArgumentNullException.ThrowIfNull(temporaryCapture);
        this.backend=backend;this.temporaryCapture=temporaryCapture;this.clock=clock??TimeProvider.System;
        timer=this.clock.CreateTimer(_=>_=CheckSafetyAsync(),null,TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(2));
    }
    public event Action? Changed;
    public PreviewControlPhase Phase {get{lock(gate)return run is{} current?current.StopRequested?PreviewControlPhase.Stopping:current.Active?PreviewControlPhase.Controlling:PreviewControlPhase.Preparing:disposed?PreviewControlPhase.Closed:PreviewControlPhase.Viewing;}}
    public bool CanStart {get{lock(gate)return !disposed&&run is null&&Supported;}}
    public bool CanStop {get{lock(gate)return !disposed&&run is{} current&&(!current.StopRequested||current.Recovery);}}
    public bool IsActive {get{lock(gate)return run?.Active==true;}}
    public string Status {get{lock(gate)return status;}}
    internal string? StartHint {get{lock(gate)return startHint;}}
    public string ReturnShortcut=>backend.PreviewReturnShortcut;
    public bool Supported=>backend.PreviewHandoffSupported&&(liveSource?.IsSupported??true);
    public void ObserveFrame(PreviewFrame? frame)
    {
        if(liveSource is not null)return;
        Run? stop=null;
        lock(gate)
        {
            if(disposed)return;
            var usable=frame is{State:"live",Width:>0,Height:>0,JpegBytes.Length:>0}&&frame.CapturedAtUtc!=default;
            if(!usable){live=false;if(run?.Active==true)stop=run;}
            else if(!live||frame!.CapturedAtUtc>capturedAt)
            {
                live=true;capturedAt=frame!.CapturedAtUtc;observedAt=clock.GetTimestamp();
                if(Fresh(TimeSpan.FromSeconds(2)))run?.FrameReady.TrySetResult();
            }
        }
        if(stop is not null)_=StopRunAsync(stop,"画面已断开，正在收回键鼠");
    }
    public async Task EnableAsync(CancellationToken token=default)
    {
        Run current;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!backend.PreviewHandoffSupported)throw new NotSupportedException(backend.PreviewHandoffHint);
            if(liveSource?.IsSupported==false)throw new NotSupportedException(liveSource.UnavailableHint);
            if(run is not null)throw new InvalidOperationException("请先确认键鼠已经归还。");
            current=new();run=current;startHint=null;liveSourceFailed=false;status="正在获取新画面，准备交接键鼠…";
        }
        Notify();
        using var preparation=CancellationTokenSource.CreateLinkedTokenSource(token,current.Preparation.Token);
        preparation.CancelAfter(TimeSpan.FromSeconds(6));
        Exception? failure=null;
        string? declinedHint=null;
        try
        {
            current.CaptureRequested=true;
            if(liveSource is{} source)await source.StartAsync(current.CaptureEpoch,preparation.Token).WaitAsync(preparation.Token).ConfigureAwait(false);
            else await temporaryCapture(true,preparation.Token).WaitAsync(preparation.Token).ConfigureAwait(false);
            lock(gate)if(Fresh(TimeSpan.FromSeconds(2)))current.FrameReady.TrySetResult();
            await current.FrameReady.Task.WaitAsync(preparation.Token).ConfigureAwait(false);
            lock(gate)if(!Fresh(TimeSpan.FromSeconds(2)))throw new InvalidOperationException("请等待新的桌面画面后再交接键鼠。");
            // Reserve the in-flight operation under the same lock as Stop; invoke
            // the backend only after leaving the lock so it cannot re-enter here.
            var beginning=new TaskCompletionSource<DeskflowPreviewState>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(gate)
            {
                preparation.Token.ThrowIfCancellationRequested();
                if(!ReferenceEquals(run,current)||current.StopRequested)throw new OperationCanceledException();
                current.Beginning=beginning.Task;
            }
            _=CompleteBeginAsync(beginning);
            // User cancellation may stop waiting, but must not discard a late owned ID.
            var result=await beginning.Task.WaitAsync(preparation.Token).ConfigureAwait(false);
            lock(gate)
            {
                current.Id=result.Id;
                preparation.Token.ThrowIfCancellationRequested();
                if(!ReferenceEquals(run,current)||current.StopRequested)throw new OperationCanceledException();
                if(result.Phase==DeskflowPreviewPhase.Viewing&&string.IsNullOrEmpty(result.Id)&&!string.IsNullOrWhiteSpace(result.Message))
                {
                    // A confirmed no-handoff reply can explain how to return the physical focus.
                    // Never expose arbitrary exception text or turn an unconfirmed return into Viewing.
                    var message=result.Message.Trim();declinedHint=message[..Math.Min(message.Length,240)];startHint=declinedHint;
                }
                if(result.Phase!=DeskflowPreviewPhase.Active||string.IsNullOrEmpty(result.Id))throw new InvalidOperationException("键鼠交接尚未确认。");
                current.Active=true;status="键鼠已交接 · "+ReturnShortcut+" 归还";
            }
        }
        catch(Exception error){failure=error;}
        finally{current.BeginSettled.TrySetResult();}
        if(failure is not null)
        {
            if(liveSource is not null&&(failure is not OperationCanceledException||!token.IsCancellationRequested&&!current.Preparation.IsCancellationRequested))
                lock(gate)if(ReferenceEquals(run,current))liveSourceFailed=true;
            var cleanup=StopRunAsync(current,token.IsCancellationRequested||current.Preparation.IsCancellationRequested?"已取消，正在收回键鼠":declinedHint??"未能交接键鼠，请检查共享与画面连接");
            if(current.Beginning?.IsCompleted!=false)await cleanup.ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        Notify();
    }
    private async Task<DeskflowPreviewState> BeginBoundedAsync()
    {using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));return await backend.BeginPreviewAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);}
    private async Task CompleteBeginAsync(TaskCompletionSource<DeskflowPreviewState> completion)
    {
        try{completion.TrySetResult(await BeginBoundedAsync().ConfigureAwait(false));}
        catch(Exception error){completion.TrySetException(error);}
    }
    internal async Task CheckSafetyAsync()
    {
        if(Interlocked.Exchange(ref checking,1)!=0)return;
        Run? observedRun=null;
        try
        {
            Run? current;bool stale;
            lock(gate){current=observedRun=run;stale=current?.Active==true&&!Fresh(TimeSpan.FromSeconds(8));
                // An unacknowledged Begin/End may still execute after any later Read.
                if(current?.UncertainOperation==true)return;}
            if(current is null)return;
            if(stale){await StopRunAfterLiveFailureAsync(current,"画面超过 8 秒未更新，正在收回键鼠").ConfigureAwait(false);return;}
            if(current.StopRequested&&!current.Recovery||!current.Active&&!current.Recovery)return;
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var state=current.Recovery?await backend.ReadPreviewAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false):
                await backend.RenewPreviewAsync(current.Id!,deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if(state.Phase==DeskflowPreviewPhase.Viewing&&string.IsNullOrEmpty(state.Id)||
                state.Phase==DeskflowPreviewPhase.Active&&!string.IsNullOrEmpty(current.Id)&&!string.IsNullOrEmpty(state.Id)&&state.Id!=current.Id)
            {await FinishConfirmedAsync(current,state.Phase==DeskflowPreviewPhase.Viewing?"键鼠已返回，当前仅观看":"其他窗口已接管键鼠，当前仅观看").ConfigureAwait(false);return;}
            if(state.Phase!=DeskflowPreviewPhase.Active&&current.Active)
                await StopRunAsync(current,"键鼠正在归还，请稍候").ConfigureAwait(false);
        }
        catch{if(observedRun is not null)await StopRunAfterLiveFailureAsync(observedRun,"连接暂不可用，正在收回键鼠").ConfigureAwait(false);}
        finally{Volatile.Write(ref checking,0);}
    }
    private bool Fresh(TimeSpan maximumAge)
    {
        if(liveSource is not null)return run is{} current&&LiveFresh(current,maximumAge);
        if(!live)return false;
        var localAge=clock.GetElapsedTime(observedAt,clock.GetTimestamp());var sourceAge=clock.GetUtcNow()-capturedAt;
        return localAge>=TimeSpan.Zero&&localAge<=maximumAge&&sourceAge>=TimeSpan.FromSeconds(-2)&&sourceAge<=maximumAge;
    }
    private void Notify(){if(Changed is{} callbacks)foreach(Action callback in callbacks.GetInvocationList())try{callback();}catch{}}
}
