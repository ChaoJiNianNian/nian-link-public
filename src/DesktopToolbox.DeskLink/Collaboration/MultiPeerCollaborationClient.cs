namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Uses the existing authenticated data protocol for one approved device.</summary>
public static class MultiPeerCollaborationClient
{
    public static CollaborationClient Open(MultiPeerInvitation invitation, bool userApproved)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        invitation.Validate();
        if (!userApproved) throw new InvalidOperationException("The user must approve this device first.");
        var bundle = new DeskLinkPairingBundle
        {
            Host = invitation.Host,
            Port = invitation.Port,
            DataPort = invitation.DataPort,
            CertificateSha256 = invitation.CertificateSha256,
            Token = invitation.Token,
            WindowsName = invitation.HostName,
            // Legacy protocol names this generic peer field MacName; it is
            // used only for data protocol validation, not OS identification.
            MacName = invitation.PeerName
        };
        return new CollaborationClient(bundle, userApproved: true, dataPort: invitation.DataPort);
    }
}
