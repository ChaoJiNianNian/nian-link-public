using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    [Fact]
    public async Task RealPrepareMarkerBindsOriginalOfferAndExactProtectedPending()
    {
        var hostRoot = VaultRoot(); var peerRoot = VaultRoot(); var protector = new WindowsCredentialProtector();
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(hostRoot, protector);
            await using var peer = await PeerInvitationStore.OpenAsync(peerRoot, protector);
            var old = await host.AddConfirmedAsync("old-device", new string('C', 64));
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            var receive = f.Peer.ReceivePrepareAndStageTestAsync(peer, f.Peer.IssueIsolatedTestCapability(), writeV3: true);
            var send = host.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock);
            await Task.WhenAll(send, receive).WaitAsync(TimeSpan.FromSeconds(5));
            var markerPath = Path.Combine(hostRoot, "pending-prepare-unknown.bin"); var pendingPath = Path.Combine(hostRoot, "pending-grant.bin");
            var marker = protector.Unprotect(await File.ReadAllBytesAsync(markerPath));
            try
            {
                using var json = JsonDocument.Parse(marker); var root = json.RootElement;
                Assert.Equal(2, root.GetProperty("version").GetInt32());
                Assert.Equal(f.Offer, root.GetProperty("offer").Deserialize<PeerIdentityOffer>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(pendingPath))), root.GetProperty("pendingCiphertextDigest").GetString());
            }
            finally { CryptographicOperations.ZeroMemory(marker); }
            Assert.Equal(2, (await host.CaptureOfflineRecoveryTestAsync(now))!.MarkerVersion);
            var originalMarker = await File.ReadAllBytesAsync(markerPath);
            foreach (var defect in new[] { "platform", "expiry", "digest", "missing", "extra", "duplicate", "version" })
            {
                var originalPlain = protector.Unprotect(originalMarker);
                try
                {
                    var node = JsonNode.Parse(originalPlain)!;
                    switch (defect)
                    {
                        case "platform": node["offer"]!["platform"] = "mac"; break;
                        case "expiry": node["offer"]!["expiresAtUtc"] = f.Offer.ExpiresAtUtc.AddSeconds(1); break;
                        case "digest": node["pendingCiphertextDigest"] = new string('A', 64); break;
                        case "missing": node.AsObject().Remove("offer"); break;
                        case "extra": node["offer"]!["extra"] = true; break;
                        case "version": node["version"] = 3; break;
                    }
                    var altered = JsonSerializer.SerializeToUtf8Bytes(node);
                    if (defect == "duplicate")
                    {
                        var duplicate = Encoding.UTF8.GetString(altered).Replace("\"version\":2", "\"version\":2,\"version\":2", StringComparison.Ordinal);
                        CryptographicOperations.ZeroMemory(altered); altered = Encoding.UTF8.GetBytes(duplicate);
                    }
                    try { await File.WriteAllBytesAsync(markerPath, protector.Protect(altered)); }
                    finally { CryptographicOperations.ZeroMemory(altered); }
                }
                finally { CryptographicOperations.ZeroMemory(originalPlain); }
                var invalid = await File.ReadAllBytesAsync(markerPath);
                Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now));
                Assert.Equal(invalid, await File.ReadAllBytesAsync(markerPath));
            }
            await File.WriteAllBytesAsync(markerPath, originalMarker);
            var plain = protector.Unprotect(await File.ReadAllBytesAsync(pendingPath));
            try { await File.WriteAllBytesAsync(pendingPath, protector.Protect(plain)); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var changed = await File.ReadAllBytesAsync(pendingPath); var markerBefore = await File.ReadAllBytesAsync(markerPath);
            Assert.Null(await host.CaptureOfflineRecoveryTestAsync(now));
            Assert.Equal(changed, await File.ReadAllBytesAsync(pendingPath)); Assert.Equal(markerBefore, await File.ReadAllBytesAsync(markerPath));
            Assert.True(host.IsAuthorized("old-device", old));
        }
        finally { CleanVault(hostRoot); CleanVault(peerRoot); }
    }

    [Theory]
    [InlineData("pending-grant.bin")]
    [InlineData("pending-prepare-unknown.bin")]
    public async Task ChangedProtectedFileBeforePrepareIsNotSent(string changedFile)
    {
        var root = VaultRoot(); var protector = new WindowsCredentialProtector(); var barrier = new ControlledLivePublishBarrier();
        using var cancellation = new CancellationTokenSource(); Task? send = null;
        try
        {
            await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now = f.Clock();
            await using var host = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            var stage = (await host.StagePendingGrantAsync(f.Identity, f.Offer, now, clock: f.Clock)).StageId;
            await host.FreezePendingInvitationForLoopbackTestAsync(stage, f.Identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: f.Clock);
            host.BeforeLivePrepareSend = barrier;
            send = host.SendPrepareAndReceiveReadyTestAsync(f.Host, f.Host.IssueIsolatedTestCapability(), stage, f.Identity, f.Offer, now, f.Clock, cancellation.Token);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var path = Path.Combine(root, changedFile); var plain = protector.Unprotect(await File.ReadAllBytesAsync(path));
                try { await File.WriteAllBytesAsync(path, protector.Protect(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                var changed = await File.ReadAllBytesAsync(path); barrier.Release();
                var result = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(2)));
                Assert.Same(send, result);
                await Assert.ThrowsAsync<InvalidOperationException>(() => send);
                Assert.Equal(changed, await File.ReadAllBytesAsync(path));
                Assert.True(File.Exists(Path.Combine(root, "pending-prepare-unknown.bin")));
                Assert.Empty(host.GetTrustedDevices());
            }
            finally { barrier.Release(); cancellation.Cancel(); if (send is not null) { try { await send.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } } }
        }
        finally { barrier.Release(); CleanVault(root); }
    }
}
