using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Preview;
using DesktopToolbox.Windows.DeskSwitch;

namespace DesktopToolbox.App.DeskLink;

public sealed partial class WindowsCollaborationHost
{
    private readonly List<WeakReference<DeskflowPreviewSession>> handoffSessions = [];
    private IDeskflowPreviewControl? DeskflowHandoff => keyboard as IDeskflowPreviewControl;
    public bool PreviewInputOwnsForeground => keyboard is IDeskflowPreviewForegroundState foreground &&
        foreground.PreviewInputOwnsForeground;

    public DeskflowPreviewSession? CreateDeskflowPreviewSession()
        => CreateDeskflowPreviewSessionCore(legacyFixture: false);

    // Kept only for synthetic regression fixtures that exercise the historical
    // one-second cadence. Production never falls back to that mode.
    private DeskflowPreviewSession? CreateLegacyDeskflowPreviewSessionForTests()
        => CreateDeskflowPreviewSessionCore(legacyFixture: true);

    private DeskflowPreviewSession? CreateDeskflowPreviewSessionCore(bool legacyFixture)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (DeskflowHandoff is not { } backend) return null;
        DeskflowPreviewSession session;
        if (legacyFixture)
        {
            var lease = new object();
            session = new DeskflowPreviewSession(backend, (enabled, token) => SetTemporaryPreviewCaptureAsync(lease, enabled, token), controlClock);
        }
        else session = new DeskflowPreviewSession(backend, new RemoteUploadControlLivePreviewSource(
            () => server, () => Volatile.Read(ref liveCaptureAllowed) != 0 && previewVisible && Snapshot.Connected&&passivePeerSupported&&server?.RemotePreviewSourceStatus?.Availability=="enabled"), controlClock);
        if(!legacyFixture)
        {
            var operationWasStarted=false;var phaseGate=new object();
            session.Changed+=() =>
            {
                var phase=session.Phase;var rotate=false;
                lock(phaseGate)
                {
                    if(phase is Nian.Input.Control.PreviewControlPhase.Preparing or Nian.Input.Control.PreviewControlPhase.Controlling)operationWasStarted=true;
                    else if(phase==Nian.Input.Control.PreviewControlPhase.Viewing&&operationWasStarted){operationWasStarted=false;rotate=true;}
                }
                if(rotate)RotatePassivePreviewRequest();
            };
        }
        lock (previewSessionsGate)
        {
            handoffSessions.RemoveAll(item => !item.TryGetTarget(out _));
            handoffSessions.Add(new(session));
        }
        return session;
    }

    private DeskflowPreviewSession[] HandoffSessions()
    {
        lock (previewSessionsGate)
        {
            handoffSessions.RemoveAll(item => !item.TryGetTarget(out _));
            return handoffSessions.Select(item => item.TryGetTarget(out var session) ? session : null)
                .OfType<DeskflowPreviewSession>().ToArray();
        }
    }

    private bool HasProtectedLiveOperation => HandoffSessions().Any(session => session.HasProtectedLiveOperation);

    private void StopHandoffSessions(string reason, bool activeOnly, bool preserveLive)
    {
        foreach (var session in HandoffSessions())
            if ((!preserveLive || !session.HasProtectedLiveOperation) && (!activeOnly || session.IsActive)) _ = session.StopAsync(reason);
    }

    private async Task DisposeHandoffSessionsAsync()
    {
        foreach (var session in HandoffSessions()) await session.DisposeAsync();
        lock (previewSessionsGate) handoffSessions.Clear();
    }
}
