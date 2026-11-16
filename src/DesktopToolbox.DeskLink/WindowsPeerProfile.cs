namespace DesktopToolbox.DeskLink;

/// <summary>
/// Builds an isolated Deskflow client configuration for an approved Windows
/// peer. The TLS control certificate is not keyboard trust: the Deskflow
/// fingerprint still needs its own confirmation through DeskflowSession.
/// </summary>
public static class MultiPeerClientProfile
{
    public static DeskflowSessionOptions Create(string profileDirectory, string enginePath,
        MultiPeerInvitation invitation, bool userApproved)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        invitation.Validate();
        if (!userApproved) throw new InvalidOperationException("The user must approve this device before connecting.");
        var options = new DeskflowSessionOptions
        {
            EnginePath = enginePath,
            ProfileDirectory = profileDirectory,
            LocalName = invitation.PeerName,
            RemoteName = invitation.HostName,
            ServerAddress = invitation.Host,
            Port = invitation.KeyboardPort,
            Role = DeskLinkRole.Client,
            UseNativeControl = false,
            ClipboardEnabled = false
        };
        DeskflowProfile.Validate(options);
        return options;
    }
}

public static class WindowsPeerProfile
{
    public static DeskflowSessionOptions Create(string profileDirectory, string enginePath,
        MultiPeerInvitation invitation, bool userApproved) =>
        MultiPeerClientProfile.Create(profileDirectory, enginePath, invitation, userApproved);
}
