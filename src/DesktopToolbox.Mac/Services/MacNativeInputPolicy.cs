using Nian.Input;
using Nian.Input.Control;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

/// <summary>Local persistent permission plus a fresh native consent generation. No network request can grant permission here.</summary>
internal sealed class MacNativeInputPolicy(IMacNativeDesktopHost host, Func<string, DevicePermission, bool> trusted) : IPreviewInputTarget
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);
    private readonly object gate = new();
    private string? peer;
    private DevicePermission allowed;
    private bool running, prepared;
    private long consent, revision;
    private DesktopControlMode mode;

    public void Configure(string? fingerprint, DevicePermission permissions)
    {
        lock (gate) { ReleaseCore(); peer = fingerprint; allowed = permissions; }
    }
    public void SetRunning(bool value)
    {
        lock (gate) { running = value; if (!value) { ReleaseCore(); host.SetArmed(false, null); } }
    }
    public bool Prepare(string fingerprint, DesktopControlMode requestedMode)
    {
        lock (gate)
        {
            if (!Authorized(fingerprint, requestedMode)) return false;
            host.RefreshSafety(); host.SetArmed(true, Lease);
            if (!host.IsGuardHealthy || !host.Target.IsReady) { host.SetArmed(false, null); return false; }
            consent = host.Target.ConsentVersion; mode = requestedMode; prepared = true; revision++; return true;
        }
    }
    public bool Renew(string fingerprint, DesktopControlMode requestedMode, long expected)
    {
        lock (gate)
        {
            if (!prepared || consent != expected || mode != requestedMode || !Authorized(fingerprint, requestedMode) ||
                host.Target.ConsentVersion != expected || !host.Target.IsReady || !host.RenewLease(expected, Lease))
            { ReleaseCore(); return false; }
            return true;
        }
    }
    public (bool Active, long Revision) ReadActivity()
    {
        lock (gate)
        {
            if (prepared && (!Authorized(peer!, mode) || host.Target.ConsentVersion != consent || !host.Target.IsReady)) ReleaseCore();
            return (prepared, revision);
        }
    }
    private bool Authorized(string fingerprint, DesktopControlMode requestedMode)
    {
        var permission = requestedMode == DesktopControlMode.Preview ? DevicePermission.PreviewControl : DevicePermission.Control;
        return running && host.IsGuardHealthy && string.Equals(peer, fingerprint, StringComparison.OrdinalIgnoreCase) &&
            (allowed & permission) == permission && trusted(fingerprint, permission);
    }
    public long ConsentVersion => host.Target.ConsentVersion;
    public bool IsReady { get { lock (gate) return prepared && Authorized(peer!, mode) && host.Target.ConsentVersion == consent && host.Target.IsReady; } }
    public string UnavailableReason => host.Target.UnavailableReason;
    public void Apply(InputPacket packet) => Apply(packet, ConsentVersion);
    public void Apply(InputPacket packet, long expected)
    {
        lock (gate) { RequireCurrent(expected); host.Target.Apply(packet, expected); }
    }
    public void ApplyPreview(ResolvedPreviewInput input, long expected)
    {
        lock (gate) { RequireCurrent(expected); host.Target.ApplyPreview(input, expected); }
    }
    private void RequireCurrent(long expected)
    {
        if (!prepared || consent != expected || !Authorized(peer!, mode) || host.Target.ConsentVersion != expected)
            throw new InvalidOperationException("Mac 本机控制允许已变化。");
    }
    public void ReleaseAll() { lock (gate) ReleaseCore(); }
    public void ReleaseAll(long expected) { lock (gate) if (prepared && consent == expected) ReleaseCore(); }
    public void InvalidatePreview() { lock (gate) if (prepared && mode == DesktopControlMode.Preview) ReleaseCore(); }
    private void ReleaseCore()
    {
        if (!prepared) return;
        var expected = consent; prepared = false; revision++;
        // The native target performs this comparison atomically with disarming.
        host.Target.ReleaseAll(expected);
        if (host.Target.ConsentVersion == expected) host.SetArmed(false, null);
    }
}
