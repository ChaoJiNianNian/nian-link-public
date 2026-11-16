using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Theory]
    [InlineData(RecoveryAuthorizationFault.Role)]
    [InlineData(RecoveryAuthorizationFault.Type)]
    [InlineData(RecoveryAuthorizationFault.Version)]
    [InlineData(RecoveryAuthorizationFault.RecoveryId)]
    [InlineData(RecoveryAuthorizationFault.RequestId)]
    [InlineData(RecoveryAuthorizationFault.HostChallenge)]
    [InlineData(RecoveryAuthorizationFault.PeerChallenge)]
    [InlineData(RecoveryAuthorizationFault.Expiry)]
    [InlineData(RecoveryAuthorizationFault.Identity)]
    [InlineData(RecoveryAuthorizationFault.Offer)]
    [InlineData(RecoveryAuthorizationFault.Stage)]
    [InlineData(RecoveryAuthorizationFault.Slot)]
    [InlineData(RecoveryAuthorizationFault.DataPortBase)]
    [InlineData(RecoveryAuthorizationFault.Host)]
    [InlineData(RecoveryAuthorizationFault.ControlPort)]
    [InlineData(RecoveryAuthorizationFault.KeyboardPort)]
    [InlineData(RecoveryAuthorizationFault.DataPort)]
    [InlineData(RecoveryAuthorizationFault.Digest)]
    [InlineData(RecoveryAuthorizationFault.Missing)]
    [InlineData(RecoveryAuthorizationFault.Extra)]
    [InlineData(RecoveryAuthorizationFault.CaseCollision)]
    [InlineData(RecoveryAuthorizationFault.Duplicate)]
    [InlineData(RecoveryAuthorizationFault.InvalidUtf8)]
    [InlineData(RecoveryAuthorizationFault.Oversize)]
    [InlineData(RecoveryAuthorizationFault.Truncated)]
    public async Task MalformedAuthenticatedRecoveryAuthorizationCannotActivatePeer(RecoveryAuthorizationFault fault)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryActivationControls { AuthorizationFault = fault, BeforePeerAuthorizationRead = barrier,
            AuthorizationWritten = new(TaskCreationOptions.RunContinuationsAsynchronously), AuthorizationPartialWritten = new(TaskCreationOptions.RunContinuationsAsynchronously),
            PeerAuthorizationRejected = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path))); var run = RunActivationAsync(c, controls);
        try
        {
            await ConfirmRecoveryAsync(controls, run); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            if (fault == RecoveryAuthorizationFault.Truncated) await controls.AuthorizationPartialWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else await controls.AuthorizationWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            barrier.Release(); Assert.Equal(fault == RecoveryAuthorizationFault.Truncated ? "eof" : "invalid", await controls.PeerAuthorizationRejected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
            Assert.False(File.Exists(Path.Combine(c.Case.PeerRoot, "peer-recovery.bin")));
            for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task OldAuthorizationFromAnotherFreshMutualTlsConnectionCannotActivate()
    {
        await using var a = await PromotedRecoveryCase.OpenAsync();
        var pending = await File.ReadAllBytesAsync(Path.Combine(a.Case.HostRoot, "pending-grant.bin"));
        await using var b = await PromotedRecoveryCase.OpenAsync(a.Case, pending); byte[]? first = null, second = null;
        var aControls = new RecoveryActivationControls { CaptureAuthorization = true };
        var aRun = RunActivationAsync(a, aControls); await ConfirmRecoveryAsync(aControls, aRun);
        Assert.Equal("test-process-recovered", await aRun.WaitAsync(TimeSpan.FromSeconds(5))); first = aControls.CapturedAuthorization ?? throw new InvalidOperationException();
        var barrier = new ControlledLivePublishBarrier();
        var bControls = new RecoveryActivationControls { ReplayAuthorization = first, CaptureAuthorization = true, BeforePeerAuthorizationRead = barrier,
            AuthorizationWritten = new(TaskCreationOptions.RunContinuationsAsynchronously), PeerAuthorizationRejected = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            var bRun = RunActivationAsync(b, bControls); await ConfirmRecoveryAsync(bControls, bRun);
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await bControls.AuthorizationWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(bControls.ReplayInjected); barrier.Release();
            Assert.Equal("invalid", await bControls.PeerAuthorizationRejected.Task.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Null(await bRun.WaitAsync(TimeSpan.FromSeconds(5)));
            second = bControls.CapturedAuthorization ?? throw new InvalidOperationException();
            using var firstJson = JsonDocument.Parse(first); using var secondJson = JsonDocument.Parse(second);
            foreach (var field in new[] { "identity", "offer", "stageId", "slot", "dataPortBase", "host", "controlPort", "keyboardPort", "dataPort", "invitationDigest" })
                Assert.True(firstJson.RootElement.GetProperty(field).GetRawText() == secondJson.RootElement.GetProperty(field).GetRawText());
            foreach (var field in new[] { "recoveryId", "requestId", "hostChallenge", "peerChallenge" })
                Assert.True(firstJson.RootElement.GetProperty(field).GetRawText() != secondJson.RootElement.GetProperty(field).GetRawText());
            Assert.Throws<InvalidOperationException>(() => b.Case.Peer.Get());
        }
        finally { barrier.Release(); if (first is not null) CryptographicOperations.ZeroMemory(first); if (second is not null) CryptographicOperations.ZeroMemory(second); }
    }
}
