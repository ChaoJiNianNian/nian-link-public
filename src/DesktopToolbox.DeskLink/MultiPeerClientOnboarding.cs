namespace DesktopToolbox.DeskLink;

public sealed record MultiPeerHostPreview(string Id, string HostName, string Host, string ControlFingerprint);

/// <summary>Reviews a private invitation for this device before saving its credential.</summary>
public sealed class MultiPeerClientOnboarding(PeerInvitationStore store, string localName)
{
    private readonly PeerInvitationStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly object sync = new();
    private readonly SemaphoreSlim commit = new(1, 1);
    private (MultiPeerHostPreview Preview, MultiPeerInvitation Invitation)? pending;

    public MultiPeerHostPreview Review(string json)
    {
        lock (sync)
        {
            pending = null;
            DeskflowProfile.ValidateName(localName);
            var invitation = MultiPeerInvitation.Parse(json);
            if (!string.Equals(invitation.PeerName, localName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This invitation belongs to a different device.");
            var preview = new MultiPeerHostPreview(Guid.NewGuid().ToString("N"), invitation.HostName,
                invitation.Host, DeskLinkFingerprint.Normalize(invitation.CertificateSha256));
            pending = (preview, invitation);
            return preview;
        }
    }

    public async Task ConfirmAsync(string previewId, string confirmedFingerprint, CancellationToken ct = default)
    {
        await commit.WaitAsync(ct);
        try
        {
            MultiPeerInvitation invitation;
            lock (sync)
            {
                var current = pending ?? throw new InvalidOperationException("Review the host identity first.");
                if (current.Preview.Id != previewId || !DeskLinkFingerprint.Matches(current.Preview.ControlFingerprint, confirmedFingerprint))
                    throw new InvalidOperationException("Host identity does not match the reviewed fingerprint.");
                invitation = current.Invitation;
                pending = null;
            }
            // A failed save requires the invitation to be reviewed again.
            await store.SaveConfirmedAsync(invitation, confirmedFingerprint, ct);
        }
        finally { commit.Release(); }
    }
}
