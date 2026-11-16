using DesktopToolbox.DeskLink;
using Nian.Input.Security;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed partial class NativeIntegratedDeskSwitchService
{
    private static readonly TimeSpan PresenceInterval=TimeSpan.FromSeconds(2),PresenceDeadline=TimeSpan.FromSeconds(2),PresenceFreshness=TimeSpan.FromSeconds(3);
    private readonly object presenceGate=new();
    private PresenceRun? presence;
    private TimeProvider PresenceClock=>options.Clock??TimeProvider.System;
    private sealed record PeerObservation(long Timestamp,bool Available);
    private sealed class PresenceRun(CancellationTokenSource owner,DeviceIdentity identity)
    {
        internal readonly CancellationTokenSource Owner=owner,Stop=CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        internal readonly DeviceIdentity Identity=identity;
        internal TrustedEndpoint? Endpoint;
        internal long Epoch,LastAttempt;
        internal bool Attempted;
        internal PeerObservation? Observation;
        internal PresenceProbe? Pending;
        internal Task Loop=Task.CompletedTask;
    }
    private sealed class PresenceProbe(TrustedEndpoint endpoint,long epoch,CancellationToken token)
    {
        internal readonly TrustedEndpoint Endpoint=endpoint;
        internal readonly long Epoch=epoch;
        internal readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private bool IsPresenceCurrent(PresenceRun run)=>ReferenceEquals(presence,run)&&SameRun(run.Owner)&&!run.Stop.IsCancellationRequested&&
        Volatile.Read(ref stopping)==0&&(DeskLinkProcessState)Volatile.Read(ref processState)==DeskLinkProcessState.Running;
    private void StartPresence(CancellationTokenSource owner)
    {
        lock(presenceGate)
        {
            var run=new PresenceRun(owner,identity!){Endpoint=CurrentEndpoint()};presence=run;
            run.Loop=Task.Run(()=>PresenceLoopAsync(run));
        }
    }
    private async Task PresenceLoopAsync(PresenceRun run)
    {
        try
        {
            while(!run.Stop.IsCancellationRequested)
            {
                await RefreshPresenceAsync(run.Stop.Token).ConfigureAwait(false);
                TimeSpan delay;
                lock(presenceGate)
                {
                    var elapsed=PresenceClock.GetElapsedTime(run.LastAttempt,PresenceClock.GetTimestamp());
                    delay=!run.Attempted||elapsed<TimeSpan.Zero?PresenceInterval:elapsed<PresenceInterval?PresenceInterval-elapsed:TimeSpan.Zero;
                }
                await Task.Delay(delay,run.Stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(run.Stop.IsCancellationRequested){}
    }
    private void ResetPresence(CancellationTokenSource owner)
    {
        PresenceProbe? previous;
        lock(presenceGate)
        {
            if(presence is not{} run||!ReferenceEquals(run.Owner,owner)||!IsPresenceCurrent(run))return;
            previous=run.Pending;run.Pending=null;run.Epoch++;run.Endpoint=CurrentEndpoint();run.Observation=null;run.Attempted=false;
        }
        CancelPresenceProbe(previous);
        _=RefreshPresenceAsync(CancellationToken.None);
    }
    private PeerObservation? ReadPeerObservation(TrustedEndpoint? endpoint)
    {
        lock(presenceGate)
        {
            if(endpoint is null||presence is not{} run||!IsPresenceCurrent(run)||run.Endpoint!=endpoint||run.Observation is not{} observed)return null;
            var age=PresenceClock.GetElapsedTime(observed.Timestamp,PresenceClock.GetTimestamp());
            return age>=TimeSpan.Zero&&age<=PresenceFreshness?observed:null;
        }
    }
    private Task RefreshPresenceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();Task completion;
        lock(presenceGate)
        {
            if(presence is not{} run||!IsPresenceCurrent(run))return Task.CompletedTask;
            var endpoint=CurrentEndpoint();
            if(endpoint is null||endpoint!=run.Endpoint||!Allows(endpoint.Fingerprint,DevicePermission.Control))
            {run.Observation=null;run.Attempted=false;return Task.CompletedTask;}
            if(run.Pending is{} pending)completion=pending.Completion.Task;
            else
            {
                var now=PresenceClock.GetTimestamp();var elapsed=PresenceClock.GetElapsedTime(run.LastAttempt,now);
                if(run.Attempted&&elapsed>=TimeSpan.Zero&&elapsed<PresenceInterval)return Task.CompletedTask;
                run.Attempted=true;run.LastAttempt=now;
                var probe=new PresenceProbe(endpoint,run.Epoch,run.Stop.Token);probe.Cancel.CancelAfter(PresenceDeadline);run.Pending=probe;
                completion=probe.Completion.Task;
                _=Task.Run(()=>ProbePresenceAsync(run,probe));
            }
        }
        return completion.WaitAsync(token);
    }
    private async Task ProbePresenceAsync(PresenceRun run,PresenceProbe probe)
    {
        try
        {
            // The outer wait also bounds a local test seam that ignores cancellation or blocks before returning a Task.
            var read=Task.Run(()=>options.ReadPeerState is{} readState?readState(run.Identity,probe.Endpoint,probe.Cancel.Token):ReadPeerStateAsync(run.Identity,probe.Endpoint,probe.Cancel.Token));
            _=read.ContinueWith(failed=>{_ = failed.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            var state=await read.WaitAsync(probe.Cancel.Token).ConfigureAwait(false);
            probe.Cancel.Token.ThrowIfCancellationRequested();
            var names=settings;
            if(names is null||state.LocalName!=names.LocalName||state.RemoteName!=names.RemoteName)throw new InvalidDataException("The native peer state has different device names.");
            lock(presenceGate)
            {
                if(CanCommitPresence(run,probe))run.Observation=new(PresenceClock.GetTimestamp(),state.Available);
            }
        }
        catch
        {
            // Failure invalidates the previous success immediately; its remaining freshness never keeps a failed peer online.
            lock(presenceGate)if(CanCommitPresence(run,probe,allowCanceledProbe:true))run.Observation=null;
        }
        finally
        {
            lock(presenceGate)if(ReferenceEquals(run.Pending,probe))run.Pending=null;
            probe.Cancel.Dispose();probe.Completion.TrySetResult();Publish();
        }
    }
    private bool CanCommitPresence(PresenceRun run,PresenceProbe probe,bool allowCanceledProbe=false)=>IsPresenceCurrent(run)&&
        ReferenceEquals(run.Pending,probe)&&run.Epoch==probe.Epoch&&run.Endpoint==probe.Endpoint&&CurrentEndpoint()==probe.Endpoint&&
        Allows(probe.Endpoint.Fingerprint,DevicePermission.Control)&&(allowCanceledProbe||!probe.Cancel.IsCancellationRequested);
    private static async Task<DesktopControlState> ReadPeerStateAsync(DeviceIdentity identity,TrustedEndpoint endpoint,CancellationToken token)
    {
        await using var client=new DesktopInputClient(identity,endpoint);await client.ConnectAsync(token).ConfigureAwait(false);
        return await client.GetStateAsync(token).ConfigureAwait(false);
    }
    private static void CancelPresenceProbe(PresenceProbe? probe)
    {try{probe?.Cancel.Cancel();}catch(ObjectDisposedException){}}
    private void StopPresence()
    {
        PresenceRun? run;PresenceProbe? pending;
        lock(presenceGate){run=presence;if(run is null)return;run.Observation=null;pending=run.Pending;}
        try{run.Stop.Cancel();}catch(ObjectDisposedException){}CancelPresenceProbe(pending);
    }
    private async Task StopPresenceAsync()
    {
        StopPresence();PresenceRun? run;Task pending;
        lock(presenceGate){run=presence;if(run is null)return;pending=run.Pending?.Completion.Task??Task.CompletedTask;}
        await Task.WhenAll(run.Loop,pending).ConfigureAwait(false);
        lock(presenceGate)if(ReferenceEquals(presence,run))presence=null;
        run.Stop.Dispose();
    }
}
