using Nian.Input.Control;

namespace Nian.Input.Runtime;

/// <summary>Adapts a borrowed platform target without ever exposing ordinary keyboard injection.</summary>
internal sealed class PreviewOnlyTargetPolicy(IPreviewInputTarget target, PreviewOnlyDeviceOptions options,
    DesktopControlArbiter arbiter, Func<string, bool> allowed, Action changed) : IPreviewInputTarget
{
    private sealed record Activation(string Peer, long Consent, DesktopControlLease Lease);
    private Activation? active;
    public long ConsentVersion => target.ConsentVersion;
    public bool IsReady => Volatile.Read(ref active) is { } value && target.IsReady &&
        target.ConsentVersion == value.Consent && allowed(value.Peer);
    public string UnavailableReason => "The local preview safety or Deskflow focus is unavailable.";
    internal bool Prepare(string peer)
    {
        if (!allowed(peer) || arbiter.Current is not { Mode: DesktopControlMode.Preview } lease || lease.Peer != peer) return false;
        var before = target.ConsentVersion; var accepted = false;
        try { accepted = options.Prepare(peer); }
        finally
        {
            var after = target.ConsentVersion;
            if (after != before) Volatile.Write(ref active, new(peer, after, lease));
        }
        changed();
        return accepted && IsReady;
    }
    internal bool Renew(string peer, long consent) => Volatile.Read(ref active) is { } value &&
        value.Peer == peer && value.Consent == consent && allowed(peer) && target.IsReady &&
        target.ConsentVersion == consent && options.Renew(peer, consent) && target.IsReady && target.ConsentVersion == consent;
    public void Apply(InputPacket packet) => throw new InvalidOperationException("This receiver accepts preview gestures only.");
    public void ApplyPreview(ResolvedPreviewInput input, long expectedConsentVersion)
    {
        var value = Volatile.Read(ref active);
        if (value is null || value.Consent != expectedConsentVersion || !IsReady) throw new InvalidOperationException(UnavailableReason);
        target.ApplyPreview(input, expectedConsentVersion);
    }
    public void ReleaseAll() { if (Volatile.Read(ref active) is { } value) ReleaseAll(value.Consent); }
    public void ReleaseAll(long expectedConsentVersion)
    {
        var value = Volatile.Read(ref active);
        if (value is null || value.Consent != expectedConsentVersion || !ReferenceEquals(Interlocked.CompareExchange(ref active, null, value), value)) return;
        try { target.ReleaseAll(expectedConsentVersion); }
        finally { changed(); }
    }
    internal void Invalidate()
    {
        var value = Volatile.Read(ref active);
        if (value is null) return;
        // Frame invalidation must not stop a different, outbound preview lease.
        arbiter.Stop(value.Lease, "preview-frame-invalidated");
        ReleaseAll(value.Consent);
    }
}
