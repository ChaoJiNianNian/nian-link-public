using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Theory]
    [InlineData(ReadyWireFault.Role)]
    [InlineData(ReadyWireFault.Type)]
    [InlineData(ReadyWireFault.Version)]
    [InlineData(ReadyWireFault.Session)]
    [InlineData(ReadyWireFault.Transaction)]
    [InlineData(ReadyWireFault.OfferNonce)]
    [InlineData(ReadyWireFault.Stage)]
    [InlineData(ReadyWireFault.SenderChallenge)]
    [InlineData(ReadyWireFault.ReceiverChallenge)]
    [InlineData(ReadyWireFault.Slot)]
    [InlineData(ReadyWireFault.DataPortBase)]
    [InlineData(ReadyWireFault.Digest)]
    [InlineData(ReadyWireFault.Missing)]
    [InlineData(ReadyWireFault.Extra)]
    [InlineData(ReadyWireFault.CaseCollision)]
    [InlineData(ReadyWireFault.Duplicate)]
    [InlineData(ReadyWireFault.BadType)]
    [InlineData(ReadyWireFault.InvalidUtf8)]
    [InlineData(ReadyWireFault.Oversize)]
    [InlineData(ReadyWireFault.Truncated)]
    public async Task MutatedV3ReadyFromRealStagingCannotCommit(ReadyWireFault fault)
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var oldPeer = TestInvitation(f); await peer.SaveConfirmedAsync(oldPeer, f.Identity.HostFingerprint);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostRoot, protector);
            var activePath = Path.Combine(hostRoot, "multi-device-trust.bin"); var active = await File.ReadAllBytesAsync(activePath);
            var pendingPath = Path.Combine(hostRoot, "pending-grant.bin"); var pendingBefore = await File.ReadAllBytesAsync(pendingPath);
            f.Peer.ReadyWireControls = new ReadyWireControls { Fault = fault };
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            var commit = host.SendPrepareReadyAndCommitTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            var error = await Record.ExceptionAsync(() => commit.WaitAsync(TimeSpan.FromSeconds(5)));
            if (fault == ReadyWireFault.Truncated) Assert.IsAssignableFrom<IOException>(error);
            else if (fault == ReadyWireFault.BadType) Assert.IsType<InvalidOperationException>(error);
            else Assert.IsType<InvalidDataException>(error);
            var peerError = await Record.ExceptionAsync(() => receive.WaitAsync(TimeSpan.FromSeconds(5)));
            if (fault == ReadyWireFault.Truncated) { Assert.IsType<IOException>(peerError); Assert.True(f.Peer.ReadyWireControls!.TruncatedWritten); }
            else { Assert.Null(peerError); Assert.True(f.Peer.ReadyWireControls!.FrameWritten); }
            Assert.Equal("v3-history", (await peer.InspectPeerV3IsolatedTestAsync(now)).State);
            Assert.True(host.IsAuthorized("old-device", old)); Assert.False(host.IsAuthorized(f.Identity.PeerName, candidate.Token));
            Assert.Equal(active, await File.ReadAllBytesAsync(activePath)); Assert.Equal(pendingBefore, await File.ReadAllBytesAsync(pendingPath));
            Assert.True(oldPeer == peer.Get());
            Assert.Equal(2, (await host.CaptureOfflineRecoveryTestAsync(now))!.MarkerVersion);
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Fact]
    public async Task CompleteReadyFromDifferentActualTlsConnectionCannotCommit()
    {
        var hostA = VaultRoot(); var hostB = VaultRoot(); var peerA = VaultRoot(); var peerB = VaultRoot(); var protector = new WindowsCredentialProtector();
        byte[]? firstFrame = null, secondFrame = null;
        try
        {
            await using var a = await Fixture.OpenAsync(); await using var b = await Fixture.OpenAsync(sameIdentity: a);
            Assert.NotEqual(a.Port, b.Port); await a.BeginAsync(); await a.ConfirmAsync(); await b.BeginAsync(); await b.ConfirmAsync(); var now = a.Clock();
            await using var ha = await MultiDeviceCredentialVault.OpenAsync(hostA, protector);
            await using var pa = await PeerInvitationStore.OpenAsync(peerA, protector);
            await ha.AddConfirmedAsync("old-device", new string('C', 64));
            var stage = (await ha.StagePendingGrantAsync(a.Identity, a.Offer, now, clock: a.Clock)).StageId;
            await ha.FreezePendingInvitationForLoopbackTestAsync(stage, a.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: a.Clock);
            var candidate = await ReadOfflineHostInvitationAsync(hostA, protector);
            Directory.CreateDirectory(hostB);
            await File.WriteAllBytesAsync(Path.Combine(hostB, "pending-grant.bin"), await File.ReadAllBytesAsync(Path.Combine(hostA, "pending-grant.bin")));
            await using var hb = await MultiDeviceCredentialVault.OpenAsync(hostB, protector);
            await using var pb = await PeerInvitationStore.OpenAsync(peerB, protector);
            var oldB = await hb.AddConfirmedAsync("old-device", new string('C', 64));
            var oldPeerB = TestInvitation(b); await pb.SaveConfirmedAsync(oldPeerB, b.Identity.HostFingerprint);
            var activeBPath = Path.Combine(hostB, "multi-device-trust.bin"); var activeB = await File.ReadAllBytesAsync(activeBPath);
            var pendingBPath = Path.Combine(hostB, "pending-grant.bin"); var pendingB = await File.ReadAllBytesAsync(pendingBPath);
            a.Peer.ReadyWireControls = new ReadyWireControls { Capture = true };
            var receiveA = a.Peer.ReceivePrepareAndStageTestAsync(pa, a.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            Assert.Equal("committed", await ha.SendPrepareReadyAndCommitTestAsync(a.Host, a.Host.IssueIsolatedTestCapability(), stage, a.Identity, a.Offer, now, a.Clock).WaitAsync(TimeSpan.FromSeconds(5)));
            await receiveA.WaitAsync(TimeSpan.FromSeconds(5)); firstFrame = a.Peer.ReadyWireControls.Captured ?? throw new InvalidOperationException();
            b.Peer.ReadyWireControls = new ReadyWireControls { Capture = true, Replay = firstFrame };
            var receiveB = b.Peer.ReceivePrepareAndStageTestAsync(pb, b.Peer.IssueIsolatedTestCapability(), writeV3: true, readyV3: true);
            var error = await Record.ExceptionAsync(() => hb.SendPrepareReadyAndCommitTestAsync(b.Host, b.Host.IssueIsolatedTestCapability(), stage, b.Identity, b.Offer, now, b.Clock).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsType<InvalidDataException>(error); await receiveB.WaitAsync(TimeSpan.FromSeconds(5));
            secondFrame = b.Peer.ReadyWireControls.Captured ?? throw new InvalidOperationException();
            using var first = JsonDocument.Parse(firstFrame); using var second = JsonDocument.Parse(secondFrame);
            foreach (var name in new[] { "version", "type", "role", "sessionId", "transactionId", "offerNonce", "stageId", "slot", "dataPortBase", "digest" })
                Assert.True(first.RootElement.GetProperty(name).GetRawText() == second.RootElement.GetProperty(name).GetRawText());
            Assert.True(first.RootElement.GetProperty("senderChallenge").GetString() != second.RootElement.GetProperty("senderChallenge").GetString());
            Assert.True(first.RootElement.GetProperty("receiverChallenge").GetString() != second.RootElement.GetProperty("receiverChallenge").GetString());
            Assert.Equal("v3-history", (await pb.InspectPeerV3IsolatedTestAsync(now)).State);
            Assert.True(hb.IsAuthorized("old-device", oldB)); Assert.False(hb.IsAuthorized(b.Identity.PeerName, candidate.Token));
            Assert.Equal(activeB, await File.ReadAllBytesAsync(activeBPath)); Assert.Equal(pendingB, await File.ReadAllBytesAsync(pendingBPath));
            Assert.True(oldPeerB == pb.Get()); Assert.True(b.Peer.ReadyWireControls.FrameWritten);
            Assert.Equal(2, (await hb.CaptureOfflineRecoveryTestAsync(now))!.MarkerVersion);
        }
        finally
        {
            if (firstFrame is not null) CryptographicOperations.ZeroMemory(firstFrame);
            if (secondFrame is not null) CryptographicOperations.ZeroMemory(secondFrame);
            CleanVault(hostA); CleanVault(hostB); CleanVault(peerA); CleanVault(peerB);
        }
    }
}
