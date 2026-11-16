using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using Nian.Input.Control;
using Nian.Input.Mac;
using Nian.Input.Runtime;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Services;

/// <summary>Independent native input receiver and Windows-coordinated switching. Borrows the GUI-created target and guard.</summary>
public sealed partial class MacNativeLinkService : IMacLinkService, INativePreviewControl
{
    private sealed record ApprovedPairing(bool UserApproved, string BundleJson, TrustedEndpoint Endpoint, DevicePermission LocalPermissions);
    private readonly MacPrivateStore store;
    private readonly IMacNativeDesktopHost desktop;
    private readonly MacNativeLinkOptions options;
    private readonly MacNativeInputPolicy input;
    private readonly DesktopControlArbiter arbiter;
    private readonly bool ownsArbiter;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();
    private readonly object connectionsGate = new();
    private readonly HashSet<NativeInputConnection> connections = [];
    private readonly PreviewFrameAuthority frames;
    private readonly MacDisplaySwitchCoordinator displaySwitch;
    private readonly MacDisplayBridgeProcess? displays;
    private MacConnectionSettings settings = new();
    private DeviceIdentity? identity;
    private DeviceTrustStore? trust;
    private DesktopInputServer? server;
    private ApprovedPairing? approved;
    private DeskLinkPairingBundle? pairing;
    private (PairingPreview Preview, DeskLinkPairingBundle Bundle)? candidate;
    private DesktopControlState? windowsState;
    private bool loaded, controlReady, displayError;
    private int running, disposed, policyEpoch;
    private long switchSequence;
    private string error = "", displayMessage = "尚未启用 Mac 本地回切。";

    /// <summary>Construct target/guard on AppKit's GUI thread. Their ownership stays with the caller, including GUI-thread disposal.</summary>
    public MacNativeLinkService(string dataDirectory, MacDesktopInputTarget target, MacDesktopLocalEventGuard guard,
        DesktopControlArbiter? sharedArbiter = null, int receiverPort = 24932)
        : this(dataDirectory, ProductionHost(target, guard), new() { SharedArbiter = sharedArbiter, ReceiverPort = receiverPort }) { }

    private static IMacNativeDesktopHost ProductionHost(MacDesktopInputTarget target, MacDesktopLocalEventGuard guard)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Mac 原生共享只能在 Mac 上运行。");
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(guard);
        return new MacNativeDesktopHost(target, guard);
    }

    internal MacNativeLinkService(string dataDirectory, IMacNativeDesktopHost desktop, MacNativeLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(desktop); ArgumentNullException.ThrowIfNull(options);
        if (options.ReceiverPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options));
        store = new(Path.Combine(dataDirectory, "native-input")); this.desktop = desktop; this.options = options;
        arbiter = options.SharedArbiter ?? new DesktopControlArbiter(options.Clock); ownsArbiter = options.SharedArbiter is null;
        input = new(desktop, (peer, permission) => trust?.Allows(peer, permission) == true);
        frames = new(desktop.ReadPreviewGeometry, options.Clock);
        var write = options.SwitchDisplayAsync;
        if (write is null)
        {
            displays = new MacDisplayBridgeProcess();
            write = async (binding, token) =>
            {
                var result = await displays.SetInputAsync(new(binding.VendorId, binding.ProductId), checked((ushort)binding.WindowsInput), token);
                return new(true, result.Status, result.Error);
            };
        }
        displaySwitch = new(write);
    }

    public event Action<MacLinkSnapshot>? Changed;
    public int? NativeReceiverPort => server?.Port;
    public async Task<DeskLinkPairingBundle?> GetApprovedPairingAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { ThrowIfDisposed(); await LoadAsync(cancellationToken); return pairing; }
        finally { gate.Release(); }
    }
    public async Task StartAsync(MacConnectionSettings value, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed(); await LoadAsync(cancellationToken);
            var startingEpoch = Volatile.Read(ref policyEpoch);
            if (Volatile.Read(ref running) != 0) return;
            if (pairing is null || approved is null) throw new InvalidOperationException("请先在 Mac 本机确认新版双向原生配对。");
            value.ValidateApprovedPairing(pairing.Host, pairing.WindowsName, pairing.MacName);
            if (!desktop.IsGuardHealthy) throw new InvalidOperationException("Mac 本机输入保护尚未就绪。");
            if (displaySwitch.IsWriteInFlight || displays?.HasUnfinishedChildren == true) throw new InvalidOperationException("上一条 Mac 显示命令尚未结束。");
            await EnsureRuntimeAsync(pairing, cancellationToken);
            if (!trust!.Allows(approved.Endpoint.Fingerprint, DevicePermission.Control | DevicePermission.PreviewControl)) throw new InvalidOperationException("此设备的本机持续允许已撤销，请重新配对。");
            await store.WriteAsync("connection.json", value, cancellationToken); settings = value;
            lock (stateGate)
            {
                ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
                if (startingEpoch != policyEpoch) throw new OperationCanceledException("Mac 原生开始操作已取消。");
                input.Configure(approved.Endpoint.Fingerprint, approved.LocalPermissions); input.SetRunning(true); Volatile.Write(ref running, 1);
            }
            displaySwitch.ResetBaseline(); error = ""; Publish();
        }
        finally { gate.Release(); }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Local injection is disabled synchronously, even if a UI/RPC operation owns the async gate.
        var explicitlyReturn = Volatile.Read(ref running) != 0;
        StopLocal("local-stop"); return StopInsideAsync(explicitlyReturn, cancellationToken);
    }
    private async Task StopInsideAsync(bool explicitlyReturn, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (explicitlyReturn && approved is not null && identity is not null)
            {
                try
                {
                    var returned = await RequestSwitchInsideAsync(settings.WindowsName, token, allowStoppedReturn: true);
                    if (!returned.InputTransferred) { displayError = true; displayMessage = returned.Error; }
                }
                catch (Exception failure) when (!token.IsCancellationRequested && IsControlError(failure))
                { displayError = true; displayMessage = "本机输入已停止；Windows 显式回切尚未确认，请手动检查显示器输入。"; }
            }
            controlReady = false; displaySwitch.ResetBaseline(); Publish();
        }
        finally { gate.Release(); }
    }
    private void StopLocal(string reason)
    {
        lock (stateGate) { Interlocked.Increment(ref policyEpoch); Volatile.Write(ref running, 0); input.SetRunning(false); }
        frames.Invalidate();
        arbiter.StopAll(reason);
        lock (connectionsGate) foreach (var connection in connections) connection.Abort(reason);
    }

    public Task ConfirmPeerAsync(string fingerprint, bool accept, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("原生设备请通过新版配对文件在双方本机明确确认。"));

    private async Task LoadAsync(CancellationToken token)
    {
        if (loaded) return;
        settings = await store.ReadAsync<MacConnectionSettings>("connection.json", token) ?? new();
        var saved = await store.ReadAsync<ApprovedPairing>("paired-control.json", token);
        if (saved is { UserApproved: true })
        {
            var bundle = DeskLinkPairingBundle.Parse(saved.BundleJson); ValidateNativeBundle(bundle);
            var invitation = DeviceInvitation.Parse(bundle.InputInvitation!);
            if (saved.LocalPermissions != BothPermissions || saved.Endpoint.Host != bundle.Host || saved.Endpoint.Port != invitation.Port ||
                !string.Equals(saved.Endpoint.Fingerprint, invitation.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mac 本机原生配对记录不一致，原文件已保留。");
            approved = saved; pairing = bundle;
            settings = settings with { WindowsHost = bundle.Host, WindowsName = bundle.WindowsName, MacName = bundle.MacName };
        }
        ConfigureDisplay(); loaded = true;
    }

    private async Task EnsureRuntimeAsync(DeskLinkPairingBundle bundle, CancellationToken token)
    {
        identity ??= await DeviceIdentity.OpenAsync(store.DirectoryPath, bundle.MacName, options.IdentityVault, token);
        if (trust is null) { trust = await DeviceTrustStore.OpenAsync(store.DirectoryPath, token); trust.Changed += OnTrustChanged; }
        if (server is not null) return;
        var address = options.BindAddress ?? await FindLocalAddressAsync(bundle.Host, token);
        var receiver = new DesktopInputServer(identity, trust, address, options.ReceiverPort, input, (_, _) => Task.FromResult(false), options.Clock, new()
        {
            Arbiter = arbiter, PreviewFrames = frames, PrepareInput = input.Prepare, RenewInput = input.Renew,
            ReadStateAsync = (peer, _) =>
            {
                if (!string.Equals(Volatile.Read(ref approved)?.Endpoint.Fingerprint, peer, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("此设备不是当前本机批准的配对。");
                return Task.FromResult(ReadLocalState());
            }
            // Full-switch is coordinated exclusively by Windows, never by this receiver.
        });
        try { await receiver.StartAsync(token); server = receiver; }
        catch { await receiver.DisposeAsync(); throw; }
    }
    private static async Task<IPAddress> FindLocalAddressAsync(string host, CancellationToken token)
    {
        foreach (var remote in await Dns.GetHostAddressesAsync(host, token))
        {
            if (remote.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
            using var route = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try { route.Connect(new IPEndPoint(remote, 9)); if (route.LocalEndPoint is IPEndPoint local) return local.Address; }
            catch (SocketException) { }
        }
        throw new IOException("无法确定到已配对 Windows 的本机接收地址。");
    }
    private void OnTrustChanged(string fingerprint)
    {
        if (approved is not { } saved || !string.Equals(saved.Endpoint.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)) return;
        if (trust?.Allows(fingerprint, BothPermissions) != true) StopLocal("permission-revoked");
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    private MacLinkSnapshot Publish() { var value = Snapshot(); try { Changed?.Invoke(value); } catch { } return value; }
    private MacLinkSnapshot Snapshot()
    {
        var activity = input.ReadActivity();
        return new()
        {
            Backend = MacLinkBackend.Native, Settings = settings, IsRunning = Volatile.Read(ref running) != 0,
            // Native sessions exist only while controlling. A fresh authenticated
            // peer state keeps the ordinary switch UI ready while input is idle.
            IsConnected = controlReady && ApprovedEndpoint(DevicePermission.Control) is not null, ControlConnected = controlReady,
            HasApprovedPairing = pairing is not null, LocalFingerprint = identity?.Fingerprint ?? "", ActiveComputer = windowsState?.ActiveComputer ?? (activity.Active ? settings.MacName : settings.WindowsName),
            HasPendingDisplayCleanup = displays?.HasUnfinishedChildren == true, DisplayRecoveryMessage = displayMessage, HasDisplayRecoveryError = displayError,
            Error = error, StatusMessage = Volatile.Read(ref running) == 0 ? "原生共享已停止。" : activity.Active ? "原生键鼠控制已建立。" : "原生接收器已就绪，等待 Windows 明确开始控制。"
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        StopLocal("disposed"); await gate.WaitAsync();
        try
        {
            NativeInputConnection[] retired; lock (connectionsGate) { retired = connections.ToArray(); connections.Clear(); }
            foreach (var connection in retired) await connection.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
            if (trust is not null) { trust.Changed -= OnTrustChanged; trust.Dispose(); }
            identity?.Dispose(); if (displays is not null) await displays.DisposeAsync(); displaySwitch.Dispose();
            if (ownsArbiter) arbiter.Dispose();
        }
        finally { gate.Release(); }
    }
}
