namespace DesktopToolbox.DeskLink;

public sealed record MultiPeerPairingPreview(string Id, string Name, string Platform, string KeyboardFingerprint, DateTimeOffset ExpiresAtUtc);

/// <summary>Separates review and explicit fingerprint confirmation before issuing a private credential.</summary>
public sealed class MultiPeerPairingCoordinator
{
    private readonly MultiDeviceCredentialVault vault;
    internal MultiDeviceCredentialVault Vault => vault;
    private readonly MultiPeerInvitation template;
    private readonly object pendingGate = new();
    private readonly SemaphoreSlim commitGate = new(1, 1);
    private DiscoveryReviewLease? inFlightLease;
    private readonly TimeProvider deadlineClock;
    private (MultiPeerPairingPreview Preview, PeerIdentityOffer Offer, DiscoveryDeadline? Deadline, DiscoveryReviewLease? Lease)? pending;

    public MultiPeerPairingCoordinator(MultiDeviceCredentialVault vault, string hostName, string host,
        int controlPort, int dataPort, int keyboardPort, string controlFingerprint)
        : this(vault, hostName, host, controlPort, dataPort, keyboardPort, controlFingerprint, TimeProvider.System) { }

    internal MultiPeerPairingCoordinator(MultiDeviceCredentialVault vault, string hostName, string host,
        int controlPort, int dataPort, int keyboardPort, string controlFingerprint, TimeProvider deadlineClock)
    {
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        this.deadlineClock = deadlineClock ?? throw new ArgumentNullException(nameof(deadlineClock));
        if (dataPort is < 1 or > 65532 || controlPort >= dataPort && controlPort < dataPort + 4 ||
            keyboardPort >= dataPort && keyboardPort < dataPort + 4 || controlPort == keyboardPort)
            throw new ArgumentException("Control, keyboard and device data ports must not overlap.");
        template = new MultiPeerInvitation
        {
            Host = host, HostName = hostName,
            PeerName = string.Equals(hostName, "temporary-peer", StringComparison.OrdinalIgnoreCase) ? "temporary-peer-alt" : "temporary-peer", Port = controlPort,
            DataPort = dataPort, KeyboardPort = keyboardPort, CertificateSha256 = controlFingerprint,
            Token = Convert.ToBase64String(new byte[32])
        };
        template.Validate();
    }

    public MultiPeerPairingPreview Review(string json, DateTimeOffset now) => ReviewCore(json, now, false, null);

    /// <summary>Review an untrusted discovery candidate while its approved network observation remains valid.</summary>
    public MultiPeerPairingPreview ReviewForDiscovery(string json, DateTimeOffset now, DiscoveryReviewLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.HasLiveNetworkOrigin) throw new InvalidOperationException("A live approved network observation is required.");
        lease.EnsureFresh();
        return ReviewCore(json, now, true, lease);
    }

#if ISOLATED_SOCKET_CONFIGURATION_TEST
    internal MultiPeerPairingPreview ReviewForDiscovery(string json, DateTimeOffset now) => ReviewCore(json, now, true, null);
    internal MultiPeerPairingPreview ReviewForDiscoveryForTest(string json, DateTimeOffset now, DiscoveryReviewLease lease)
    { lease.EnsureFresh(); return ReviewCore(json, now, true, lease); }
#endif

    private MultiPeerPairingPreview ReviewCore(string json, DateTimeOffset now, bool discovery, DiscoveryReviewLease? lease)
    {
        lock (pendingGate)
        {
            if (lease is not null && (ReferenceEquals(pending?.Lease, lease) || ReferenceEquals(inFlightLease, lease)))
                throw new InvalidOperationException("A network observation cannot be reused for another review.");
            if (inFlightLease is not null && !inFlightLease.RevokeUnlessPublicationClaimed())
                throw new InvalidOperationException("A previous confirmation is publishing; wait for its result.");
            pending?.Lease?.TryRevoke();
            pending = null;
            var offer = PeerIdentityOffer.Parse(json, now);
            if (string.Equals(offer.Name, template.HostName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Peer and host must have different names.");
            var expires = discovery && now.AddMinutes(2) < offer.ExpiresAtUtc ? now.AddMinutes(2) : offer.ExpiresAtUtc;
            var preview = new MultiPeerPairingPreview(Guid.NewGuid().ToString("N"), offer.Name, offer.Platform,
                DeskLinkFingerprint.Normalize(offer.KeyboardFingerprint), expires);
            pending = (preview, offer, discovery ? new DiscoveryDeadline(now, expires, deadlineClock) : null, lease);
            return preview;
        }
    }

    /// <summary>Cancel only the matching unconsumed review; false does not roll back an in-progress commit.</summary>
    public bool CancelReview(string previewId)
    {
        lock (pendingGate)
        {
            if (pending is not { } current || current.Preview.Id != previewId) return false;
            current.Lease?.TryRevoke(); pending = null; return true;
        }
    }

    internal void ValidateConfirmation(string previewId, string confirmedFingerprint, DateTimeOffset now)
    {
        lock (pendingGate)
        {
            var current = pending ?? throw new InvalidOperationException("Review the device identity first.");
            if (current.Preview.Id != previewId ||
                !DeskLinkFingerprint.Matches(current.Preview.KeyboardFingerprint, confirmedFingerprint))
                throw new InvalidOperationException("Device identity does not match the reviewed fingerprint.");
            current.Deadline?.Validate(now);
            current.Lease?.EnsureFresh();
            current.Offer.Validate(now);
        }
    }

    internal MultiPeerInvitation ExportTrustedInvitation(string name)
    {
        DeskflowProfile.ValidateName(name);
        var device = vault.GetTrustedDevices().SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Device trust does not exist.");
        var secret = vault.ReadTokenForEndpoint(device.Name);
        try { return template with { PeerName = device.Name, Token = Convert.ToBase64String(secret), DataPort = template.DataPort + device.Slot - 1 }; }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>
    /// Prepares but does not activate an invitation for this authenticated, mutually confirmed session.
    /// The session, runtime and coordinator must use the same identity, trust vault and approved address.
    /// </summary>
    internal async Task<PendingFrozenInvitationMetadata> PrepareFilelessAsync(PairingTlsConsentSession.DeliveryLease delivery,
        MultiPeerRuntime runtime, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery); ArgumentNullException.ThrowIfNull(runtime);
        delivery.EnsureCurrent(ct);
        var identity = delivery.Identity;
        if (!delivery.IsHost || !string.Equals(template.HostName, identity.HostName, StringComparison.Ordinal) ||
            !DeskLinkFingerprint.Matches(template.CertificateSha256, identity.HostFingerprint) ||
            !System.Net.IPAddress.TryParse(template.Host, out var templateAddress) || !templateAddress.Equals(delivery.LocalAddress))
            throw new InvalidOperationException("The confirmed session does not match the prepared host identity.");
        var binding = await runtime.RequirePairingBindingAsync(vault, ct).ConfigureAwait(false);
        if (!binding.Address.Equals(delivery.LocalAddress))
            throw new InvalidOperationException("The confirmed connection is not on the prepared host network.");
        delivery.EnsureCurrent(ct);
        await delivery.StageHostGrantAsync(vault, ct).ConfigureAwait(false);
        return await delivery.FreezeHostInvitationAsync(binding, template.Port, template.KeyboardPort, template.DataPort, ct).ConfigureAwait(false);
    }

    internal async Task<MultiPeerInvitation> ConfirmAsync(string previewId, string confirmedFingerprint,
        DateTimeOffset now, CancellationToken ct = default)
    {
        await commitGate.WaitAsync(ct);
        try
        {
            PeerIdentityOffer offer;
            DiscoveryDeadline? deadline;
            DiscoveryReviewLease? lease;
            lock (pendingGate)
            {
                ValidateConfirmation(previewId, confirmedFingerprint, now);
                var current = pending!.Value;
                offer = current.Offer; deadline = current.Deadline; lease = current.Lease;
                inFlightLease = lease;
                pending = null; // A failed persistence requires an explicit new review.
            }
            try
            {
                var grant = deadline is null
                    ? await vault.AddConfirmedWithSlotAsync(offer.Name, offer.KeyboardFingerprint, offer.Platform, ct)
                    : await vault.AddReviewedWithSlotAsync(offer.Name, offer.KeyboardFingerprint, offer.Platform,
                        () => { deadline.EnsureCurrent(); lease?.EnsureFresh(); },
                        () => { deadline.EnsureCurrent(); lease?.ClaimPublish(); }, ct);
                return template with { PeerName = offer.Name, Token = grant.Token, DataPort = template.DataPort + grant.Slot - 1 };
            }
            finally
            {
                lease?.FinishPublish();
                lock (pendingGate)
                    if (ReferenceEquals(inFlightLease, lease)) inFlightLease = null;
            }
        }
        finally { commitGate.Release(); }
    }

    private sealed class DiscoveryDeadline(DateTimeOffset start, DateTimeOffset expires, TimeProvider clock)
    {
        private readonly object gate = new();
        private readonly long timestamp = clock.GetTimestamp();
        private readonly TimeSpan duration = expires - start;
        private DateTimeOffset lastObserved = start;
        private bool invalid;
        internal void EnsureCurrent() => Validate(clock.GetUtcNow());
        internal void Validate(DateTimeOffset now)
        {
            lock (gate)
            {
                var elapsed = clock.GetElapsedTime(timestamp, clock.GetTimestamp());
                if (invalid || now < lastObserved || now >= expires || elapsed < TimeSpan.Zero || elapsed >= duration)
                { invalid = true; throw new ArgumentException("Discovery review has expired or the clock moved backwards."); }
                lastObserved = now;
            }
        }
    }
}
