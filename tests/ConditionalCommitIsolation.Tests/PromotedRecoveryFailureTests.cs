using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private sealed class PromotedRecoveryCase : IAsyncDisposable
    {
        internal AppliedCase Case = null!;
        internal DateTimeOffset Now;
        internal string[] Files => [Path.Combine(Case.HostRoot, "multi-device-trust.bin"), Path.Combine(Case.HostRoot, "pending-grant.bin"), Path.Combine(Case.HostRoot, "pending-prepare-unknown.bin"),
            Case.ActivePath, Case.StagePath, Case.PromotionPath];
        internal static async Task<PromotedRecoveryCase> OpenAsync(AppliedCase? sameIdentity = null, byte[]? clonedPending = null)
        {
            var result = new PromotedRecoveryCase();
            try
            {
                result.Case = await AppliedCase.OpenAsync(sameIdentity: sameIdentity, clonedPending: clonedPending); var c = result.Case; result.Now = c.Fixture.Clock();
                await c.Fixture.Peer.PromoteAndSendAppliedTestAsync(c.Peer, c.Ticket).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("applied-observed", await c.Observation.WaitAsync(TimeSpan.FromSeconds(5)));
                await c.Fixture.Host.DisposeAsync(); await c.Fixture.Peer.DisposeAsync();
                await c.Host.DisposeAsync(); await c.Peer.DisposeAsync();
                c.Host = await MultiDeviceCredentialVault.OpenAsync(c.HostRoot, c.Protector);
                c.Peer = await PeerInvitationStore.OpenAsync(c.PeerRoot, c.Protector);
                return result;
            }
            catch { if (result.Case is not null) await result.Case.DisposeAsync(); throw; }
        }
        internal async Task<IsolatedRecoveryObservation?> QueryAsync(DateTimeOffset? now = null, RecoveryQueryControls? controls = null, CancellationToken ct = default)
        {
            var at = now ?? Now; using var host = PrivateFiles.LoadTlsCertificate(Case.Fixture.HostCertificatePath); using var peer = PrivateFiles.LoadTlsCertificate(Case.Fixture.PeerCertificatePath);
            return await IsolatedRecoveryQuery.RunPromotedAsync(Case.Host, Case.Peer, host, peer, at, controls, () => at, ct);
        }
        public async ValueTask DisposeAsync() { if (Case is not null) await Case.DisposeAsync(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CorruptAnyPromotedRecoveryFileIsUnknownAndPreserved(int fileIndex)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var file = c.Files[fileIndex]; byte[] corrupt = [1, 2, 3];
        await File.WriteAllBytesAsync(file, corrupt); var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path)));
        Assert.Null(await c.QueryAsync());
        for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("offer")]
    [InlineData("v3-digest")]
    [InlineData("base-digest")]
    [InlineData("invitation")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("version")]
    public async Task MutatedPromotionRecordCannotProduceReadOnlyObservation(string defect)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var path = c.Case.PromotionPath; var plain = c.Case.Protector.Unprotect(await File.ReadAllBytesAsync(path));
        try
        {
            var node = JsonNode.Parse(plain)!;
            switch (defect)
            {
                case "stage": node["stageId"] = Guid.NewGuid(); break;
                case "offer": node["offer"]!["platform"] = "mac"; break;
                case "v3-digest": node["v3CipherDigest"] = new string('A', 64); break;
                case "base-digest": node["baseCipherDigest"] = new string('A', 64); break;
                case "invitation": node["invitation"]!["port"] = 29999; break;
                case "missing": node.AsObject().Remove("stageId"); break;
                case "extra": node["unexpected"] = true; break;
                case "version": node["version"] = 2; break;
            }
            var altered = JsonSerializer.SerializeToUtf8Bytes(node);
            try { await File.WriteAllBytesAsync(path, c.Case.Protector.Protect(altered)); }
            finally { CryptographicOperations.ZeroMemory(altered); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var before = await File.ReadAllBytesAsync(path); Assert.Null(await c.QueryAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrSameNameReplacementCannotMatchPromotedRecovery(bool replace)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var original = c.Case.Candidate.Token;
        Assert.True(await c.Case.Host.RevokeAsync(c.Case.Fixture.Identity.PeerName));
        if (replace)
        {
            var replacement = await c.Case.Host.AddConfirmedWithSlotAsync(c.Case.Fixture.Identity.PeerName, c.Case.Fixture.Identity.PeerFingerprint, c.Case.Fixture.Offer.Platform);
            Assert.True(c.Case.Host.IsAuthorized(c.Case.Fixture.Identity.PeerName, replacement.Token));
        }
        var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path)));
        Assert.Null(await c.QueryAsync()); Assert.False(c.Case.Host.IsAuthorized(c.Case.Fixture.Identity.PeerName, original));
        for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task PromotedRecoveryRequiresOriginalCertificatePinsAndReportsExpiredHistory()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); await using var wrong = await Fixture.OpenAsync();
        using var host = PrivateFiles.LoadTlsCertificate(c.Case.Fixture.HostCertificatePath); using var peer = PrivateFiles.LoadTlsCertificate(c.Case.Fixture.PeerCertificatePath);
        using var wrongHost = PrivateFiles.LoadTlsCertificate(wrong.HostCertificatePath); using var wrongPeer = PrivateFiles.LoadTlsCertificate(wrong.PeerCertificatePath);
        Assert.Null(await IsolatedRecoveryQuery.RunPromotedAsync(c.Case.Host, c.Case.Peer, wrongHost, peer, c.Now, clock: () => c.Now));
        Assert.Null(await IsolatedRecoveryQuery.RunPromotedAsync(c.Case.Host, c.Case.Peer, host, wrongPeer, c.Now, clock: () => c.Now));
        var expired = c.Case.Fixture.Identity.ExpiresAtUtc.AddSeconds(1); var result = await c.QueryAsync(expired);
        Assert.NotNull(result); Assert.True(result.HistoryExpired); Assert.Equal("promotion-current-grant", result.Scope);
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task PromotedRecoveryCannotReplayStateAcrossFreshTlsConnections()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var first = new RecoveryQueryControls { CaptureState = true };
        Assert.NotNull(await c.QueryAsync(controls: first)); var captured = first.CapturedState ?? throw new InvalidOperationException();
        var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryQueryControls { ReplayState = captured, CaptureGeneratedState = true, BeforeHostStateRead = barrier,
            StateWritten = new(TaskCreationOptions.RunContinuationsAsynchronously), HostStateRejected = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            var query = c.QueryAsync(controls: controls);
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); await controls.StateWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(controls.ReplayInjected); barrier.Release();
            Assert.Equal("invalid", await controls.HostStateRejected.Task.WaitAsync(TimeSpan.FromSeconds(5))); Assert.Null(await query.WaitAsync(TimeSpan.FromSeconds(5)));
            var generated = controls.GeneratedState ?? throw new InvalidOperationException();
            try
            {
                using var a = JsonDocument.Parse(captured); using var b = JsonDocument.Parse(generated);
                Assert.True(a.RootElement.GetProperty("recoveryId").GetString() != b.RootElement.GetProperty("recoveryId").GetString());
                Assert.True(a.RootElement.GetProperty("requestId").GetString() != b.RootElement.GetProperty("requestId").GetString());
                Assert.True(a.RootElement.GetProperty("hostChallenge").GetString() != b.RootElement.GetProperty("hostChallenge").GetString());
                Assert.True(a.RootElement.GetProperty("peerChallenge").GetString() != b.RootElement.GetProperty("peerChallenge").GetString());
                foreach (var field in new[] { "identity", "offer", "stageId", "slot", "dataPortBase", "host", "controlPort", "keyboardPort", "dataPort", "invitationDigest", "state" })
                    Assert.True(a.RootElement.GetProperty(field).GetRawText() == b.RootElement.GetProperty(field).GetRawText());
            }
            finally { CryptographicOperations.ZeroMemory(generated); }
        }
        finally { barrier.Release(); CryptographicOperations.ZeroMemory(captured); }
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromotedRecoveryRejectsChangesBeforeEitherSideFinalCheck(bool peerChanges)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var controls = peerChanges ? new RecoveryQueryControls { BeforePeerState = barrier } : new RecoveryQueryControls { BeforeHostFinal = barrier };
        var query = c.QueryAsync(controls: controls);
        try
        {
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            if (peerChanges) await File.WriteAllBytesAsync(c.Case.StagePath, [4, 5, 6]);
            else Assert.True(await c.Case.Host.RevokeAsync(c.Case.Fixture.Identity.PeerName));
            barrier.Release(); Assert.Null(await query.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { barrier.Release(); }
    }

    [Fact]
    public async Task LegacyMarkerCannotProveOriginalPromotedBinding()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var path = c.Files[2]; var plain = c.Case.Protector.Unprotect(await File.ReadAllBytesAsync(path));
        try
        {
            var node = JsonNode.Parse(plain)!; node["version"] = 1; node.AsObject().Remove("offer"); node.AsObject().Remove("pendingCiphertextDigest");
            var changed = JsonSerializer.SerializeToUtf8Bytes(node);
            try { await File.WriteAllBytesAsync(path, c.Case.Protector.Protect(changed)); }
            finally { CryptographicOperations.ZeroMemory(changed); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var before = await File.ReadAllBytesAsync(path); Assert.Null(await c.QueryAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnavailablePromotedRecoveryProtectorIsUnknown(bool hostSide)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var faulty = new RecoveryFaultProtector();
        if (hostSide)
        {
            await c.Case.Host.DisposeAsync(); c.Case.Host = await MultiDeviceCredentialVault.OpenAsync(c.Case.HostRoot, faulty);
        }
        else
        {
            await c.Case.Peer.DisposeAsync(); c.Case.Peer = await PeerInvitationStore.OpenAsync(c.Case.PeerRoot, faulty);
        }
        var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path))); faulty.Unavailable = true;
        Assert.Null(await c.QueryAsync());
        for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ReprotectedBoundCiphertextCannotMatchPromotedRecovery(int fileIndex)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var path = c.Files[fileIndex]; var plain = c.Case.Protector.Unprotect(await File.ReadAllBytesAsync(path));
        try { await File.WriteAllBytesAsync(path, c.Case.Protector.Protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var before = await File.ReadAllBytesAsync(path); Assert.Null(await c.QueryAsync()); Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Theory]
    [InlineData(RecoveryTestFault.WrongRole)]
    [InlineData(RecoveryTestFault.WrongVersion)]
    [InlineData(RecoveryTestFault.WrongChallenge)]
    [InlineData(RecoveryTestFault.WrongSession)]
    [InlineData(RecoveryTestFault.WrongNonce)]
    [InlineData(RecoveryTestFault.WrongFingerprint)]
    [InlineData(RecoveryTestFault.WrongStage)]
    [InlineData(RecoveryTestFault.WrongPlatform)]
    [InlineData(RecoveryTestFault.WrongOfferExpiry)]
    [InlineData(RecoveryTestFault.WrongSlot)]
    [InlineData(RecoveryTestFault.WrongPort)]
    [InlineData(RecoveryTestFault.WrongDigest)]
    [InlineData(RecoveryTestFault.Missing)]
    [InlineData(RecoveryTestFault.Extra)]
    [InlineData(RecoveryTestFault.CaseCollision)]
    [InlineData(RecoveryTestFault.Duplicate)]
    [InlineData(RecoveryTestFault.InvalidUtf8)]
    [InlineData(RecoveryTestFault.Oversize)]
    [InlineData(RecoveryTestFault.Truncated)]
    public async Task MalformedPromotedRecoveryStateCannotProduceMatch(RecoveryTestFault fault)
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path)));
        var barrier = new ControlledLivePublishBarrier();
        var controls = new RecoveryQueryControls { FaultFrame = RecoveryTestFrame.State, Fault = fault, BeforeHostStateRead = barrier,
            StateWritten = new(TaskCreationOptions.RunContinuationsAsynchronously), StateTruncatedWritten = new(TaskCreationOptions.RunContinuationsAsynchronously),
            HostStateRejected = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            var query = c.QueryAsync(controls: controls); await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            if (fault == RecoveryTestFault.Truncated) await controls.StateTruncatedWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else await controls.StateWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
            barrier.Release(); Assert.Equal(fault == RecoveryTestFault.Truncated ? "eof" : "invalid", await controls.HostStateRejected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(await query.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { barrier.Release(); }
        for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task PromotedRecoveryCancellationAndBoundedStallDoNotReturnMatch()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var before = await Task.WhenAll(c.Files.Select(path => File.ReadAllBytesAsync(path)));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.QueryAsync(ct: cancel.Token));
        var controls = new RecoveryQueryControls { StallPeerBeforeState = true, Timeout = TimeSpan.FromSeconds(4), PeerStalled = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var stalled = c.QueryAsync(controls: controls); await controls.PeerStalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await stalled.WaitAsync(TimeSpan.FromSeconds(6)));
        for (var i = 0; i < c.Files.Length; i++) Assert.Equal(before[i], await File.ReadAllBytesAsync(c.Files[i]));
        Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
    }

    [Fact]
    public async Task RevocationAfterHostInitialFinalCheckBeforePeerRecheckCannotMatch()
    {
        await using var c = await PromotedRecoveryCase.OpenAsync(); var barrier = new ControlledLivePublishBarrier();
        var query = c.QueryAsync(controls: new RecoveryQueryControls { BeforeFinalPeerRecheck = barrier });
        try
        {
            await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await c.Case.Host.RevokeAsync(c.Case.Fixture.Identity.PeerName)); barrier.Release();
            Assert.Null(await query.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => c.Case.Peer.Get());
        }
        finally { barrier.Release(); }
    }
}
