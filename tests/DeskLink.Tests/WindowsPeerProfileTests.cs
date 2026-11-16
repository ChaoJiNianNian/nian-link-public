using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class WindowsPeerProfileTests
{
    [Fact]
    public async Task ApprovedWindowsPeerUsesClientRoleAndNeverAutomaticallyTrustsKeyboardServer()
    {
        using var folder = new TestFolder();
        var invitation = Invitation();
        Assert.Throws<InvalidOperationException>(() =>
            WindowsPeerProfile.Create(folder.Path, System.IO.Path.Combine(folder.Path, "deskflow-core.exe"), invitation, false));
        var options = WindowsPeerProfile.Create(folder.Path, System.IO.Path.Combine(folder.Path, "deskflow-core.exe"), invitation, true);
        Assert.Equal(DeskLinkRole.Client, options.Role);
        Assert.Equal("Win-Two", options.LocalName);
        Assert.Equal("Win-PC", options.RemoteName);
        Assert.Equal(invitation.Host, options.ServerAddress);
        Assert.Equal(invitation.KeyboardPort, options.Port);
        Assert.False(options.UseNativeControl);
        Assert.False(options.ClipboardEnabled);
        var profile = await DeskflowProfile.PrepareAsync(options);
        var text = await File.ReadAllTextAsync(profile.SettingsPath);
        Assert.Contains("checkPeerFingerprints=true", text);
        Assert.Contains("remoteHost=\"127.0.0.1\"", text);
        Assert.False(File.Exists(System.IO.Path.Combine(profile.TrustDirectory, "trusted-servers")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void RejectsInvalidKeyboardPort(int port) =>
        Assert.Throws<ArgumentException>(() => (Invitation() with { KeyboardPort = port }).Validate());

    private static MultiPeerInvitation Invitation() => new()
    {
        Host = "127.0.0.1", Port = 24801, KeyboardPort = 24800, HostName = "Win-PC", PeerName = "Win-Two",
        CertificateSha256 = new string('1', 64), Token = Convert.ToBase64String(new byte[32])
    };
}
