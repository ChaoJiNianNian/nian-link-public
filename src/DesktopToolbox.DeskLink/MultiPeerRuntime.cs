using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Coordination;
using DesktopToolbox.DeskLink.Preview;

namespace DesktopToolbox.DeskLink;

/// <summary>Progress of stopping after the approved host LAN interface becomes unavailable.</summary>
public enum HostNetworkLossStatus { None, Stopping, AwaitingNetworkApproval, CleanupUnconfirmed }

public interface IMultiPeerKeyboard : IAsyncDisposable
{
    Task<DeskLinkState> StartAsync(CancellationToken ct);
    Task<DeskLinkState> GetStateAsync(CancellationToken ct);
    Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expected, long revision, CancellationToken ct);
    Task<DeskLinkState> StopAsync(CancellationToken ct);
}
public interface IMultiPeerKeyboardFocusEvents
{
    event EventHandler<FocusChangedEvent>? FocusChanged;
}
public sealed record MultiPeerDeviceEndpoint(string Name, string Platform, int DataPort);
public sealed record MultiPeerClipboardState(bool LocalEnabled, bool RemoteEnabled, bool Connected);
public sealed record MultiPeerCaptureDemand(bool Requested, int Width, int Height, int RefreshSeconds, Guid? RequestId);

/// <summary>Owns isolated multi-device components. LAN binding and product integration remain disabled.</summary>
public sealed class MultiPeerRuntime : IMultiPeerRuntime, IAsyncDisposable
{
    private readonly MultiDeviceCredentialVault vault;
    private readonly string root, hostName;
    private readonly IPAddress address;
    private readonly VerifiedLanBinding? lanBinding;
    private readonly int controlPort, dataPortBase;
    private readonly Func<IReadOnlyList<TrustedDeviceIdentity>, IMultiPeerKeyboard> createKeyboard;
    private readonly IUnifiedDisplayControl? displayControl;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<(MultiPeerDeviceEndpoint Endpoint, CollaborationServer Server)> data = [];
    private readonly Dictionary<string, MultiPeerFileSettings> peerSettings = new(StringComparer.Ordinal);
    private MultiScreenConfiguration? memoryScreens;
    private readonly MultiPeerSettingsStore? settingsStore;
    private readonly ConcurrentDictionary<long, TaskCompletionSource> operations = new();
    private long nextOperation;
    private CancellationTokenSource? activeLifetime;
    private MultiPeerDeviceEndpoint[] publishedDevices = [];
    private IMultiPeerKeyboard? keyboard;
    private MultiPeerControlServer? control;
    private MultiPeerPreviewManager? previewManager;
    private CancellationTokenSource? previewWatchLifetime;
    private Task previewWatch = Task.CompletedTask;
    private bool disposed;
    private CancellationTokenSource? networkWatchLifetime;
    private Task networkWatch = Task.CompletedTask;
    private string? lastError;
    private int networkLossStatus;
    // Set only after an explicit stop acquires the lifecycle gate. A canceled
    // wait must not suppress the genuine network-failure cleanup.
    private CancellationTokenSource? manualStopLifetime;

    public IReadOnlyList<MultiPeerDeviceEndpoint> Devices => Volatile.Read(ref publishedDevices).ToArray();
    public int ControlPort => control?.Port ?? 0;
    public string? CertificateSha256 => control?.CertificateSha256;
    public string? LastError => Volatile.Read(ref lastError);
    public HostNetworkLossStatus NetworkLossStatus => (HostNetworkLossStatus)Volatile.Read(ref networkLossStatus);
    public bool OwnsLiveKeyboardProcess(int processId) => keyboard is MultiPeerKeyboardSession owned && owned.OwnsLiveProcess(processId);

    public async Task<string> PrepareControlIdentityAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (control?.CertificateSha256 is { } active) return active;
            var directory = Path.Combine(root, "control");
            var certificatePath = Path.Combine(directory, "multi-control.pem");
            var leasePath = Path.Combine(directory, ".identity.lock");
            RejectLinks(directory); RejectLinks(certificatePath); RejectLinks(leasePath);
            PrivateFiles.EnsureDirectory(directory);
            using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await PrivateFiles.EnsureCertificateAsync(certificatePath, ct);
            using var certificate = PrivateFiles.LoadCertificate(certificatePath);
            return DeskLinkFingerprint.OfCertificate(certificate);
        }
        finally { gate.Release(); }
    }

    // Only the isolated candidate pairing coordinator can use this key; the UI receives no certificate object.
    internal async Task<(string Name, X509Certificate2 Certificate)> OpenPairingIdentityAsync(DiscoveryNetworkBinding network, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(network);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lanBinding is null || !network.HasLiveNetworkOrigin || !lanBinding.IsStillAvailable() ||
                !network.IsHardwareAvailable() || !lanBinding.Address.Equals(network.Address) ||
                !lanBinding.MatchesInterface(network.Interface.Name, network.Interface.Id))
                throw new InvalidOperationException("The pairing network no longer matches the prepared host.");
            var directory = Path.Combine(root, "control");
            var certificatePath = Path.Combine(directory, "multi-control.pem");
            RejectLinks(directory); RejectLinks(certificatePath);
            if (!File.Exists(certificatePath)) throw new InvalidOperationException("Prepare the host identity before pairing.");
            var certificate = PrivateFiles.LoadTlsCertificate(certificatePath);
            if (!certificate.HasPrivateKey) { certificate.Dispose(); throw new InvalidOperationException("Host identity is unavailable."); }
            return (hostName, certificate);
        }
        finally { gate.Release(); }
    }

    // The same runtime/vault and user-approved network must back a fileless invitation.
    internal async Task<VerifiedLanBinding> RequirePairingBindingAsync(MultiDeviceCredentialVault expectedVault, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expectedVault);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ReferenceEquals(vault, expectedVault) || lanBinding is null || !lanBinding.IsStillAvailable())
                throw new InvalidOperationException("The prepared host trust store or approved network changed.");
            return lanBinding;
        }
        finally { gate.Release(); }
    }

    public MultiPeerRuntime(MultiDeviceCredentialVault vault, string root, string hostName, IPAddress address,
        int controlPort, int dataPortBase, Func<IReadOnlyList<TrustedDeviceIdentity>, IMultiPeerKeyboard> createKeyboard,
        MultiPeerSettingsStore? settingsStore = null, IUnifiedDisplayControl? displayControl = null)
        : this(vault, root, hostName, address, controlPort, dataPortBase, createKeyboard, null, settingsStore, displayControl) { }

    public static MultiPeerRuntime CreateForLan(MultiDeviceCredentialVault vault, string root, string hostName,
        VerifiedLanBinding binding, int controlPort, int dataPortBase, string enginePath, string expectedEngineHash,
        MultiPeerSettingsStore? settingsStore = null, IUnifiedDisplayControl? displayControl = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!DeskLinkFingerprint.Matches(expectedEngineHash, MultiPeerKeyboardSession.ExperimentalEngineSha256))
            throw new InvalidDataException("This engine is not the audited multi-device candidate.");
        return new MultiPeerRuntime(vault, root, hostName, binding.Address, controlPort, dataPortBase,
            peers => MultiPeerKeyboardSession.CreateForLan(enginePath, expectedEngineHash, Path.Combine(root, "keyboard"),
                hostName, peers, binding), binding, settingsStore, displayControl);
    }

    private MultiPeerRuntime(MultiDeviceCredentialVault vault, string root, string hostName, IPAddress address,
        int controlPort, int dataPortBase, Func<IReadOnlyList<TrustedDeviceIdentity>, IMultiPeerKeyboard> createKeyboard,
        VerifiedLanBinding? binding, MultiPeerSettingsStore? settingsStore, IUnifiedDisplayControl? displayControl)
    {
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute runtime path is required.", nameof(root));
        DeskflowProfile.ValidateName(hostName);
        if (address is null || !IPAddress.IsLoopback(address) && (binding is null || !binding.Address.Equals(address)))
            throw new ArgumentException("A verified LAN interface is required.", nameof(address));
        if (controlPort is < 0 or > 65535 || dataPortBase is < 0 or > 65532 ||
            dataPortBase != 0 && controlPort >= dataPortBase && controlPort < dataPortBase + 4)
            throw new ArgumentException("Invalid or overlapping endpoint ports.");
        this.root = Path.GetFullPath(root); this.hostName = hostName; this.address = address;
        lanBinding = binding;
        this.settingsStore = settingsStore;
        this.displayControl = displayControl;
        this.controlPort = controlPort; this.dataPortBase = dataPortBase;
        this.createKeyboard = createKeyboard ?? throw new ArgumentNullException(nameof(createKeyboard));
    }

    public async Task StartAsync(IReadOnlyList<TrustedDeviceIdentity> peers, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (NetworkLossStatus == HostNetworkLossStatus.CleanupUnconfirmed)
                throw new InvalidOperationException("Previous sharing stop or keyboard return was not confirmed; inspect device state and restart the test application.");
            if (NetworkLossStatus == HostNetworkLossStatus.AwaitingNetworkApproval)
                throw new InvalidOperationException("The previous approved network is no longer valid; restart the test application and approve the network again.");
            if (lanBinding is not null && !lanBinding.IsStillAvailable())
                throw new InvalidOperationException("The approved LAN interface is unavailable.");
            if (keyboard is not null || control is not null || data.Count != 0) throw new InvalidOperationException("Stop the current runtime first.");
            var approved = vault.GetTrustedDevices();
            if (peers.Count is < 1 or > MultiDeviceCredentials.MaximumPeers ||
                peers.Count != approved.Count || peers.Distinct().Count() != peers.Count || peers.Except(approved).Any() ||
                peers.Any(x => string.Equals(x.Name, hostName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Device list no longer matches approved trust.");
            RejectLinks(root);
            PrivateFiles.EnsureDirectory(root);
            try
            {
                activeLifetime = new CancellationTokenSource();
                keyboard = createKeyboard(peers.ToArray());
                await keyboard.StartAsync(ct);
                previewManager = new MultiPeerPreviewManager(hostName, peers.Select(x => x.Name), keyboard, display: displayControl);
                if (keyboard is IMultiPeerKeyboardFocusEvents focusEvents) focusEvents.FocusChanged += OnKeyboardFocus;
                var controlRoot = Path.Combine(root, "control");
                RejectLinks(controlRoot);
                control = lanBinding is null
                    ? new MultiPeerControlServer(controlRoot, address, controlPort, hostName, vault,
                        token => keyboard.GetStateAsync(token),
                        (target, expected, revision, token) => keyboard.SwitchIfCurrentAsync(target, expected, revision, token), previewManager)
                    : MultiPeerControlServer.CreateForLan(controlRoot, lanBinding, controlPort, hostName, vault,
                        token => keyboard.GetStateAsync(token),
                        (target, expected, revision, token) => keyboard.SwitchIfCurrentAsync(target, expected, revision, token), previewManager);
                await control.StartAsync(ct);
                var cert = Path.Combine(controlRoot, "multi-control.pem");
                foreach (var peer in peers)
                {
                    var port = dataPortBase == 0 ? 0 : dataPortBase + peer.Slot - 1;
                    var saved = GetPeerSettings(peer.Name);
                    var endpoint = lanBinding is null
                        ? MultiPeerCollaborationEndpoint.Create(vault, peer.Name, Path.Combine(root, "data"), cert, address, port, saved.ReceiveDirectory, saved.Shares)
                        : MultiPeerCollaborationEndpoint.CreateForLan(vault, peer.Name, Path.Combine(root, "data"), cert, lanBinding, port, saved.ReceiveDirectory, saved.Shares);
                    // Track before startup so a failed bind is still disposed.
                    var item = new MultiPeerDeviceEndpoint(peer.Name, peer.Platform, 0);
                    data.Add((item, endpoint));
                    await endpoint.StartAsync(ct);
                    endpoint.SetLocalClipboardEnabled(saved.ClipboardEnabled);
                    data[^1] = (item with { DataPort = endpoint.Port }, endpoint);
                }
                var activeSettings = peers.Select(x => PeerSettingsKey(x.Name)).ToHashSet(StringComparer.Ordinal);
                foreach (var key in peerSettings.Keys.Where(x => !activeSettings.Contains(x)).ToArray()) peerSettings.Remove(key);
                if (settingsStore is not null) await settingsStore.RetainAsync(activeSettings, ct);
                else if (memoryScreens is not null && memoryScreens.Peers.Any(peer => !activeSettings.Contains(peer.GrantKey))) memoryScreens = null;
                Volatile.Write(ref publishedDevices, data.Select(x => x.Endpoint).ToArray());
                Volatile.Write(ref lastError, null);
                Volatile.Write(ref networkLossStatus, (int)HostNetworkLossStatus.None);
                previewWatchLifetime = new CancellationTokenSource();
                previewWatch = WatchPreviewAsync(previewWatchLifetime.Token);
                if (lanBinding is not null)
                {
                    networkWatchLifetime = new CancellationTokenSource();
                    networkWatch = WatchNetworkAsync(networkWatchLifetime.Token, CaptureNetworkLoss());
                }
            }
            catch (Exception startupError)
            {
                try { await StopInsideAsync(); }
                catch (Exception cleanupError)
                { throw new AggregateException("Runtime startup and cleanup both failed.", startupError, cleanupError); }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            manualStopLifetime = activeLifetime;
            try { await StopInsideAsync(); }
            catch { Volatile.Write(ref networkLossStatus, (int)HostNetworkLossStatus.CleanupUnconfirmed); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task<DeskLinkState> GetKeyboardStateAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is null) return new DeskLinkState();
            return await keyboard.GetStateAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task<DeskLinkSwitchResult> SwitchKeyboardAsync(string target, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (keyboard is null) throw new InvalidOperationException("The multi-device session is not running.");
            if (!string.Equals(target, hostName, StringComparison.Ordinal) &&
                !data.Any(x => string.Equals(x.Endpoint.Name, target, StringComparison.Ordinal)))
                throw new ArgumentException("Select an approved device.", nameof(target));
            if (lanBinding is not null && !lanBinding.IsStillAvailable())
                throw new InvalidOperationException("The approved LAN interface is unavailable.");
            if (previewManager is not null) return await previewManager.SwitchNormalAsync(hostName, target, ct);
            var state = await keyboard.GetStateAsync(ct);
            if (state.ProcessState != DeskLinkProcessState.Running || !state.NativeControlAvailable || state.Revision < 0 ||
                state.ActiveComputer is null ||
                (!string.Equals(state.ActiveComputer, hostName, StringComparison.Ordinal) &&
                 !data.Any(x => string.Equals(x.Endpoint.Name, state.ActiveComputer, StringComparison.Ordinal))) ||
                (!string.Equals(target, hostName, StringComparison.Ordinal) &&
                 !state.ConnectedComputers.Contains(target, StringComparer.Ordinal)))
                throw new InvalidOperationException("The chosen device is not connected or the keyboard state is unavailable.");
            return await keyboard.SwitchIfCurrentAsync(target, state.ActiveComputer, state.Revision, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> BeginPreviewAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await (previewManager ?? throw new InvalidOperationException("Sharing is not running.")).BeginAsync(hostName, peer, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await (previewManager ?? throw new InvalidOperationException("Sharing is not running.")).RenewAsync(hostName, id, ct); }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await (previewManager ?? throw new InvalidOperationException("Sharing is not running.")).EndAsync(hostName, id, ct); }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await (previewManager ?? throw new InvalidOperationException("Sharing is not running.")).ReadForDeviceAsync(hostName, ct); }
        finally { gate.Release(); }
    }

    public async Task<DeskflowPreviewSession> CreateHostPreviewSessionAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (previewManager is null) throw new InvalidOperationException("Sharing is not running.");
            var endpoint = GetPeer(peer);
            var source = new RemoteUploadControlLivePreviewSource(() => endpoint, () =>
                endpoint.RemotePassivePreviewGenerationSupported && endpoint.RemotePreviewSourceStatus?.Availability == "enabled");
            return new DeskflowPreviewSession(new HostPreviewControl(this, peer), source);
        }
        finally { gate.Release(); }
    }

    private sealed class HostPreviewControl(MultiPeerRuntime runtime, string peer) : IDeskflowPreviewControl
    {
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "Ctrl+Alt+F11";
        public string PreviewHandoffHint => "请先连接设备并开启画面";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default) => runtime.BeginPreviewAsync(peer, cancellationToken);
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default) => runtime.RenewPreviewAsync(id, cancellationToken);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default) => runtime.EndPreviewAsync(id, cancellationToken);
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default) => runtime.ReadPreviewAsync(cancellationToken);
    }

    public async Task SetReceiveDirectoryAsync(string peer, string? directory, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var endpoint = GetPeer(peer);
            var old = GetPeerSettings(peer);
            await ChangeSettingsAsync(peer, old, old with { ReceiveDirectory = directory },
                value => endpoint.UpdateReceiveDirectory(value.ReceiveDirectory), ct);
        }
        finally { gate.Release(); }
    }

    public async Task SetSharedDirectoriesAsync(string peer, IReadOnlyList<SharedDirectory> shares, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var endpoint = GetPeer(peer);
            var old = GetPeerSettings(peer);
            await ChangeSettingsAsync(peer, old, old with { Shares = shares.ToArray() },
                value => endpoint.UpdateSharedDirectories(value.Shares ?? []), ct);
        }
        finally { gate.Release(); }
    }

    public Task<TransferManifest> QueueFilesAsync(string peer, IEnumerable<string> paths, CancellationToken ct) =>
        WithPeerOperationAsync(peer, (endpoint, token) => endpoint.QueueFilesAsync(paths, token), ct);

    public Task<IReadOnlyList<TransferProgress>> GetTransfersAsync(string peer, CancellationToken ct) =>
        WithPeerOperationAsync(peer, (endpoint, _) => Task.FromResult(endpoint.GetTransfers()), ct);

    public Task<SharedListing> ListLocalSharedAsync(string peer, string? rootId, string relativePath, CancellationToken ct) =>
        WithPeerOperationAsync(peer, (endpoint, _) => Task.FromResult(endpoint.ListLocalShared(rootId, relativePath)), ct);

    public Task<SharedPreview> PreviewLocalSharedAsync(string peer, string rootId, string relativePath, CancellationToken ct) =>
        WithPeerOperationAsync(peer, (endpoint, token) => endpoint.GetLocalSharedPreviewAsync(rootId, relativePath, token), ct);

    public async Task<TransferReceipt> CopyLocalSharedAsync(string peer, string rootId, string relativePath, CancellationToken ct)
    {
        var directory = (await GetFileSettingsAsync(peer, ct)).ReceiveDirectory;
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("请先选择本机收件目录。");
        return await WithPeerOperationAsync(peer, (endpoint, token) => endpoint.CopyLocalSharedAsync(rootId, relativePath, directory, token), ct);
    }

    private async Task<T> WithPeerOperationAsync<T>(string peer, Func<CollaborationServer, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        CollaborationServer endpoint;
        CancellationTokenSource linked;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long operationId;
        await gate.WaitAsync(ct);
        try
        {
            endpoint = GetPeer(peer);
            var lifetime = activeLifetime ?? throw new InvalidOperationException("The multi-device session is not running.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            operationId = Interlocked.Increment(ref nextOperation);
            operations[operationId] = completion;
        }
        finally { gate.Release(); }
        try { return await action(endpoint, linked.Token); }
        finally
        {
            linked.Dispose();
            operations.TryRemove(operationId, out _);
            completion.TrySetResult();
        }
    }

    public async Task<PreviewFrame> GetPreviewAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return GetPeer(peer).GetRemotePreview(); }
        finally { gate.Release(); }
    }

    public async Task<PreviewSourceStatus?> GetRemotePreviewSourceAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return GetPeer(peer).RemotePreviewSourceStatus; }
        finally { gate.Release(); }
    }

    public async Task<MultiPeerCaptureDemand> GetCaptureDemandAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var endpoint = GetPeer(peer);
            return new(endpoint.IsPeerConnected && endpoint.LocalCaptureRequested,
                endpoint.LocalCaptureMaxWidth, endpoint.LocalCaptureMaxHeight, endpoint.LocalCaptureRefreshSeconds, endpoint.LocalCaptureRequestId);
        }
        finally { gate.Release(); }
    }

    public async Task PublishPreviewAsync(string peer, PreviewFrame frame, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { GetPeer(peer).SetLocalPreview(frame); }
        finally { gate.Release(); }
    }

    public async Task SetPreviewSourceAsync(string peer, PreviewSourceStatus status, CancellationToken ct,
        ILivePreviewCaptureProvider? liveCapture = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            var endpoint = GetPeer(peer);
            // Revocation cancels existing subscribers before publishing the locked/off state.
            if (status.Availability != "enabled") endpoint.LivePreview.SetLocalCaptureProvider(null);
            endpoint.SetLocalPreviewSource(status);
            if (status.Availability == "enabled") endpoint.LivePreview.SetLocalCaptureProvider(liveCapture);
        }
        finally { gate.Release(); }
    }

    public async Task SetPreviewRequestedAsync(string peer, bool enabled, CancellationToken ct, Guid? requestId = null,
        int maxWidth = 960, int maxHeight = 540)
    {
        await gate.WaitAsync(ct);
        try { GetPeer(peer).SetPreviewRequested(enabled, maxWidth, maxHeight, previewRefreshSeconds: GetPeerSettings(peer).PreviewRefreshSeconds, requestId: requestId); }
        finally { gate.Release(); }
    }

    public async Task<int> GetPreviewRefreshSecondsAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { _ = GetPeer(peer); return GetPeerSettings(peer).PreviewRefreshSeconds; }
        finally { gate.Release(); }
    }

    public async Task<MultiPeerMonitorSettings?> GetDisplayPreferenceAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { _ = GetPeer(peer); return GetPeerSettings(peer).Monitor; }
        finally { gate.Release(); }
    }

    public async Task<MultiPeerFileSettings> GetFileSettingsAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { _ = GetPeer(peer); var value = GetPeerSettings(peer); return value with { Shares = value.Shares?.ToArray() }; }
        finally { gate.Release(); }
    }

    public async Task SetDisplayPreferenceAsync(string peer, MultiPeerMonitorSettings? preference, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            _ = GetPeer(peer);
            var old = GetPeerSettings(peer);
            await ChangeSettingsAsync(peer, old, old with { Monitor = preference }, _ => { }, ct);
        }
        finally { gate.Release(); }
    }

    public async Task SetMonitorGroupAsync(IReadOnlyDictionary<string, MultiPeerMonitorSettings> mappings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var requested = mappings.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (requested.Count > 0) _ = MultiPeerMonitorGroup.Create(hostName, requested);
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var peers = data.Select(item => item.Endpoint.Name).ToArray();
            if (peers.Length == 0 || requested.Keys.Any(name => !peers.Contains(name, StringComparer.Ordinal)))
                throw new ArgumentException("请选择已配对且已启动的设备。", nameof(mappings));
            var updates = peers.ToDictionary(PeerSettingsKey, name => requested.GetValueOrDefault(name), StringComparer.Ordinal);
            if (settingsStore is not null) await settingsStore.SetMonitorsAsync(updates, ct);
            else
                foreach (var (key, monitor) in updates)
                {
                    var current = peerSettings.TryGetValue(key, out var value) ? value : new MultiPeerFileSettings();
                    peerSettings[key] = current with { Monitor = monitor };
                }
        }
        finally { gate.Release(); }
    }

    public async Task<MultiScreenLayout?> GetScreenTopologyAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var configuration = (settingsStore?.GetScreenConfiguration() ?? memoryScreens)?.ValidateAndCopy();
            return configuration is null ? null : new(configuration.HostName,
                Array.AsReadOnly(configuration.Peers.Select(peer => peer.DeviceName).ToArray()), configuration.Screens);
        }
        finally { gate.Release(); }
    }

    public async Task SetScreenTopologyAsync(IReadOnlyList<MultiScreenDefinition> screens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count is 0 or > 16 || screens.Any(screen => screen is null || screen.Attachments is null || screen.Attachments.Count is 0 or > 5))
            throw new ArgumentException("屏幕连接数量无效。", nameof(screens));
        var requested = screens.Select(screen => screen is null ? throw new ArgumentException("屏幕信息无效。", nameof(screens)) :
            screen with { Attachments = Array.AsReadOnly((screen.Attachments ?? throw new ArgumentException("屏幕连接无效。", nameof(screens))).ToArray()) }).ToArray();
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var peerNames = data.Select(item => item.Endpoint.Name).ToArray();
            if (peerNames.Length == 0) throw new InvalidOperationException("请先启动共享并添加设备。");
            var topology = MultiScreenTopology.Create(hostName, peerNames, requested);
            var attached = topology.Screens.SelectMany(screen => screen.Attachments).Select(item => item.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var grants = peerNames.Where(attached.Contains).Select(name => new MultiScreenPeerGrant(name, PeerSettingsKey(name))).ToArray();
            var configuration = new MultiScreenConfiguration(hostName, grants, topology.Screens.ToArray()).ValidateAndCopy();
            if (settingsStore is not null) await settingsStore.SetScreenConfigurationAsync(configuration, ct);
            else memoryScreens = configuration;
        }
        finally { gate.Release(); }
    }

    public async Task ClearScreenTopologyAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (settingsStore is not null) await settingsStore.SetScreenConfigurationAsync(null, ct);
            else memoryScreens = null;
        }
        finally { gate.Release(); }
    }

    public async Task SetPreviewRefreshSecondsAsync(string peer, int seconds, CancellationToken ct)
    {
        PreviewCaptureCadence.ValidateRefreshSeconds(seconds);
        await gate.WaitAsync(ct);
        try
        {
            _ = GetPeer(peer);
            var old = GetPeerSettings(peer);
            await ChangeSettingsAsync(peer, old, old with { PreviewRefreshSeconds = seconds }, _ => { }, ct);
        }
        finally { gate.Release(); }
    }

    public async Task SetClipboardEnabledAsync(string peer, bool enabled, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var endpoint = GetPeer(peer);
            var old = GetPeerSettings(peer);
            await ChangeSettingsAsync(peer, old, old with { ClipboardEnabled = enabled },
                value => endpoint.SetLocalClipboardEnabled(value.ClipboardEnabled), ct);
        }
        finally { gate.Release(); }
    }

    public async Task<ClipboardPayload?> GetClipboardAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return GetPeer(peer).GetRemoteClipboard(); }
        finally { gate.Release(); }
    }

    public async Task<MultiPeerClipboardState> GetClipboardStateAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var endpoint = GetPeer(peer);
            return new(GetPeerSettings(peer).ClipboardEnabled, endpoint.RemoteClipboardEnabled, endpoint.IsPeerConnected);
        }
        finally { gate.Release(); }
    }

    public async Task ClearClipboardAsync(string peer, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { GetPeer(peer).ClearClipboard(); }
        finally { gate.Release(); }
    }

    public async Task SetClipboardAsync(string peer, ClipboardPayload payload, CancellationToken ct)
    {
        if (payload.PngBytes is not null) throw new ArgumentException("Only text and links are supported.", nameof(payload));
        await gate.WaitAsync(ct);
        try { GetPeer(peer).SetLocalClipboard(payload); }
        finally { gate.Release(); }
    }

    private CollaborationServer GetPeer(string peer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return data.FirstOrDefault(x => string.Equals(x.Endpoint.Name, peer, StringComparison.OrdinalIgnoreCase)).Server
            ?? throw new ArgumentException("The selected device is not connected to this runtime.", nameof(peer));
    }

    private string PeerSettingsKey(string peer)
    {
        var token = vault.ReadTokenForEndpoint(peer);
        try { return Convert.ToHexString(SHA256.HashData(token)); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private MultiPeerFileSettings GetPeerSettings(string peer)
    {
        var key = PeerSettingsKey(peer);
        return settingsStore?.Get(key) ?? (peerSettings.TryGetValue(key, out var saved) ? saved : new());
    }

    private async Task ChangeSettingsAsync(string peer, MultiPeerFileSettings old, MultiPeerFileSettings next,
        Action<MultiPeerFileSettings> apply, CancellationToken ct)
    {
        MultiPeerSettingsStore.Validate(next);
        var key = PeerSettingsKey(peer);
        if (settingsStore is not null) await settingsStore.SetAsync(key, next, ct);
        try
        {
            apply(next);
            if (settingsStore is null) peerSettings[key] = next;
        }
        catch (Exception error)
        {
            try
            {
                if (settingsStore is not null) await settingsStore.SetAsync(key, old, CancellationToken.None);
                apply(old);
            }
            catch (Exception recovery) { throw new AggregateException("Device settings could not be applied or restored.", error, recovery); }
            throw;
        }
    }

    private async Task StopInsideAsync()
    {
        // Never dismantle the return channel while preview ownership is unresolved.
        if (previewManager is not null) { await previewManager.DisposeAsync(); previewManager = null; }
        if (keyboard is IMultiPeerKeyboardFocusEvents focusEvents) focusEvents.FocusChanged -= OnKeyboardFocus;
        if (previewWatchLifetime is not null)
        {
            await previewWatchLifetime.CancelAsync();
            try { await previewWatch; } catch (OperationCanceledException) { }
            previewWatchLifetime.Dispose(); previewWatchLifetime = null; previewWatch = Task.CompletedTask;
        }
        Volatile.Write(ref publishedDevices, []);
        var errors = new List<Exception>();
        if (activeLifetime is not null)
        {
            try { await activeLifetime.CancelAsync(); }
            catch (Exception error) { errors.Add(error); }
            try { await Task.WhenAll(operations.Values.Select(x => x.Task)); }
            catch (Exception error) { errors.Add(error); }
            finally { activeLifetime.Dispose(); activeLifetime = null; }
        }
        if (networkWatchLifetime is not null)
        {
            await networkWatchLifetime.CancelAsync();
            try { await networkWatch; } catch (OperationCanceledException) { }
            networkWatchLifetime.Dispose(); networkWatchLifetime = null;
            networkWatch = Task.CompletedTask;
        }
        for (var i = data.Count - 1; i >= 0; i--)
        {
            try { await data[i].Server.DisposeAsync(); data.RemoveAt(i); }
            catch (Exception error) { errors.Add(error); }
        }
        if (control is not null)
        {
            try { await control.DisposeAsync(); control = null; }
            catch (Exception error) { errors.Add(error); }
        }
        if (keyboard is not null)
        {
            try
            {
                var state = await keyboard.GetStateAsync(CancellationToken.None);
                if (state.ProcessState == DeskLinkProcessState.Running && state.NativeControlAvailable &&
                    state.ActiveComputer is { } active && active != hostName)
                {
                    var result = await keyboard.SwitchIfCurrentAsync(hostName, active, state.Revision, CancellationToken.None);
                    if (!result.InputTransferred) errors.Add(new InvalidOperationException("Keyboard return was not confirmed."));
                }
            }
            catch (Exception error) { errors.Add(error); }
            try { await keyboard.StopAsync(CancellationToken.None); await keyboard.DisposeAsync(); keyboard = null; }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException("Multi-device runtime did not stop cleanly.", errors);
    }

    private async void OnKeyboardFocus(object? sender, FocusChangedEvent change)
    {
        var coordinator = previewManager; var lifetime = activeLifetime;
        if (coordinator is null || lifetime is null || !ReferenceEquals(sender, keyboard)) return;
        try { await coordinator.OnFocusChangedAsync(change, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
        { Volatile.Write(ref lastError, "Focus update could not be confirmed."); }
    }

    private async Task WatchPreviewAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                var current = previewManager;
                if (current is null) return;
                try { await current.ReadAsync(ct); }
                catch (ObjectDisposedException) { return; }
                catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
                { Volatile.Write(ref lastError, "Preview return requires attention: " + error.Message); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // Capture the exact session so a delayed network callback cannot stop a
    // newer session after an explicit stop or subsequent restart.
    internal Func<Task> CaptureNetworkLoss()
    {
        var expected = Volatile.Read(ref activeLifetime);
        return expected is null ? () => Task.CompletedTask : () => StopForNetworkLossAsync(expected);
    }

    private async Task StopForNetworkLossAsync(CancellationTokenSource expected)
    {
        await gate.WaitAsync();
        try
        {
            if (disposed || !ReferenceEquals(activeLifetime, expected) ||
                ReferenceEquals(manualStopLifetime, expected) ||
                NetworkLossStatus != HostNetworkLossStatus.None) return;
            Volatile.Write(ref networkLossStatus, (int)HostNetworkLossStatus.Stopping);
            Volatile.Write(ref lastError, "The approved LAN interface disconnected; sharing is stopping.");
            try
            {
                await StopInsideAsync();
                Volatile.Write(ref networkLossStatus, (int)HostNetworkLossStatus.AwaitingNetworkApproval);
                Volatile.Write(ref lastError, "The approved LAN interface disconnected; sharing stopped and the network must be approved again.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref networkLossStatus, (int)HostNetworkLossStatus.CleanupUnconfirmed);
                Volatile.Write(ref lastError, "LAN disconnected and cleanup failed: " + error.Message);
            }
        }
        finally { gate.Release(); }
    }

    private async Task WatchNetworkAsync(CancellationToken ct, Func<Task> onNetworkLoss)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (lanBinding!.IsStillAvailable()) continue;
                // The watcher must return before its cleanup is awaited by StopInsideAsync.
                _ = Task.Run(onNetworkLoss);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Runtime path cannot contain links."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!disposed)
            {
                manualStopLifetime = activeLifetime;
                try { await StopInsideAsync(); disposed = true; }
                catch { Volatile.Write(ref networkLossStatus, (int)HostNetworkLossStatus.CleanupUnconfirmed); throw; }
            }
        }
        finally { gate.Release(); }
    }
}
