using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Windows.DeskSwitch.Collaboration;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private WindowsClipboardAdapter clipboard = null!;
    private ClipboardBridgeCursor? hostClipboardCursor, peerClipboardCursor;
    private string? hostClipboardTarget;
    private bool peerClipboardEnabled;

    private void InitializeClipboard() => clipboard = new WindowsClipboardAdapter(Dispatcher);

    private async void HostDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || HostClipboard is null) return;
        previewEpoch++;
        CloseHostDetached();
        SetPreview(HostPreviewImage, HostPreviewStatus, PreviewFrame.Off());
        hostClipboardTarget = null; hostClipboardCursor = null;
        RefreshDeviceControls();
        if (!await SynchronizeMiniSelectionAsync()) return;
        await RefreshHostClipboardToggleAsync();
        RefreshHostPreviewToggle();
        await RefreshHostRefreshChoiceAsync();
    }

    private async Task RefreshHostClipboardToggleAsync()
    {
        var selected = HostDevices.SelectedItem as TrustedDeviceIdentity;
        var current = host;
        if (selected is null || current is null) { HostClipboard.IsEnabled = false; HostClipboard.IsChecked = false; return; }
        try
        {
            var state = await current.GetClipboardStateAsync(selected.Name, CancellationToken.None);
            if (!ReferenceEquals(current, host) || !ReferenceEquals(selected, HostDevices.SelectedItem)) return;
            HostClipboard.IsEnabled = true; HostClipboard.IsChecked = state.LocalEnabled;
        }
        catch (ArgumentException) { HostClipboard.IsEnabled = false; HostClipboard.IsChecked = false; }
        catch (ObjectDisposedException) { HostClipboard.IsEnabled = false; HostClipboard.IsChecked = false; }
    }

    private async void HostClipboard_Click(object sender, RoutedEventArgs e)
    {
        var wanted = HostClipboard.IsChecked == true;
        await WorkAsync(async () =>
        {
            var device = SelectedHostDevice();
            if (host is null) throw new InvalidOperationException("请先启动主控共享。");
            await host.SetClipboardEnabledAsync(device.Name, wanted, CancellationToken.None);
            hostClipboardTarget = null; hostClipboardCursor = null;
            Feedback.Text = wanted ? "已开启该设备的文字剪贴板；另一端也需要开启。" : "已暂停该设备的文字剪贴板。";
        });
        await RefreshHostClipboardToggleAsync();
    }

    private async void PeerClipboard_Click(object sender, RoutedEventArgs e)
    {
        var wanted = PeerClipboard.IsChecked == true;
        await WorkAsync(() => SetPeerClipboardAsync(wanted));
        PeerClipboard.IsChecked = peerClipboardEnabled;
    }

    private async Task SetPeerClipboardAsync(bool wanted)
    {
            var data = peerData ?? throw new InvalidOperationException("请先连接主控。");
            var invitation = peerStore?.Get() ?? throw new InvalidOperationException("请先确认配对。");
            var settings = peerSettings ?? throw new InvalidOperationException("本机设置尚未打开。");
            var key = InvitationKey(invitation);
            var old = settings.Get(key);
            // Turning off takes effect locally before any network operation.
            if (!wanted) { peerClipboardEnabled = false; peerClipboardCursor = null; }
            await settings.SetAsync(key, old with { ClipboardEnabled = wanted });
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await data.SetClipboardEnabledAsync(wanted, timeout.Token);
                peerClipboardEnabled = wanted; peerClipboardCursor = null;
            }
            catch
            {
                if (wanted)
                {
                    await settings.SetAsync(key, old with { ClipboardEnabled = false }, CancellationToken.None);
                    peerClipboardEnabled = false;
                    try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await data.SetClipboardEnabledAsync(false, timeout.Token); } catch { }
                }
                throw;
            }
            Feedback.Text = wanted ? "文字剪贴板已开启，主控也需要开启。" : "文字剪贴板已暂停。";
        PeerClipboard.IsChecked = peerClipboardEnabled;
    }

    private async Task PollHostClipboardAsync(DeskLinkState state)
    {
        var current = host;
        if (current is null || state.ProcessState != DeskLinkProcessState.Running) return;
        var selected = SelectedHostClipboardPeer();
        var target = MultiPeerClipboardTarget.Choose(HostName.Text, state.ActiveComputer, selected, state.ConnectedComputers);
        if (target is null) { hostClipboardTarget = null; hostClipboardCursor = null; return; }
        var status = await current.GetClipboardStateAsync(target, CancellationToken.None);
        if (!status.LocalEnabled || !status.RemoteEnabled || !status.Connected)
        { hostClipboardTarget = null; hostClipboardCursor = null; return; }
        if (hostClipboardTarget != target || hostClipboardCursor is null)
        {
            hostClipboardTarget = target;
            var fresh = new ClipboardBridgeCursor(HostName.Text);
            hostClipboardCursor = fresh;
            // Do not forward a clipboard item left by an earlier active device.
            if (await current.GetClipboardAsync(target, CancellationToken.None) is { } prior) fresh.Ignore(prior.Id);
            if (!ReferenceEquals(hostClipboardCursor, fresh)) return;
        }
        var cursor = hostClipboardCursor;
        var sequence = clipboard.Sequence;
        if (cursor.ShouldPublish(sequence, true))
        {
            DesktopToolbox.DeskLink.Collaboration.ClipboardPayload? value;
            try { value = await clipboard.ReadAsync(HostName.Text, CancellationToken.None); }
            catch (System.Runtime.InteropServices.ExternalException) { cursor.RetryPublish(sequence); throw; }
            if (clipboard.Sequence != sequence || !await HostTargetStillAsync(current, target) || !ReferenceEquals(cursor, hostClipboardCursor)) return;
            if (value is null) await current.ClearClipboardAsync(target, CancellationToken.None);
            else await current.SetClipboardAsync(target, value, CancellationToken.None);
            return;
        }
        var remote = await current.GetClipboardAsync(target, CancellationToken.None);
        if (remote is null || !cursor.ShouldApply(remote.Id, remote.Origin) || !await HostTargetStillAsync(current, target) || !ReferenceEquals(cursor, hostClipboardCursor)) return;
        if (remote.Text is null || remote.PngBytes is not null) { cursor.Ignore(remote.Id); return; }
        var applied = await clipboard.ApplyAsync(remote, CancellationToken.None, sequence);
        if (applied.HasValue) cursor.Applied(remote.Id, applied.Value);
    }

    private async Task<bool> HostTargetStillAsync(MultiPeerRuntime current, string target)
    {
        if (!ReferenceEquals(host, current)) return false;
        var state = await current.GetKeyboardStateAsync(CancellationToken.None);
        if (state.ProcessState != DeskLinkProcessState.Running) return false;
        var selected = SelectedHostClipboardPeer();
        if (MultiPeerClipboardTarget.Choose(HostName.Text, state.ActiveComputer, selected, state.ConnectedComputers) != target) return false;
        var status = await current.GetClipboardStateAsync(target, CancellationToken.None);
        return status.LocalEnabled && status.RemoteEnabled && status.Connected;
    }

    private string? SelectedHostClipboardPeer()
    {
        var selected = (HostDevices.SelectedItem as TrustedDeviceIdentity)?.Name;
        return MultiPeerClipboardTarget.ReconcileSelection(selected, hostMiniService is not null, hostMiniService?.Snapshot.SelectedDevice);
    }

    private async Task PollPeerClipboardAsync()
    {
        var data = peerData;
        if (data is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await data.SetClipboardEnabledAsync(peerClipboardEnabled, timeout.Token);
        if (!peerClipboardEnabled) { peerClipboardCursor = null; return; }
        var status = await data.GetStatusAsync(timeout.Token);
        if (!status.RemoteClipboardEnabled) { peerClipboardCursor = null; return; }
        if (peerClipboardCursor is null)
        {
            var fresh = new ClipboardBridgeCursor(PeerName.Text);
            peerClipboardCursor = fresh;
            if (await data.GetLatestClipboardAsync(timeout.Token) is { } prior) fresh.Ignore(prior.Id);
            if (!ReferenceEquals(peerClipboardCursor, fresh)) return;
        }
        var cursor = peerClipboardCursor;
        var sequence = clipboard.Sequence;
        if (cursor.ShouldPublish(sequence, true))
        {
            DesktopToolbox.DeskLink.Collaboration.ClipboardPayload? value;
            try { value = await clipboard.ReadAsync(PeerName.Text, timeout.Token); }
            catch (System.Runtime.InteropServices.ExternalException) { cursor.RetryPublish(sequence); throw; }
            if (!ReferenceEquals(data, peerData) || !peerClipboardEnabled || clipboard.Sequence != sequence) return;
            if (value is null) await data.ClearClipboardAsync(timeout.Token);
            else await data.PublishClipboardAsync(value, timeout.Token);
            return;
        }
        var remote = await data.GetLatestClipboardAsync(timeout.Token);
        if (!ReferenceEquals(data, peerData) || !peerClipboardEnabled || remote is null || !cursor.ShouldApply(remote.Id, remote.Origin)) return;
        if (remote.Text is null || remote.PngBytes is not null) { cursor.Ignore(remote.Id); return; }
        var applied = await clipboard.ApplyAsync(remote, timeout.Token, sequence);
        if (applied.HasValue) cursor.Applied(remote.Id, applied.Value);
    }

    private async Task ClosePeerDataAsync()
    {
        var data = peerData;
        peerData = null; peerClipboardCursor = null;
        if (data is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await data.SetClipboardEnabledAsync(false, timeout.Token);
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException or ObjectDisposedException) { }
        finally { await data.DisposeAsync(); }
    }
}
