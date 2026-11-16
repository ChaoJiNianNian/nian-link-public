namespace DesktopToolbox.DeskLink.Preview;

public sealed partial class DeskflowPreviewSession
{
    public Task StopAsync(string reason)
    {Run? current;lock(gate)current=run;return current is null?Task.CompletedTask:StopRunAsync(current,reason);}
    private Task StopRunAsync(Run current,string reason)
    {
        Task task;
        lock(gate)
        {
            if(!ReferenceEquals(run,current))return Task.CompletedTask;
            if(current.Cleanup is{IsCompleted:false} pending)return pending;
            current.Active=false;current.StopRequested=true;current.Recovery=false;
            status=current.UncertainOperation?UncertainReturnStatus():"正在收回键鼠，请稍候";current.Preparation.Cancel();
            task=current.Cleanup=ReturnOwnedAsync(current,reason);
        }
        Notify();return task;
    }
    private async Task ReturnOwnedAsync(Run current,string reason)
    {
        // Leave the caller's state lock before running callbacks or backend code.
        await Task.Yield();
        await current.CleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A concurrent confirmed-return path may already have retired this run.
            // Never issue an old End against a subsequently started operation.
            lock(gate)if(!ReferenceEquals(run,current))return;
            await current.BeginSettled.Task.ConfigureAwait(false);
            lock(gate)if(current.UncertainOperation)throw new IOException("键鼠操作结果尚未确认。");
            if(current.Beginning is{} beginning)
            {
                try
                {
                    var result=await beginning.ConfigureAwait(false);current.Id=result.Id;
                    current.ReturnConfirmed=result.Phase==DeskflowPreviewPhase.Viewing&&string.IsNullOrEmpty(result.Id);
                    if(!current.ReturnConfirmed&&!(result.Phase==DeskflowPreviewPhase.Active&&!string.IsNullOrEmpty(result.Id)))
                        throw new IOException("键鼠交接结果尚未确认。");
                }
                catch
                {
                    // A failed response does not cancel a request already received by the server.
                    lock(gate)current.UncertainOperation=true;
                    throw;
                }
            }
            if(!current.ReturnConfirmed&&current.Id is{} ownedId)
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                DeskflowPreviewState state;
                try{state=await backend.EndPreviewAsync(ownedId,deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);}
                catch{lock(gate)current.UncertainOperation=true;throw;}
                // An active replacement is not evidence that keyboard input returned.
                var released=state.Phase==DeskflowPreviewPhase.Viewing&&string.IsNullOrEmpty(state.Id);
                if(!released){lock(gate)current.UncertainOperation=true;throw new IOException("键鼠归还尚未确认。");}
                current.ReturnConfirmed=true;
            }
            else if(current.Beginning is null)current.ReturnConfirmed=true;
            await RestoreCaptureAsync(current).ConfigureAwait(false);
            lock(gate)
            {
                if(!ReferenceEquals(run,current))return;
                if(!current.ReturnConfirmed)throw new IOException("键鼠归还尚未确认。");
                run=null;status=reason;
            }
        }
        catch
        {
            try{await RestoreCaptureAsync(current).ConfigureAwait(false);}catch{}
            lock(gate)if(ReferenceEquals(run,current))
            {current.Recovery=true;status=current.UncertainOperation
                ? UncertainReturnStatus()
                : "尚未确认键鼠归还，请按 "+ReturnShortcut+"，或重试归还";}
        }
        finally{current.CleanupGate.Release();Notify();}
    }
    private string UncertainReturnStatus()=>"尚未确认键鼠归还，请检查键鼠所在设备（"+ReturnShortcut+"）；此窗口暂不能再次开始操作";
    private async Task FinishConfirmedAsync(Run current,string message)
    {
        await current.CleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
        lock(gate)
        {
            if(!ReferenceEquals(run,current)||current.UncertainOperation)return;
            current.Active=false;current.StopRequested=true;current.ReturnConfirmed=true;current.Preparation.Cancel();
        }
        try
        {
            await RestoreCaptureAsync(current).ConfigureAwait(false);
            lock(gate)if(ReferenceEquals(run,current)){run=null;status=message;}
        }
        catch{lock(gate)if(ReferenceEquals(run,current)){current.Recovery=true;status="键鼠已返回，画面档位恢复失败，请重试";}}
        Notify();
        }
        finally{current.CleanupGate.Release();}
    }
    private async Task RestoreCaptureAsync(Run current)
    {
        if(!current.CaptureRequested||current.CaptureRestored)return;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        if(liveSource is{} source)await source.StopAsync(current.CaptureEpoch,deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        else await temporaryCapture(false,deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        current.CaptureRestored=true;
    }
    public async ValueTask DisposeAsync()
    {
        lock(gate){if(disposed)return;disposed=true;}
        await timer.DisposeAsync().ConfigureAwait(false);
        await StopAsync("预览已关闭，键鼠已归还").ConfigureAwait(false);
        if(liveSource is{} source)
        {
            source.PacketReceived-=ObserveLivePacket;source.StreamClosed-=ObserveLiveClosed;
            await source.DisposeAsync().ConfigureAwait(false);
        }
        Notify();
    }
}
