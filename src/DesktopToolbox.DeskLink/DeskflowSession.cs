using System.Diagnostics;
namespace DesktopToolbox.DeskLink;

/// <summary>Owns exactly one private Deskflow child, its settings and explicit peer trust.</summary>
public sealed class DeskflowSession : IAsyncDisposable
{
    private readonly DeskflowSessionOptions options;
    private readonly DeskflowSessionDependencies dependencies;
    private readonly HashSet<string> allowedComputers;
    private readonly string? multiDeviceBindings;
    private readonly SemaphoreSlim operation = new(1,1);
    private readonly object stateLock = new();
    private DeskLinkState state = new();
    private long stateVersion;
    // The IPC stream belongs to a single child lifetime. Old callbacks must
    // never repopulate connection evidence after that lifetime is revoked.
    private long coreEpoch;
    private long peerEvidenceVersion;
    private long peerConnectionVersion;
    private DeskflowProfileInfo? profile;
    private IDeskflowOwnedProcess? process;
    private FileStream? profileLease;
    private DeskflowCoreIpcClient? coreIpc;
    private NativeControlClient? native;
    private CancellationTokenSource? running;
    private Task connections = Task.CompletedTask;
    private bool disposed;
    public event EventHandler<DeskLinkState>? StateChanged;
    public event EventHandler<FocusChangedEvent>? FocusChanged;
    public DeskflowSession(DeskflowSessionOptions options, DeskflowSessionDependencies? dependencies = null)
        : this(options, dependencies, null) { }

    internal DeskflowSession(DeskflowSessionOptions options, DeskflowSessionDependencies? dependencies, string? multiDeviceBindings)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with { AdditionalRemoteNames = options.AdditionalRemoteNames?.ToArray()
            ?? throw new ArgumentException("Remote computer names cannot be null.") };
        DeskflowProfile.Validate(options);
        // This session and its trust/preview routing remain pair-only. Do not
        // silently start a partial multi-device session from a generated profile.
        if (options.AdditionalRemoteNames.Count != 0 && multiDeviceBindings is null)
            throw new NotSupportedException("Multi-device sessions are not yet available.");
        if (multiDeviceBindings is not null && (options.Role != DeskLinkRole.Server || !options.UseNativeControl ||
            !Path.IsPathFullyQualified(multiDeviceBindings)))
            throw new ArgumentException("Invalid multi-device session boundary.");
        this.multiDeviceBindings = multiDeviceBindings;
        allowedComputers = new HashSet<string>(new[] { options.LocalName, options.RemoteName }.Concat(options.AdditionalRemoteNames), StringComparer.Ordinal);
        this.options = options; this.dependencies = dependencies ?? new();
    }
    private DeskLinkState Snapshot { get { lock(stateLock) return state; } }

    // The last committed in-memory state; unlike GetStateAsync this never performs I/O
    // or invokes observers. Consumers must still associate it with this session instance.
    public DeskLinkState CurrentState => Snapshot;
    public bool OwnsLiveProcess(int pid)
    {
        var owned = Volatile.Read(ref process);
        var current = Snapshot;
        if (pid <= 0 || disposed || owned is null || current.ProcessState != DeskLinkProcessState.Running || current.ProcessId != pid) return false;
        try { return owned.Id == pid && !owned.HasExited && ReferenceEquals(owned, Volatile.Read(ref process)); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private void Update(Func<DeskLinkState,DeskLinkState> change)
    {
        DeskLinkState value; lock(stateLock) { value = state = change(state) with { SessionStateVersion = ++stateVersion }; } StateChanged?.Invoke(this, value);
    }
    private long BeginCoreEpoch() { lock (stateLock) return ++coreEpoch; }
    private void RevokeCoreEpoch() { lock (stateLock) ++coreEpoch; }
    private void UpdateCore(long epoch, CancellationToken ct, Func<DeskLinkState, DeskLinkState> change)
    {
        DeskLinkState value;
        lock (stateLock)
        {
            if (epoch != coreEpoch || ct.IsCancellationRequested || state.ProcessState != DeskLinkProcessState.Running) return;
            var next = change(state);
            if (next.ConnectionState != state.ConnectionState) ++peerConnectionVersion;
            value = state = next with { SessionStateVersion = ++stateVersion };
        }
        StateChanged?.Invoke(this, value);
    }

    // Point-in-time proof for a peer-side control reply. This does not fence
    // commands already accepted by the remote host or its native input queue.
    internal readonly record struct PeerSwitchEvidence(long CoreEpoch, long ConnectionVersion,
        long PeerEvidenceVersion, IDeskflowOwnedProcess Process, CancellationTokenSource Lifetime);

    internal PeerSwitchEvidence CapturePeerSwitchEvidence()
    {
        lock (stateLock)
        {
            var owner = process;
            var lifetime = running;
            if (state.ProcessState != DeskLinkProcessState.Running || state.ConnectionState != DeskLinkConnectionState.Connected ||
                state.PendingPeerFingerprint is not null || owner is null || lifetime is null || lifetime.IsCancellationRequested ||
                !IsPeerProcessLive(owner))
                throw new InvalidOperationException("The keyboard connection is not confirmed.");
            return new(coreEpoch, peerConnectionVersion, peerEvidenceVersion, owner, lifetime);
        }
    }

    internal void ValidatePeerSwitchEvidence(PeerSwitchEvidence evidence)
    {
        lock (stateLock)
        {
            if (coreEpoch != evidence.CoreEpoch || peerConnectionVersion != evidence.ConnectionVersion ||
                peerEvidenceVersion != evidence.PeerEvidenceVersion || !ReferenceEquals(process, evidence.Process) ||
                !ReferenceEquals(running, evidence.Lifetime) || evidence.Lifetime.IsCancellationRequested ||
                !IsPeerProcessLive(evidence.Process) ||
                state.ProcessState != DeskLinkProcessState.Running || state.ConnectionState != DeskLinkConnectionState.Connected ||
                state.PendingPeerFingerprint is not null)
                throw new InvalidOperationException("键鼠连接已变化，操作结果尚未确认，请检查键鼠所在设备。");
        }
    }

    private static bool IsPeerProcessLive(IDeskflowOwnedProcess owner)
    {
        try { return !owner.HasExited && !owner.Completion.IsCompleted; }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        { return false; }
    }
    private void RecordLifecycleNotificationFailure(Exception error)
    {
        lock (stateLock) state = state with { ProcessState = DeskLinkProcessState.Faulted,
            ConnectionState = DeskLinkConnectionState.Disconnected, PendingPeerFingerprint = null,
            NativeControlAvailable = false, PreviewInputIsolated = false, ActiveComputer = null, ConnectedComputers = [],
            ProcessId = process is null ? null : state.ProcessId,
            LastError = "Deskflow state notification failed: " + error.Message, SessionStateVersion = ++stateVersion };
    }
    public async Task<DeskflowProfileInfo> PrepareAsync(CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        try { ThrowIfDisposed(); return await PrepareInsideAsync(ct); }
        finally { operation.Release(); }
    }
    private async Task<DeskflowProfileInfo> PrepareInsideAsync(CancellationToken ct)
    {
        if (process is { HasExited: false }) throw new InvalidOperationException("Stop this sharing session before changing its private profile.");
        if (process is not null) await ReleaseResourcesAsync();
        profile = await DeskflowProfile.PrepareAsync(options, ct);
        Update(s => s with { LocalFingerprint = profile.LocalFingerprint });
        return profile;
    }
    public async Task<DeskLinkState> StartAsync(CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (process is { HasExited: false }) return Snapshot;
            if (dependencies.HasConflictingCore(null)) throw new InvalidOperationException("Another Deskflow core is running. Exit it normally before starting integrated sharing.");
            profile = await PrepareInsideAsync(ct);
            profileLease = new FileStream(Path.Combine(options.ProfileDirectory, ".session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { Update(_ => new DeskLinkState { ProcessState = DeskLinkProcessState.Starting, ConnectionState = DeskLinkConnectionState.Connecting, LocalFingerprint = profile.LocalFingerprint }); }
            catch (Exception error)
            {
                profileLease.Dispose(); profileLease = null;
                RecordLifecycleNotificationFailure(error);
                throw;
            }
            var start = new ProcessStartInfo(options.EnginePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(options.EnginePath)!
            };
            start.ArgumentList.Add(options.Role == DeskLinkRole.Server ? "server" : "client");
            start.ArgumentList.Add("--settings"); start.ArgumentList.Add(profile.SettingsPath);
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(options.ProfileDirectory, "config");
            start.Environment["XDG_STATE_HOME"] = Path.Combine(options.ProfileDirectory, "state");
            if (options.UseNativeControl) start.Environment["DESKTOOLBOX_CONTROL"] = "1";
            else start.Environment.Remove("DESKTOOLBOX_CONTROL");
            if (multiDeviceBindings is not null) start.Environment["NIANDESKLINK_PEER_BINDINGS"] = multiDeviceBindings;
            else start.Environment.Remove("NIANDESKLINK_PEER_BINDINGS");
            running = new CancellationTokenSource();
            try { process = await dependencies.StartProcessAsync(start, ct); }
            catch { profileLease.Dispose(); profileLease = null; running.Dispose(); running = null; Update(s => s with { ProcessState = DeskLinkProcessState.Faulted }); throw; }
            var owned = process;
            try { Update(s => s with { ProcessState = DeskLinkProcessState.Running, ProcessId = owned.Id }); }
            catch (Exception error)
            {
                running.Cancel();
                try { await owned.StopAsync(CancellationToken.None); await ReleaseResourcesAsync(); }
                catch (Exception cleanup) { RecordLifecycleNotificationFailure(cleanup); throw new AggregateException(error, cleanup); }
                RecordLifecycleNotificationFailure(error);
                throw;
            }
            connections = ConnectAsync(owned, running.Token, BeginCoreEpoch());
            _ = WatchProcessAsync(owned);
            return Snapshot;
        }
        finally { operation.Release(); }
    }
    private async Task ConnectAsync(IDeskflowOwnedProcess owned, CancellationToken ct, long epoch)
    {
        await Task.WhenAll(ConnectOriginalAsync(owned, ct, epoch), options.UseNativeControl ? ConnectNativeAsync(owned, ct) : Task.CompletedTask);
    }
    private async Task<Stream> ConnectOriginalStreamAsync(IDeskflowOwnedProcess owned, CancellationToken ct) =>
        await RetryAsync(token => dependencies.ConnectCoreIpcAsync(owned.Id, token), ct);
    private async Task ConnectOriginalAsync(IDeskflowOwnedProcess owned, CancellationToken ct, long epoch)
    {
        try
        {
            var stream = await ConnectOriginalStreamAsync(owned, ct);
            if (ct.IsCancellationRequested) { await stream.DisposeAsync(); return; }
            coreIpc = new(stream);
            coreIpc.Message += (_, message) =>
            {
                // The native connection proof is revisioned; original IPC lists cannot replace it.
                if (options.UseNativeControl && message.Key == "connectedClients") return;
                if (message.Key == "peerFingerprint")
                {
                    var fingerprint = DeskLinkFingerprint.Normalize(message.Value);
                    var alreadyTrusted = IsTrusted(fingerprint);
                    UpdateCore(epoch, ct, s =>
                    {
                        ++peerEvidenceVersion;
                        return alreadyTrusted ? s
                            : DeskflowIpcState.Apply(s, message.Key, fingerprint);
                    });
                    return;
                }
                UpdateCore(epoch, ct, s => DeskflowIpcState.Apply(s, message.Key, message.Value));
            };
            coreIpc.Disconnected += (_, error) => UpdateCore(epoch, ct, s =>
            {
                ++coreEpoch; ++peerEvidenceVersion;
                return s with { ConnectionState = DeskLinkConnectionState.Disconnected,
                    PendingPeerFingerprint = null, LastError = "Deskflow status channel: " + error.Message };
            });
            await coreIpc.StartAsync(ct);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        { UpdateCore(epoch, ct, s => { ++coreEpoch; ++peerEvidenceVersion; return s with { ConnectionState = DeskLinkConnectionState.Disconnected,
            PendingPeerFingerprint = null, LastError = "Deskflow status channel unavailable: " + error.Message }; }); }
    }
    private async Task ConnectNativeAsync(IDeskflowOwnedProcess owned, CancellationToken ct)
    {
        try
        {
            native = await RetryAsync(token => dependencies.ConnectNativeAsync(owned.Id, token), ct);
            native.FocusChanged += (_, focus) =>
            {
                if (!allowedComputers.Contains(focus.ActiveComputer))
                { Update(s => s with { LastError = "Native control reported a computer outside this session." }); return; }
                if (focus.Revision < Snapshot.Revision) return;
                Update(s => focus.Revision < s.Revision ? s : s with
                {
                    ActiveComputer = focus.ActiveComputer, Revision = focus.Revision, PreviewInputIsolated = focus.PreviewInputIsolated,
                    // Focus changes do not disconnect this transport. Only its connection proof expires.
                    ConnectedComputers = focus.Revision > s.Revision ? [] : s.ConnectedComputers
                });
                FocusChanged?.Invoke(this, focus);
            };
            native.Disconnected += (sender, error) =>
            {
                if (ct.IsCancellationRequested) return;
                DeskLinkState current;
                lock (stateLock)
                {
                    if (ct.IsCancellationRequested || state.ProcessState != DeskLinkProcessState.Running ||
                        !ReferenceEquals(sender, Volatile.Read(ref native))) return;
                    current = state = state with { NativeControlAvailable = false, PreviewInputIsolated = false,
                        ConnectedComputers = [], LastError = "Native control disconnected: " + error.Message,
                        SessionStateVersion = ++stateVersion };
                }
                StateChanged?.Invoke(this, current);
            };
            await GetStateAsync(ct);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        { if (!ct.IsCancellationRequested) Update(s => s with { NativeControlAvailable = false, LastError = "Native control unavailable: " + error.Message }); }
    }
    private async Task<T> RetryAsync<T>(Func<CancellationToken,Task<T>> connect, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(dependencies.ConnectionTimeout);
        while (true)
        {
            try { return await connect(deadline.Token); }
            catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or TimeoutException)
            { await Task.Delay(100, deadline.Token); }
        }
    }
    private readonly record struct NativeReplyLease(NativeControlClient Connection, IDeskflowOwnedProcess Owner, CancellationTokenSource Lifetime);

    private NativeReplyLease? CurrentNativeLease()
    {
        lock (stateLock)
        {
            var connection = Volatile.Read(ref native);
            var owner = Volatile.Read(ref process);
            var lifetime = Volatile.Read(ref running);
            return connection is null || owner is null || lifetime is null ? null : new(connection, owner, lifetime);
        }
    }

    private (NativeReplyLease? Lease, DeskLinkState State) CaptureNativeRead()
    {
        NativeReplyLease? lease;
        DeskLinkState result;
        var publish = false;
        lock (stateLock)
        {
            var connection = Volatile.Read(ref native);
            var owner = Volatile.Read(ref process);
            var lifetime = Volatile.Read(ref running);
            lease = connection is null || owner is null || lifetime is null ? null : new(connection, owner, lifetime);
            if (lease is null && options.UseNativeControl && state.ProcessState == DeskLinkProcessState.Running &&
                (state.NativeControlAvailable || state.PreviewInputIsolated || state.ActiveComputer is not null || state.ConnectedComputers.Count != 0))
            {
                state = state with { NativeControlAvailable = false, PreviewInputIsolated = false, ActiveComputer = null,
                    ConnectedComputers = [], LastError = "Native control session is no longer available.",
                    SessionStateVersion = ++stateVersion };
                publish = true;
            }
            result = state;
        }
        if (publish) StateChanged?.Invoke(this, result);
        return (lease, result);
    }

    // Only the currently published references can be used for a reply. This
    // is a point-in-time check, not proof that a physical action was undone.
    private bool NativeLeaseValidLocked(NativeReplyLease lease)
    {
        if (!ReferenceEquals(lease.Connection, Volatile.Read(ref native)) ||
            !ReferenceEquals(lease.Owner, Volatile.Read(ref process)) ||
            !ReferenceEquals(lease.Lifetime, Volatile.Read(ref running)) ||
            lease.Lifetime.IsCancellationRequested || lease.Connection.IsDisconnected ||
            state.ProcessState != DeskLinkProcessState.Running) return false;
        try { return !lease.Owner.Completion.IsCompleted && !lease.Owner.HasExited; }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception) { return false; }
    }

    private void EnsureNativeAdmission(NativeReplyLease lease, bool requireAvailable)
    {
        DeskLinkState? result = null;
        lock (stateLock)
        {
            if (NativeLeaseValidLocked(lease) && (!requireAvailable || state.NativeControlAvailable)) return;
            if (ReferenceEquals(lease.Connection, Volatile.Read(ref native)) &&
                ReferenceEquals(lease.Owner, Volatile.Read(ref process)) &&
                ReferenceEquals(lease.Lifetime, Volatile.Read(ref running)) &&
                state.ProcessState == DeskLinkProcessState.Running)
                result = state = state with { NativeControlAvailable = false, PreviewInputIsolated = false,
                    ConnectedComputers = [], LastError = "Native control session is no longer available.",
                    SessionStateVersion = ++stateVersion };
        }
        if (result is not null) StateChanged?.Invoke(this, result);
        throw new IOException("Native control session is no longer available; keyboard result is unknown.");
    }

    private void ValidateNativeReplyNames(NativeControlReply reply)
    {
        if (reply.ActiveComputer is { } active && !allowedComputers.Contains(active) ||
            reply.ConnectedComputers.Any(name => !allowedComputers.Contains(name)))
            throw new IOException("Native control reported a computer outside this session.");
    }

    private DeskLinkState ApplyNative(NativeControlReply reply, NativeReplyLease lease, bool rejectStale)
    {
        ValidateNativeReplyNames(reply);
        DeskLinkState result;
        var invalid = false; var stale = false; var publish = false;
        lock (stateLock)
        {
            if (!NativeLeaseValidLocked(lease))
            {
                invalid = true;
                if (ReferenceEquals(lease.Connection, Volatile.Read(ref native)) &&
                    ReferenceEquals(lease.Owner, Volatile.Read(ref process)) &&
                    ReferenceEquals(lease.Lifetime, Volatile.Read(ref running)) &&
                    state.ProcessState == DeskLinkProcessState.Running)
                {
                    state = state with { NativeControlAvailable = false, PreviewInputIsolated = false,
                        ConnectedComputers = [], LastError = "Native control session changed; the keyboard action result is unknown.",
                        SessionStateVersion = ++stateVersion };
                    publish = true;
                }
                result = state;
            }
            else if (reply.Revision < state.Revision)
            {
                stale = rejectStale;
                result = state;
            }
            else
            {
                result = state = state with
                {
                    NativeControlAvailable = true, PreviewInputIsolated = reply.PreviewInputIsolated,
                    ActiveComputer = reply.ActiveComputer, ConnectedComputers = reply.ConnectedComputers,
                    Revision = reply.Revision, LastError = reply.Ok ? state.LastError : reply.Error,
                    SessionStateVersion = ++stateVersion
                };
                publish = true;
            }
        }
        if (publish) StateChanged?.Invoke(this, result);
        if (invalid) throw new IOException("Native control session changed; the keyboard action result is unknown.");
        if (stale) throw new IOException("Native keyboard action result is no longer current.");
        return result;
    }

    private void VerifyNativeRejectionStillCurrent(NativeReplyLease lease)
    {
        lock (stateLock)
        {
            if (!NativeLeaseValidLocked(lease) || !state.NativeControlAvailable)
                throw new IOException("Native keyboard rejection is no longer confirmed.");
        }
    }

    private void VerifyNativeSwitchStillCurrent(NativeReplyLease lease, NativeControlReply reply)
    {
        lock (stateLock)
        {
            if (!NativeLeaseValidLocked(lease) || !state.NativeControlAvailable ||
                state.Revision != reply.Revision || state.ActiveComputer != reply.ActiveComputer)
                throw new IOException("Native keyboard action result is no longer confirmed.");
        }
    }

    private DeskLinkState ClearNativeReadFailure(NativeReplyLease lease, string error)
    {
        DeskLinkState result;
        lock (stateLock)
        {
            if (!ReferenceEquals(lease.Connection, Volatile.Read(ref native)) ||
                !ReferenceEquals(lease.Owner, Volatile.Read(ref process)) ||
                !ReferenceEquals(lease.Lifetime, Volatile.Read(ref running)))
                throw new IOException("Native control session changed while reading state.");
            if (state.ProcessState != DeskLinkProcessState.Running)
                return state;
            result = state = state with { NativeControlAvailable = false, PreviewInputIsolated = false,
                ConnectedComputers = [], LastError = error, SessionStateVersion = ++stateVersion };
        }
        StateChanged?.Invoke(this, result);
        return result;
    }

    public async Task<DeskLinkState> GetStateAsync(CancellationToken ct = default)
    {
        var (captured, currentState) = CaptureNativeRead();
        if (captured is not { } lease || currentState.ProcessState != DeskLinkProcessState.Running) return currentState;
        try
        {
            EnsureNativeAdmission(lease, false);
            // A newer focus may overtake a completed reply. Re-read at most twice.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reply = await lease.Connection.GetStateAsync(ct);
                ct.ThrowIfCancellationRequested();
                ApplyNative(reply, lease, false);
                DeskLinkState? current;
                lock (stateLock) current = NativeLeaseValidLocked(lease) ? state : null;
                if (current is null) return ClearNativeReadFailure(lease, "Native control state is no longer current.");
                if (reply.Ok && current.NativeControlAvailable && current.Revision == reply.Revision) return current;
                if (!reply.Ok) break;
            }
            return ClearNativeReadFailure(lease, "Native control state changed before its connection status could be confirmed.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested) throw;
            return ClearNativeReadFailure(lease, error.Message);
        }
    }
    public async Task<NativeControlReply> SwitchAsync(string target, CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(target);
        if (!allowedComputers.Contains(target)) throw new ArgumentException("Target is outside this session.", nameof(target));
        if (!options.UseNativeControl) throw new NotSupportedException("This client core cannot change server focus. Use the paired Windows control channel.");
        var captured = CurrentNativeLease();
        if (captured is not { } lease || !Snapshot.NativeControlAvailable) throw new InvalidOperationException("Native input control is not ready.");
        EnsureNativeAdmission(lease, true);
        var reply = await lease.Connection.SwitchAsync(target, ct);
        if (!reply.Ok)
        {
            ApplyNative(reply, lease, false);
            VerifyNativeRejectionStillCurrent(lease);
            return reply;
        }
        ApplyNative(reply, lease, true);
        VerifyNativeSwitchStillCurrent(lease, reply);
        return reply;
    }
    internal async Task<NativeControlReply> SwitchIfCurrentAsync(string target, string expectedActive, long expectedRevision, CancellationToken ct)
    {
        DeskflowProfile.ValidateName(target);
        if (!allowedComputers.Contains(target) || !allowedComputers.Contains(expectedActive))
            throw new ArgumentException("Target or expected owner is outside this session.");
        var captured = CurrentNativeLease();
        if (multiDeviceBindings is null || captured is not { } lease || !Snapshot.NativeControlAvailable)
            throw new InvalidOperationException("Conditional multi-device control is not ready.");
        EnsureNativeAdmission(lease, true);
        var reply = await lease.Connection.SwitchIfCurrentAsync(target, expectedActive, expectedRevision, ct);
        if (!reply.Ok)
        {
            ApplyNative(reply, lease, false);
            VerifyNativeRejectionStillCurrent(lease);
            return reply;
        }
        ApplyNative(reply, lease, true);
        VerifyNativeSwitchStillCurrent(lease, reply);
        return reply;
    }
    public async Task ConfirmPeerAsync(string fingerprint, CancellationToken ct = default)
    {
        var normalized = DeskLinkFingerprint.Normalize(fingerprint);
        await operation.WaitAsync(ct);
        string? staged = null;
        try
        {
            ThrowIfDisposed();
            long epoch, evidence;
            lock (stateLock)
            {
                if (profile is null || state.ProcessState != DeskLinkProcessState.Running ||
                    state.PendingPeerFingerprint is not { } presented || !DeskLinkFingerprint.Matches(presented, normalized))
                    throw new InvalidOperationException("Only the fingerprint currently presented by this session can be explicitly trusted.");
                epoch = coreEpoch; evidence = peerEvidenceVersion;
            }
            var path = TrustPath;
            var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
            if (existing.Length > 1024*1024) throw new InvalidDataException("Trust database is unexpectedly large.");
            var line = "v2:sha256:" + normalized.ToLowerInvariant();
            if (!existing.Split('\n').Any(value => value.Trim() == line))
            {
                staged = path + "." + Guid.NewGuid().ToString("N") + ".pending";
                await dependencies.WritePeerTrustAsync(staged, existing.TrimEnd() + (existing.Length > 0 ? "\n" : "") + line + "\n", ct);
            }
            ct.ThrowIfCancellationRequested();
            DeskLinkState result;
            lock (stateLock)
            {
                ct.ThrowIfCancellationRequested();
                if (epoch != coreEpoch || evidence != peerEvidenceVersion || state.ProcessState != DeskLinkProcessState.Running ||
                    running is null || running.IsCancellationRequested || process is null || process.HasExited ||
                    state.PendingPeerFingerprint is not { } current || !DeskLinkFingerprint.Matches(current, normalized))
                    throw new InvalidOperationException("The presented fingerprint is no longer current; verify the connection again.");
                // Only the final replacement is inside this lock: IPC evidence changes
                // and the durable trust commit have a single ordering point.
                if (staged is not null) File.Move(staged, path, true);
                result = state = state with { PendingPeerFingerprint = null, SessionStateVersion = ++stateVersion };
            }
            StateChanged?.Invoke(this, result);
        }
        finally
        {
            try { if (staged is not null && File.Exists(staged)) File.Delete(staged); }
            finally { operation.Release(); }
        }
    }
    public async Task RejectPeerAsync(CancellationToken ct = default)
    {
        await StopAsync(ct); Update(s => s with { PendingPeerFingerprint = null });
    }
    private string TrustPath => Path.Combine(profile!.TrustDirectory, options.Role == DeskLinkRole.Server ? "trusted-clients" : "trusted-servers");
    private bool IsTrusted(string normalized) => profile is not null && File.Exists(TrustPath) && File.ReadLines(TrustPath).Any(line => line.Equals("v2:sha256:" + normalized.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
    private async Task WatchProcessAsync(IDeskflowOwnedProcess owned)
    {
        var exitCode = await owned.Completion;
        await operation.WaitAsync();
        try
        {
            if (!ReferenceEquals(process, owned)) return;
            RevokeCoreEpoch();
            running?.Cancel(); profileLease?.Dispose(); profileLease = null;
            try { Update(s => s with { ProcessState = exitCode == 0 ? DeskLinkProcessState.Stopped : DeskLinkProcessState.Faulted, ConnectionState = DeskLinkConnectionState.Disconnected, NativeControlAvailable = false, ActiveComputer = null, ConnectedComputers = [], ProcessId = null, PendingPeerFingerprint = null, LastError = exitCode == 0 ? s.LastError : "Deskflow core exited with code " + exitCode }); }
            catch (Exception error) { RecordLifecycleNotificationFailure(error); }
        }
        finally { operation.Release(); }
    }
    private async Task ReleaseResourcesAsync()
    {
        running?.Cancel();
        var errors = new List<Exception>();
        try { await connections; } catch (OperationCanceledException) { } catch (Exception error) { errors.Add(error); }
        if (native is not null) { try { await native.DisposeAsync(); } catch (Exception error) { errors.Add(error); } finally { native = null; } }
        if (coreIpc is not null) { try { await coreIpc.DisposeAsync(); } catch (Exception error) { errors.Add(error); } finally { coreIpc = null; } }
        if (process is not null) { try { await process.DisposeAsync(); } catch (Exception error) { errors.Add(error); } finally { process = null; } }
        try { running?.Dispose(); } catch (Exception error) { errors.Add(error); } finally { running = null; }
        try { profileLease?.Dispose(); } catch (Exception error) { errors.Add(error); } finally { profileLease = null; }
        connections = Task.CompletedTask;
        if (errors.Count != 0) throw new AggregateException("Deskflow session cleanup failed.", errors);
    }
    public async Task<DeskLinkState> StopAsync(CancellationToken ct = default)
    {
        await operation.WaitAsync(ct);
        try
        {
            RevokeCoreEpoch();
            Exception? stoppingNotification = null;
            try { Update(s => s with { ProcessState = DeskLinkProcessState.Stopping }); }
            catch (Exception error) { stoppingNotification = error; }
            running?.Cancel();
            // Once stopping has begun, finish owned-child cleanup even if a UI request is cancelled.
            if (process is { HasExited: false })
            {
                if (coreIpc is not null)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await coreIpc.SendAsync("stop", deadline.Token); await process.Completion.WaitAsync(deadline.Token); }
                    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                }
                if (!process.HasExited) await process.StopAsync(CancellationToken.None);
            }
            try { await ReleaseResourcesAsync(); }
            catch (Exception error)
            {
                RecordLifecycleNotificationFailure(stoppingNotification is null ? error : new AggregateException(stoppingNotification, error));
                throw;
            }
            if (stoppingNotification is not null)
            {
                RecordLifecycleNotificationFailure(stoppingNotification);
                throw new InvalidOperationException("Deskflow stop completed, but a state observer failed.", stoppingNotification);
            }
            Update(s => s with { ProcessState = DeskLinkProcessState.Stopped, ConnectionState = DeskLinkConnectionState.Disconnected, NativeControlAvailable = false, ActiveComputer = null, ConnectedComputers = [], ProcessId = null, PendingPeerFingerprint = null });
            return Snapshot;
        }
        finally { operation.Release(); }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public async ValueTask DisposeAsync() { if (disposed) return; await StopAsync(); disposed = true; }
}
