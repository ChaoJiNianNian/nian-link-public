using System.Diagnostics;
using System.IO;

namespace Nian.Input.Windows;

public sealed class InputSendPump : IAsyncDisposable
{
    private readonly object gate=new();
    private readonly LinkedList<InputPacket> pending=new();
    private readonly SemaphoreSlim available=new(0);
    private readonly CancellationTokenSource stop=new();
    private readonly Func<InputPacket,CancellationToken,Task<InputReply>> send;
    private readonly Func<CancellationToken,Task<InputReply>> heartbeat;
    private readonly Func<CancellationToken,Task<InputReply>> end;
    private readonly int capacity;
    private bool stopped,queueOverloaded;
    private Exception? failure;
    private int disposed;

    public InputSendPump(Func<InputPacket,CancellationToken,Task<InputReply>> send,
        Func<CancellationToken,Task<InputReply>> heartbeat,
        Func<CancellationToken,Task<InputReply>> end, int capacity=128)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity,1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity,4096);
        this.send=send;this.heartbeat=heartbeat;this.end=end;this.capacity=capacity;
        Completion=Task.Run(RunAsync);
    }
    public bool IsStopped {get{lock(gate)return stopped;}}
    public bool QueueOverloaded {get{lock(gate)return queueOverloaded;}}
    public Exception? Failure {get{lock(gate)return failure;}}
    public Task Completion {get;}

    public bool TryEnqueue(InputPacket packet)
    {
        lock(gate)
        {
            if(stopped)return false;
            // Coalesce only adjacent, still-pending motion. Buttons, keys and
            // wheels remain order barriers; an in-flight packet is immutable.
            if(pending.Last is { } tail && IsMotion(tail.Value) && IsMotion(packet))
            {
                var x=tail.Value.X+packet.X;var y=tail.Value.Y+packet.Y;
                if(Math.Abs(x)<=32767&&Math.Abs(y)<=32767)
                {
                    tail.Value=tail.Value with{X=x,Y=y};
                    return true; // No extra queue item/semaphore token.
                }
            }
            if(pending.Count<capacity){pending.AddLast(packet);available.Release();return true;}
            queueOverloaded=true;
        }
        Stop(new InvalidOperationException("Input queue full; session stopped to release held input."));
        return false;
    }

    private static bool IsMotion(InputPacket packet)=>packet.Kind==InputKind.MoveRelative&&
        packet.Code==0&&packet.Delta==0&&double.IsFinite(packet.X)&&double.IsFinite(packet.Y)&&
        Math.Abs(packet.X)<=32767&&Math.Abs(packet.Y)<=32767;

    public async Task StopAsync(){Stop(null);await Completion.ConfigureAwait(false);}
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if(Interlocked.Exchange(ref disposed,1)==0){stop.Dispose();available.Dispose();}
    }

    private void Stop(Exception? error)
    {
        lock(gate)
        {
            if(stopped)return;
            stopped=true;failure=error;pending.Clear();
        }
        stop.Cancel();
    }

    private async Task RunAsync()
    {
        long sequence=0;
        var heartbeatClock=Stopwatch.StartNew();
        try
        {
            while(!stop.IsCancellationRequested)
            {
                if(heartbeatClock.ElapsedMilliseconds>=250)
                {
                    RequireSuccess(await heartbeat(stop.Token).WaitAsync(stop.Token).ConfigureAwait(false));
                    heartbeatClock.Restart();
                }
                var wait=TimeSpan.FromMilliseconds(Math.Max(1,250-heartbeatClock.ElapsedMilliseconds));
                if(!await available.WaitAsync(wait,stop.Token).ConfigureAwait(false))continue;
                InputPacket? packet=null;
                lock(gate)
                {
                    if(!stopped&&pending.First is { } first)
                    {packet=first.Value;pending.RemoveFirst();}
                }
                if(packet is not null)
                    RequireSuccess(await send(packet with{Sequence=++sequence},stop.Token).WaitAsync(stop.Token).ConfigureAwait(false));
            }
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception error){Stop(error);}
        finally
        {
            Stop(null);
            using var releaseDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try{RequireSuccess(await end(releaseDeadline.Token).WaitAsync(releaseDeadline.Token).ConfigureAwait(false));}
            catch(Exception error){lock(gate)failure??=error;}
        }
    }

    private static void RequireSuccess(InputReply reply)
    {
        if(!reply.Ok)throw new IOException("The receiver rejected input; session stopped.");
    }
}
