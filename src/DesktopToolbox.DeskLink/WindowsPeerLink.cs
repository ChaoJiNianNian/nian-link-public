namespace DesktopToolbox.DeskLink;

/// <summary>
/// Isolated Windows or Mac peer combining a Deskflow client with a
/// certificate-pinned control client. Not wired to the production app.
/// </summary>
public class MultiPeerClientLink : IAsyncDisposable, IDeskflowPreviewControl
{
    private readonly DeskflowSession keyboard;
    private readonly MultiPeerControlClient control;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public bool OwnsLiveProcess(int processId) => !disposed && keyboard.OwnsLiveProcess(processId);
    public bool PreviewHandoffSupported => !disposed;
    public string PreviewReturnShortcut => "请使用归还按钮";
    public string PreviewHandoffHint => "连接键鼠后，可在放大预览中操作";

    public MultiPeerClientLink(string profileDirectory, string enginePath, MultiPeerInvitation invitation,
        bool userApproved, DeskflowSessionDependencies? dependencies = null)
    {
        var options = MultiPeerClientProfile.Create(profileDirectory, enginePath, invitation, userApproved);
        keyboard = new DeskflowSession(options, dependencies);
        control = new MultiPeerControlClient(invitation, userApproved);
    }

    public async Task<DeskLinkState> StartAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await keyboard.StartAsync(ct); }
        finally { gate.Release(); }
    }

    public async Task<DeskLinkState> GetStateAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await keyboard.GetStateAsync(ct); }
        finally { gate.Release(); }
    }

    public async Task ConfirmKeyboardPeerAsync(string fingerprint, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); await keyboard.ConfirmPeerAsync(fingerprint, ct); }
        finally { gate.Release(); }
    }

    public async Task<bool> SwitchAsync(string target, CancellationToken ct = default) => (await SwitchDetailedAsync(target, ct)).InputTransferred;

    public async Task<DeskLinkSwitchResult> SwitchDetailedAsync(string target, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var evidence = keyboard.CapturePeerSwitchEvidence();
            var result = await control.SwitchDetailedAsync(target, ct);
            ct.ThrowIfCancellationRequested();
            keyboard.ValidatePeerSwitchEvidence(evidence);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var state = await keyboard.GetStateAsync(cancellationToken);
            if (state.ProcessState != DeskLinkProcessState.Running ||
                state.ConnectionState != DeskLinkConnectionState.Connected || state.PendingPeerFingerprint is not null)
                throw new InvalidOperationException("The keyboard connection is not confirmed.");
            return await control.BeginPreviewAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var state = await keyboard.GetStateAsync(cancellationToken);
            if (state.ProcessState != DeskLinkProcessState.Running ||
                state.ConnectionState != DeskLinkConnectionState.Connected || state.PendingPeerFingerprint is not null)
                throw new InvalidOperationException("The keyboard connection is not confirmed.");
            return await control.RenewPreviewAsync(id, cancellationToken);
        }
        finally { gate.Release(); }
    }
    public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default) =>
        PreviewRequestAsync(token => control.EndPreviewAsync(id, token), cancellationToken);
    public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default) =>
        PreviewRequestAsync(control.ReadPreviewAsync, cancellationToken);

    private async Task<DeskflowPreviewState> PreviewRequestAsync(Func<CancellationToken, Task<DeskflowPreviewState>> request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(disposed, this); return await request(ct); }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { if (!disposed) await keyboard.StopAsync(ct); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            await keyboard.DisposeAsync();
            await control.DisposeAsync();
            disposed = true;
        }
        finally { gate.Release(); }
    }
}

public sealed class WindowsPeerLink(string profileDirectory, string enginePath, MultiPeerInvitation invitation,
    bool userApproved, DeskflowSessionDependencies? dependencies = null)
    : MultiPeerClientLink(profileDirectory, enginePath, invitation, userApproved, dependencies);
