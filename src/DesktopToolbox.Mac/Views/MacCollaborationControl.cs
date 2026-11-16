using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Views;

/// <summary>Business adapter for the single shared, approved HTML/CSS UI.</summary>
public sealed partial class MacCollaborationControl : MacWebViewHost, IDisposable
{
    private readonly Window owner;
    private readonly MacCollaborationService service;
    private readonly IMacMiniLink link;
    private readonly MacWirelessViewModel wireless;
    private readonly Action<bool, bool>? reportViewerVisibility;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer placementTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private MacRemotePreviewWindow? detached;
    private string epoch = "";
    private long revision;
    private bool collapsed, disposed, moving, placementRestored;
    private Size requested = new(398, 310);
    public event Action? LayoutRequested;
    public event Action? OpenSettings;
    public event Action<Window?>? PreviewPairingRequested;
    public Size RequestedSize => requested;
    public bool Collapsed => collapsed;
    public MacCollaborationControl(Window owner, MacCollaborationService service, IMacMiniLink link, MacWirelessViewModel wireless, Action<bool, bool>? reportViewerVisibility = null)
    {
        this.owner = owner; this.service = service; this.link = link; this.wireless = wireless; this.reportViewerVisibility = reportViewerVisibility;
        var initial = MacPreviewPresentation.MiniSize(service.Snapshot.Settings.PreviewEnabled, false); requested = new Size(initial.Width, initial.Height);
        owner.PositionChanged += OnPositionChanged; owner.PropertyChanged += OnAppearanceChanged;
        service.Changed += OnChanged; link.PropertyChanged += OnModelChanged; wireless.PropertyChanged += OnModelChanged;
        placementTimer.Tick += async (_, _) =>
        {
            placementTimer.Stop(); if (!moving || disposed) return; moving = false;
            try { await RememberPlacementAsync(); } catch (OperationCanceledException) { }
        };
    }
    private void OnChanged(MacCollaborationSnapshot state) => Dispatcher.UIThread.Post(() => { if (!disposed) PushState(); });
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() => { if (!disposed) PushState(); });
    private void OnAppearanceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualThemeVariantProperty || e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
        { RefreshViewerVisibility(); PushState(); }
    }
    private void PushState()
    {
        if (disposed || !BridgeReady) return;
        var state = service.Snapshot; var settings = state.Settings;
        owner.Topmost = settings.Pinned;
        var frame = settings.PreviewEnabled ? state.Preview : PreviewFrame.Off("local-off", "画面已关闭");
        detached?.SetAppearance(owner.ActualThemeVariant==ThemeVariant.Dark);
        detached?.SetPassiveContext(state.PassivePreviewGenerationSupported,state.PassivePreviewRequestId,state.RemotePreviewSourceStatus);
        detached?.SetConnectionState(service.PreviewControlReady,service.PreviewControlHint);
        detached?.ShowFrame(frame, settings.PreviewRefreshSeconds);
        var maxHeight = owner.Screens.ScreenFromWindow(owner) is { } screen ? screen.WorkingArea.Height / screen.Scaling - 16 : 1000;
        var multi = link is MacMultiMiniLink;
        PostWeb("state", new
        {
            epoch, revision = ++revision, platform = "Mac", connected = state.IsOnline,
            connectionMessage = state.IsOnline ? "Windows 已连接" : state.Status,
            multiDeviceMode = multi,
            devices = multi ? new[] { new { name = link.WindowsName, label = "主控 · " + link.WindowsName } } : Array.Empty<object>(),
            localDeviceName = multi ? link.MacName : "", selectedDevice = multi ? link.WindowsName : "",
            sharedFolderIsRemote = multi,
            activeComputer = link.IsMacActive ? (multi ? link.MacName : "Mac") : link.IsWindowsActive ? (multi ? link.WindowsName : "Windows") : "",
            canSwitchWindows = link.CanReturnToWindows, canSwitchMac = link.CanSwitch,
            keyboardSharingRunning = link.IsSharingRunning, keyboardSharingConnected = link.IsInputConnected,
            keyboardCanStart = link.CanStart, keyboardCanStop = link.CanStop,
            keyboardSharingMessage = multi && link.ErrorMessage.Length > 0 ? link.ErrorMessage : link.IsInputConnected ? "键鼠已连接" : link.IsSharingRunning ? "等待键鼠连接" : "键鼠共享未启动",
            previewEnabled = settings.PreviewEnabled, previewRefreshSeconds = settings.PreviewRefreshSeconds, preview = frame,
            clipboardEnabled = settings.ClipboardEnabled,
            clipboardMessage = !settings.ClipboardEnabled ? "开启后，另一边可直接粘贴新复制的文字和链接。" : state.ClipboardError ?? (state.RemoteClipboardEnabled ? "两端已开启，新复制的文字和链接会自动互通。" : "请在 Windows 开启剪贴板互通。"),
            receiveDirectory = settings.ReceiveDirectory, receiveSetupRequired = state.ReceiveSetupRequired, receiveError = state.ReceiveError, sharedDirectory = "",
            transfers = state.Transfer is { } transfer ? new[] { transfer } : Array.Empty<TransferProgress>(),
            canPauseSending = service.CanPauseSending, canRetrySending = service.CanRetrySending,
            pinned = settings.Pinned, collapsed, theme = owner.ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light",
            reduceTransparency = false, maxHeight,
            wireless = new { enabled = wireless.IsSmoothEnabled, canChange = wireless.CanChange, needsRestoreRetry = wireless.NeedsRestoreRetry, canRetryRestore = wireless.CanRetryRestore, status = wireless.StatusMessage, error = wireless.ErrorMessage }
        });
        RefreshViewerVisibility();
    }
    protected override async void OnWebRequest(MacWebRequest request)
    {
        if (disposed) return;
        var requestEpoch = epoch;
        try
        {
            if (request.Action == "ready")
            {
                epoch = Guid.NewGuid().ToString("N"); revision = 0; BridgeReady = true;
                Reply(request.Id, epoch, true, new { epoch }); PushState(); return;
            }
            if (request.Action.StartsWith("native.", StringComparison.Ordinal)) return;
            if (!BridgeReady && !request.Native) return;
            var result = await DispatchActionAsync(request);
            if (!disposed) { if (request.Native && request.Action == "files.nativeDrop") PostWeb("transfer-result", new { ok = true }); else Reply(request.Id, requestEpoch, true, result); PushState(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed) { var message = error is OperationCanceledException ? "操作已暂停，可稍后重试。" : error.Message; if (request.Native && request.Action == "files.nativeDrop") PostWeb("transfer-result", new { ok = false, error = message }); else Reply(request.Id, requestEpoch, false, null, message); PushState(); } }
    }
    private void Reply(string? id, string replyEpoch, bool ok, object? result = null, string? error = null)
    { if (id is not null) PostWeb("reply", new { id, epoch = replyEpoch, ok, result, error }); }
    public void RefreshViewerVisibility()
    {
        if (disposed) return;
        var visible = !collapsed && IsVisible && owner.IsVisible && owner.WindowState != WindowState.Minimized;
        var detachedVisible = detached?.IsVisible == true && detached.WindowState != WindowState.Minimized;
        if (reportViewerVisibility is not null) reportViewerVisibility(visible, detachedVisible);
        else service.SetViewerVisibility(visible, detachedVisible);
    }
    private void OpenDetached()
    {
        if (!service.Snapshot.Settings.PreviewEnabled) return;
        if (detached is not null) { if (detached.WindowState == WindowState.Minimized) detached.WindowState = WindowState.Normal; detached.Activate(); return; }
        var handoff=service.CreateDeskflowPreviewSession();
        var candidateBlue = link is MacMultiMiniLink;
        detached = handoff is not null?new MacRemotePreviewWindow(handoff,"Mac","Windows",candidateBlue):new MacRemotePreviewWindow(service.CreatePreviewControlSession(),candidateBlue);
        detached.SetAppearance(owner.ActualThemeVariant==ThemeVariant.Dark);
        detached.SetPassiveContext(service.Snapshot.PassivePreviewGenerationSupported,service.Snapshot.PassivePreviewRequestId,service.Snapshot.RemotePreviewSourceStatus);
        detached.SetConnectionState(service.PreviewControlReady,service.PreviewControlHint);
        var viewer=detached;
        detached.PairingRequested+=()=>PreviewPairingRequested?.Invoke(viewer);
        detached.Closed += (_, _) => { detached = null; RefreshViewerVisibility(); };
        detached.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) RefreshViewerVisibility(); };
        detached.Show(); detached.ShowFrame(service.Snapshot.Preview, service.Snapshot.Settings.PreviewRefreshSeconds); RefreshViewerVisibility();
    }
    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    { if (moving) { placementTimer.Stop(); placementTimer.Start(); } }
    public void ApplySavedPlacement()
    {
        var settings = service.Snapshot.Settings;
        if (!placementRestored)
        {
            placementRestored = true;
            if (settings.WindowX is { } x && settings.WindowY is { } y) owner.Position = new PixelPoint(x, y);
        }
        if (moving || owner.Screens.ScreenFromWindow(owner) is not { } screen) return;
        var area = screen.WorkingArea; var width = (int)(owner.Width * screen.Scaling); var height = (int)(owner.Height * screen.Scaling);
        var targetX = settings.DockEdge == "left" ? area.X : settings.DockEdge == "right" ? area.Right - width : owner.Position.X;
        var resolved = MacMiniPlacement.Resolve(targetX, owner.Position.Y, width, height, area.X, area.Y, area.Width, area.Height, 0);
        var target = new PixelPoint(resolved.X, resolved.Y); if (target != owner.Position) owner.Position = target;
    }
    private async Task RememberPlacementAsync()
    {
        if (owner.Screens.ScreenFromWindow(owner) is not { } screen) return;
        var area = screen.WorkingArea;
        var resolved = MacMiniPlacement.Resolve(owner.Position.X, owner.Position.Y, (int)(owner.Width * screen.Scaling), (int)(owner.Height * screen.Scaling), area.X, area.Y, area.Width, area.Height, (int)(32 * screen.Scaling));
        owner.Position = new PixelPoint(resolved.X, resolved.Y); await service.SetPlacementAsync(resolved.X, resolved.Y, resolved.Edge, lifetime.Token);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); placementTimer.Stop(); owner.PositionChanged -= OnPositionChanged; owner.PropertyChanged -= OnAppearanceChanged;
        service.Changed -= OnChanged; link.PropertyChanged -= OnModelChanged; wireless.PropertyChanged -= OnModelChanged;
        if (reportViewerVisibility is not null) reportViewerVisibility(false, false);
        else service.SetViewerVisibility(false, false);
        detached?.Close(); lifetime.Dispose();
    }
}




