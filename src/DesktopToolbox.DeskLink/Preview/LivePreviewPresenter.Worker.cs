using System.Diagnostics;
using System.Threading.Channels;

namespace DesktopToolbox.DeskLink.Preview;

public sealed partial class LivePreviewPresenter<T>
{
    private async Task DecodeLoopAsync()
    {
        var token = lifetime.Token;
        try
        {
            while (true)
            {
                var next = await input.ReadAsync(token).ConfigureAwait(false);
                if (!Eligible(next.Epoch)) continue;
                T image;
                try
                {
                    image = await decode(next.Packet, token).ConfigureAwait(false);
                    if (image is null) throw new InvalidOperationException("The live preview decoder returned no image.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    if (Eligible(next.Epoch)) Queue(new(next.Epoch, next.Packet, default!, false, error));
                    continue;
                }
                Queue(new(next.Epoch, next.Packet, image, true));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
        catch (Exception error) { Trace.TraceError("Live preview decoder stopped ({0}).", error.GetType().Name); }
        finally { lifetime.Dispose(); }
    }

    private void Queue(Output next)
    {
        Output? replaced = null; var post = false; var accepted = false;
        lock (gate)
        {
            if (!disposed && acceptedEpoch == next.Epoch && Current(next.Epoch) && (next.Failure is not null || failedEpoch != next.Epoch))
            {
                if (next.Failure is not null) failedEpoch = next.Epoch;
                replaced = pending; pending = next; accepted = true;
                if (!uiScheduled) { uiScheduled = true; post = true; }
            }
        }
        Release(replaced);
        if (!accepted) Release(next);
        else if (post) PostUi();
    }

    private void PostUi()
    {
        lock (gate) if (disposed) { uiScheduled = false; return; }
        try { postToUi(RunUi); }
        catch (Exception error)
        {
            Output? lost;
            lock (gate) { lost = pending; pending = null; uiScheduled = false; if (lost is not null) failedEpoch = lost.Epoch; }
            Release(lost);
            // An unavailable dispatcher cannot safely run a window's failure callback either.
            Trace.TraceError("Live preview UI dispatch failed ({0}).", error.GetType().Name);
        }
    }

    private void RunUi()
    {
        Output? ready;
        lock (gate) { ready = pending; pending = null; }
        try
        {
            if (ready is null) return;
            if (!Eligible(ready.Epoch, ready.Failure is not null)) { Release(ready); return; }
            if (ready.Failure is { } failure)
            {
                try { failed?.Invoke(failure); }
                catch (Exception error) { Trace.TraceError("Live preview failure callback failed ({0}).", error.GetType().Name); }
                return;
            }
            try { present(ready.Epoch, ready.Packet, ready.Image); }
            catch (Exception error) { Release(ready); Queue(new(ready.Epoch, ready.Packet, default!, false, error)); }
        }
        finally
        {
            var again = false;
            lock (gate)
            {
                if (!disposed && pending is not null) again = true;
                else uiScheduled = false;
            }
            if (again) PostUi();
        }
    }
}
