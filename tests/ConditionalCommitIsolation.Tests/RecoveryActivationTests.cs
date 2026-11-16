using DesktopToolbox.DeskLink;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task RecoveryRequiresBothFreshConfirmationsAndOnlyActivatesCurrentTestProcess()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path)));
        var controls = new RecoveryActivationControls();
        using var hostCert = PrivateFiles.LoadTlsCertificate(c.Case.Fixture.HostCertificatePath);
        using var peerCert = PrivateFiles.LoadTlsCertificate(c.Case.Fixture.PeerCertificatePath);
        var recover = RecoveryActivationExperiment.RunAsync(c.Case.Host, c.Case.Peer, hostCert, peerCert, c.Now, controls, () => c.Now);
        await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(recover.IsCompleted); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        controls.ConfirmHost(); await controls.HostConfirmationSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(recover.IsCompleted); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        controls.ConfirmPeer();
        Assert.Equal("test-process-recovered", await recover.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(c.Case.Candidate == c.Case.Peer.Get());
        var record = Path.Combine(c.Case.PeerRoot, "peer-recovery.bin"); Assert.True(File.Exists(record));
        for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
        await c.Case.Peer.DisposeAsync(); c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, c.Case.Protector);
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Case.Peer.SaveConfirmedAsync(c.Case.Old, c.Case.Fixture.Identity.HostFingerprint));
    }

    [Fact]
    public async Task DirectAuthorizedFrameWithoutCurrentGrantCannotActivatePeer()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryActivationControls { BeforeHostAuthorization = barrier };
        using var host = PrivateFiles.LoadTlsCertificate(c.Case.Fixture.HostCertificatePath); using var peer = PrivateFiles.LoadTlsCertificate(c.Case.Fixture.PeerCertificatePath);
        var run = RecoveryActivationExperiment.RunAsync(c.Case.Host, c.Case.Peer, host, peer, c.Now, controls, () => c.Now);
        try
        {
            await Task.WhenAll(controls.HostAwaitingConfirmation.Task, controls.PeerAwaitingConfirmation.Task).WaitAsync(TimeSpan.FromSeconds(5));
            controls.ConfirmHost(); controls.ConfirmPeer(); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await c.Case.Host.RevokeAsync(c.Case.Fixture.Identity.PeerName));
            var session = controls.HostSessionForTest ?? throw new InvalidOperationException();
            await Assert.ThrowsAsync<InvalidOperationException>(() => RecoveryActivationProtocol.WriteAsync(session, "recovery-authorized", "host", session.PeerChallenge, CancellationToken.None));
            barrier.Release(); Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { barrier.Release(); try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
    }
}
