namespace DesktopToolbox.Mac.Services.Display;
public sealed class MacNativeDisplayControl : IMacDisplayControl
{
    private readonly IMacNativeDisplayApi api;
    private readonly TimeSpan deadline;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    public MacNativeDisplayControl() : this(new IoKitMacDisplayApi()) { }
    internal MacNativeDisplayControl(IMacNativeDisplayApi api, TimeSpan? deadline = null) { this.api = api; this.deadline = deadline ?? TimeSpan.FromSeconds(3); }
    public Task<IReadOnlyList<MacDisplayDescriptor>> ListAsync(CancellationToken cancellationToken = default) => ExecuteAsync(_ => api.List(), cancellationToken);
    public async Task<MacDisplayReadResult> ReadInputAsync(MacDisplayIdentity identity, CancellationToken cancellationToken = default)
    {
        try { return await ExecuteAsync(token => Read(identity, token), cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false,null,null,null,"operation_timeout_or_stopped"); }
        catch (Exception e) when (IsNativeFailure(e)) { return new(false,null,null,null,e.Message); }
    }
    public async Task<MacDisplaySetResult> SetInputAsync(MacDisplayIdentity identity, ushort input, CancellationToken cancellationToken = default)
    {
        if (identity.VendorId is 0 or > ushort.MaxValue || identity.ProductId is 0 or > ushort.MaxValue) return new(false,null,"invalid_display_identity");
        if (input is 0 or > 255) return new(false,null,"input_out_of_range");
        try { return await ExecuteAsync(token => Set(identity,input,token), cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false,null,"operation_timeout_or_stopped"); }
        catch (Exception e) when (IsNativeFailure(e)) { return new(false,null,e.Message); }
    }
    private MacDisplayReadResult Read(MacDisplayIdentity identity, CancellationToken token)
    {
        var target = MacDisplaySelection.RequireUnique(api.List(),identity);
        token.ThrowIfCancellationRequested();
        using var channel = api.Open(target);
        int lastError=0;
        var attempts=new List<MacDisplayReadAttempt>();
        foreach(var variant in new[]{(Offset:0u,Standard:true),(Offset:0u,Standard:false),(Offset:0x51u,Standard:true),(Offset:0x51u,Standard:false)})
        {
            for(int cycle=0;cycle<2;cycle++) { Pause(10,token);lastError=channel.Write(MacDdcProtocol.CreateReadInput(variant.Standard));if(lastError!=0)break; }
            if(lastError!=0){attempts.Add(new(variant.Offset,variant.Standard,lastError,null,null));continue;}
            Pause(50,token);
            var reply=channel.Read(variant.Offset,11);lastError=reply.Error;
            attempts.Add(new(variant.Offset,variant.Standard,0,reply.Error,Convert.ToHexString(reply.Bytes.AsSpan(0,Math.Min(reply.Bytes.Length,11)))));
            token.ThrowIfCancellationRequested();
            if(lastError==0 && MacDdcProtocol.TryParseInput(reply.Bytes,out var current,out var maximum)) return new(true,current,maximum,0,null) { ReadVariant=$"{(variant.Standard?"standard":"m1ddc")}-offset-{variant.Offset:X2}", Attempts=attempts };
        }
        return new(false,null,null,lastError,lastError==0?"invalid_vcp_reply":"native_read_failed") { Attempts=attempts };
    }
    private MacDisplaySetResult Set(MacDisplayIdentity identity, ushort input, CancellationToken token)
    {
        var target=MacDisplaySelection.RequireUnique(api.List(),identity);
        token.ThrowIfCancellationRequested();
        using var channel=api.Open(target);
        bool accepted=false;
        for(int cycle=0;cycle<2;cycle++)
        {
            if(accepted && token.IsCancellationRequested) return new(true,0,"cancelled_after_acceptance");
            try { Pause(10,token); }
            catch(OperationCanceledException) when(accepted) { return new(true,0,"cancelled_after_acceptance"); }
            int error=channel.Write(MacDdcProtocol.CreateSetInput(input));
            if(error!=0) return new(accepted,error,"native_write_failed");
            accepted=true;
        }
        return new(accepted,0,null);
    }
    private async Task<T> ExecuteAsync<T>(Func<CancellationToken,T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
        using var timer=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
        timer.CancelAfter(deadline);
        await operation.WaitAsync(timer.Token).ConfigureAwait(false);
        try {
            // An entered IOKit call cannot be cancelled. Keep this task and the gate alive until it returns.
            // The caller may bound its UI wait, but must retain this in-flight task to prevent a late conflicting write.
            return await Task.Run(()=>{timer.Token.ThrowIfCancellationRequested();return action(timer.Token);},CancellationToken.None).ConfigureAwait(false);
        } finally { operation.Release(); }
    }
    private static bool IsNativeFailure(Exception e) => e is IOException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException;
    private static void Pause(int milliseconds,CancellationToken token) { if(token.WaitHandle.WaitOne(milliseconds))token.ThrowIfCancellationRequested();token.ThrowIfCancellationRequested(); }
    public void Dispose() { if(Interlocked.Exchange(ref disposed,1)==0){lifetime.Cancel();lifetime.Dispose();} }
}



