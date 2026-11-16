using System.Security.Cryptography;
using System.Text;

namespace DesktopToolbox.DeskLink;

/// <summary>
/// Isolated multi-device keyboard host. Its supplied engine hash and peer
/// identities must come from verified build materials and user-approved trust.
/// </summary>
public sealed class MultiPeerKeyboardSession : IMultiPeerKeyboard, IMultiPeerKeyboardFocusEvents
{
    // Hash of the isolated 0001+0002+0003+0004 Windows candidate. Release
    // packaging must update this from the audited engine it actually ships.
    public const string ExperimentalEngineSha256 = "04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA";
    private readonly string enginePath;
    private readonly string expectedHash;
    private readonly string profile;
    private readonly TrustedDeviceIdentity[] peers;
    private readonly string bindingPath;
    private readonly DeskflowSession session;
    private readonly VerifiedLanBinding? lanBinding;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public event EventHandler<FocusChangedEvent>? FocusChanged;

    public MultiPeerKeyboardSession(string enginePath, string expectedHash, string profile,
        string localName, IReadOnlyCollection<TrustedDeviceIdentity> peers,
        DeskflowSessionDependencies? dependencies = null)
        : this(enginePath, expectedHash, profile, localName, peers, null, dependencies) { }

    public static MultiPeerKeyboardSession CreateForLan(string enginePath, string expectedHash, string profile,
        string localName, IReadOnlyCollection<TrustedDeviceIdentity> peers, VerifiedLanBinding binding,
        DeskflowSessionDependencies? dependencies = null)
    {
        if (!DeskLinkFingerprint.Matches(expectedHash, ExperimentalEngineSha256))
            throw new InvalidDataException("This engine is not the audited multi-device candidate.");
        return new(enginePath, expectedHash, profile, localName, peers, binding ?? throw new ArgumentNullException(nameof(binding)), dependencies);
    }

    private MultiPeerKeyboardSession(string enginePath, string expectedHash, string profile,
        string localName, IReadOnlyCollection<TrustedDeviceIdentity> peers, VerifiedLanBinding? binding,
        DeskflowSessionDependencies? dependencies)
    {
        ArgumentNullException.ThrowIfNull(peers);
        if (peers.Count is < 1 or > MultiDeviceCredentials.MaximumPeers)
            throw new ArgumentException("One to four approved peers are required.", nameof(peers));
        _ = NativePeerBindings.ToJson(peers); // Validate all identities before any writes.
        if (!Path.IsPathFullyQualified(enginePath) || !Path.IsPathFullyQualified(profile))
            throw new ArgumentException("Engine and profile paths must be absolute.");
        this.enginePath = Path.GetFullPath(enginePath);
        this.expectedHash = DeskLinkFingerprint.Normalize(expectedHash);
        this.profile = Path.GetFullPath(profile);
        this.peers = peers.ToArray();
        lanBinding = binding;
        bindingPath = Path.Combine(this.profile, "peer-bindings.json");
        var options = new DeskflowSessionOptions
        {
            EnginePath = this.enginePath, ProfileDirectory = this.profile, LocalName = localName,
            RemoteName = this.peers[0].Name, AdditionalRemoteNames = this.peers.Skip(1).Select(x => x.Name).ToArray(),
            Role = DeskLinkRole.Server, UseNativeControl = true, ClipboardEnabled = false,
            AdvancedSettings = new Dictionary<string, string> { ["core/interface"] = binding?.Address.ToString() ?? "127.0.0.1" }
        };
        session = new DeskflowSession(options, dependencies, bindingPath);
        session.FocusChanged += ForwardFocus;
    }

    public async Task<DeskLinkState> StartAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lanBinding is not null && !lanBinding.IsStillAvailable())
                throw new InvalidOperationException("The approved LAN interface is no longer available.");
            RejectLinks(enginePath);
            // Keep the verified image open through process creation so Windows
            // cannot replace it between the hash check and start.
            await using var image = new FileStream(enginePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(image, ct));
            if (!DeskLinkFingerprint.Matches(expectedHash, actual)) throw new InvalidDataException("The multi-device keyboard engine did not match its verified hash.");
            RejectLinks(profile);
            PrivateFiles.EnsureDirectory(profile);
            var lockPath = Path.Combine(profile, ".session.lock");
            RejectLinks(lockPath);
            using (var profileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var trust = Path.Combine(profile, "tls", "trusted-clients");
                RejectLinks(bindingPath); RejectLinks(trust);
                var bindings = NativePeerBindings.ToJson(peers);
                var trusted = string.Join("\n", peers.Select(x => "v2:sha256:" + DeskLinkFingerprint.Normalize(x.Fingerprint).ToLowerInvariant())) + "\n";
                await PrivateFiles.WriteAsync(bindingPath, bindings, ct);
                await PrivateFiles.WriteAsync(trust, trusted, ct);
            }
            return await session.StartAsync(ct);
        }
        finally { gate.Release(); }
    }

    public Task<DeskLinkState> GetStateAsync(CancellationToken ct = default) => session.GetStateAsync(ct);

    public bool OwnsLiveProcess(int processId) => session.OwnsLiveProcess(processId);

    private void ForwardFocus(object? sender, FocusChangedEvent change) => FocusChanged?.Invoke(this, change);

    public async Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target, string expectedActive, long expectedRevision, CancellationToken ct = default)
    {
        var reply = await session.SwitchIfCurrentAsync(target, expectedActive, expectedRevision, ct);
        var confirmed = reply.Ok && string.Equals(reply.ActiveComputer, target, StringComparison.Ordinal);
        return new() { Ok = confirmed, Target = target, ActiveComputer = reply.ActiveComputer, InputTransferred = confirmed,
            VideoStatus = "notAttempted", Error = confirmed ? null : reply.Error ?? "Keyboard focus was not confirmed." };
    }

    public Task<DeskLinkState> StopAsync(CancellationToken ct = default) => session.StopAsync(ct);

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Keyboard profile cannot use a linked path."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) { session.FocusChanged -= ForwardFocus; await session.DisposeAsync(); disposed = true; } }
        finally { gate.Release(); }
    }
}
