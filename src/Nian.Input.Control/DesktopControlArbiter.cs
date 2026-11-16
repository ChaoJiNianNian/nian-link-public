namespace Nian.Input.Control;

public sealed record DesktopControlLease(long Generation,string Peer,DesktopControlMode Mode);

/// <summary>One local input owner. Stop callbacks must synchronously stop capture/release held input, never await network or change display inputs.</summary>
public sealed class DesktopControlArbiter : IDisposable
{
    private sealed record Active(DesktopControlLease Lease,Action<string> Stop,long RenewedAt,TimeSpan Duration);
    private readonly object gate=new();
    private readonly TimeProvider clock;
    private readonly ITimer watchdog;
    private Active? active;
    private long generation,lastTimestamp;
    private bool hasTimestamp,disposed;

    public DesktopControlArbiter(TimeProvider? clock=null)
    {
        this.clock=clock??TimeProvider.System;
        watchdog=this.clock.CreateTimer(_=>CheckSafety(),null,TimeSpan.FromMilliseconds(250),TimeSpan.FromMilliseconds(250));
    }
    public DesktopControlLease? Current {get{lock(gate){CheckCore();return active?.Lease;}}}

    public DesktopControlLease Enter(string peer,DesktopControlMode mode,Action<string> stop,TimeSpan? lease=null,bool replaceSamePeer=false)
    {
        if(peer is not{Length:64}||!peer.All(Uri.IsHexDigit))throw new ArgumentException("A pinned device fingerprint is required.",nameof(peer));
        if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
        ArgumentNullException.ThrowIfNull(stop);var duration=ValidateDuration(lease??TimeSpan.FromSeconds(2));
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);CheckCore();
            if(active is{} previous)
            {
                if(!replaceSamePeer||!string.Equals(previous.Lease.Peer,peer,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Another control session is active.");
                StopCore("mode-changed");
            }
            var token=new DesktopControlLease(checked(++generation),peer.ToUpperInvariant(),mode);
            active=new(token,stop,ReadTimestamp(),duration);return token;
        }
    }
    public bool Execute(DesktopControlLease lease,Action input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock(gate)
        {
            CheckCore();if(!Matches(lease))return false;
            try{input();return true;}
            catch{StopCore("input-failed");throw;}
        }
    }
    public bool Renew(DesktopControlLease lease,TimeSpan duration)
    {
        ValidateDuration(duration);
        lock(gate){CheckCore();if(!Matches(lease))return false;active=active! with{RenewedAt=ReadTimestamp(),Duration=duration};return true;}
    }
    public bool Stop(DesktopControlLease lease,string reason)
    {
        lock(gate){if(!Matches(lease))return false;StopCore(reason);return true;}
    }
    public void StopAll(string reason){lock(gate)StopCore(reason);}
    public void CheckSafety()
    {
        // A native release failure must not escape a timer callback. The session is already cleared.
        lock(gate){try{CheckCore();}catch{}}
    }
    private bool Matches(DesktopControlLease? lease)=>!disposed&&lease is not null&&active?.Lease==lease;
    private void CheckCore()
    {
        if(disposed)return;
        var now=clock.GetTimestamp();
        if(hasTimestamp&&now<lastTimestamp){lastTimestamp=now;StopCore("clock-changed");return;}
        hasTimestamp=true;lastTimestamp=now;
        if(active is{} current&&clock.GetElapsedTime(current.RenewedAt,now)>=current.Duration)StopCore("lease-expired");
    }
    private long ReadTimestamp(){var now=clock.GetTimestamp();hasTimestamp=true;lastTimestamp=now;return now;}
    private void StopCore(string reason)
    {
        var previous=active;active=null;
        previous?.Stop(reason);
    }
    private static TimeSpan ValidateDuration(TimeSpan duration)=>duration>TimeSpan.Zero&&duration<=TimeSpan.FromMinutes(5)?duration:throw new ArgumentOutOfRangeException(nameof(duration));
    public void Dispose()
    {
        lock(gate){if(disposed)return;disposed=true;try{StopCore("disposed");}finally{watchdog.Dispose();}}
    }
}
