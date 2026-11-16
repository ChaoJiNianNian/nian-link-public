using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.App.DeskLink;
using NianLink.Windows;

namespace MultiPeerTrust.Tests;

public sealed class ReverseShareTransportTests
{
    [Fact]
    public async Task OutOfOrderStatusResponsesCannotReplaceANewerDeliveredRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-reverse-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var control = new PairedControlServer(new() { ProfileDirectory = root, Port = 0, AllowedTargets = ["host", "one"] },
                _ => Task.FromResult(new DeskLinkState()), (_, _) => Task.FromResult(new DeskLinkSwitchResult()));
            await control.StartAsync();
            using var certificate = PrivateFiles.LoadTlsCertificate(Path.Combine(root, "control.pem"));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var now = DateTimeOffset.UtcNow;
                var first = new ReverseShareRequest(Guid.NewGuid(), Guid.NewGuid(), ReverseShareOperation.List, null, "", now, now.AddMinutes(1));
                var second = first with { Id = Guid.NewGuid(), Authorization = Guid.NewGuid() };
                async Task ServeAsync(ReverseShareRequest? reply, bool waitFirst)
                {
                    using var socket = await listener.AcceptTcpClientAsync();
                    await using var tls = new SslStream(socket.GetStream());
                    await tls.AuthenticateAsServerAsync(certificate);
                    var request = await CollaborationProtocol.ReadAsync<CollaborationRequest>(tls, default);
                    if (waitFirst) { firstArrived.SetResult(); await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                    object data = reply is null ? true : new CollaborationStatus(false, []) { ReverseShareTestSupported = true, ReverseShareRequest = reply, ReverseShareTestSequence = waitFirst ? 1 : 2 };
                    await CollaborationProtocol.WriteAsync(tls, new CollaborationResponse(1, request.Id, true, CollaborationProtocol.Element(data)), default);
                }
                var serveFirst = ServeAsync(first, true);
                var bundle = control.CreatePairingBundle("127.0.0.1", "host", "one");
                await using var client = new CollaborationClient(bundle, true, ((IPEndPoint)listener.LocalEndpoint).Port);
                var oldStatus = client.GetStatusAsync(supportsReverseShareTest: true);
                await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var serveSecond = ServeAsync(second, false);
                var newStatus = client.GetStatusAsync(supportsReverseShareTest: true);
                Assert.Equal(second, (await newStatus.WaitAsync(TimeSpan.FromSeconds(5))).ReverseShareRequest);
                await serveSecond;
                releaseFirst.SetResult();
                Assert.Null((await oldStatus.WaitAsync(TimeSpan.FromSeconds(5))).ReverseShareRequest);
                await serveFirst;
                var serveCompletion = ServeAsync(null, false);
                await client.CompleteReverseShareTestAsync(second, [1]).WaitAsync(TimeSpan.FromSeconds(5));
                await serveCompletion;
            }
            finally { listener.Stop(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ExperimentalTransportRequiresCapabilitiesAndKeepsGrantsIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-reverse-tls-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "trust"), new WindowsCredentialProtector());
            var one = await vault.AddConfirmedAsync("one", new string('1', 64));
            var two = await vault.AddConfirmedAsync("two", new string('2', 64));
            await using var control = new PairedControlServer(new() { ProfileDirectory = Path.Combine(root, "cert"), Port = 0, AllowedTargets = ["host", "one"] },
                _ => Task.FromResult(new DeskLinkState()), (_, _) => Task.FromResult(new DeskLinkSwitchResult()));
            await control.StartAsync();
            var cert = Path.Combine(root, "cert", "control.pem");
            await using var first = MultiPeerCollaborationEndpoint.CreateForReverseShareTest(vault, "one", Path.Combine(root, "data"), cert);
            await using var second = MultiPeerCollaborationEndpoint.CreateForReverseShareTest(vault, "two", Path.Combine(root, "data"), cert);
            await first.StartAsync(); await second.StartAsync();
            DeskLinkPairingBundle Bundle(string peer, string token) => new() { Host = "127.0.0.1", Port = control.Port, WindowsName = "host", MacName = peer, CertificateSha256 = control.CertificateSha256!, Token = token };
            await using var client = new CollaborationClient(Bundle("one", one), true, first.Port);
            await using var other = new CollaborationClient(Bundle("two", two), true, second.Port);
            await using var wrong = new CollaborationClient(Bundle("one", one), true, second.Port);
            await using var wrongCertificate = new CollaborationClient(Bundle("one", one) with { CertificateSha256 = new string('A', 64) }, true, first.Port);
            await first.EnableReverseShareTestAsync();
            var request = await first.EnqueueReverseShareTestAsync(ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(30));
            Assert.Null((await client.GetStatusAsync()).ReverseShareRequest);
            Assert.Null((await other.GetStatusAsync(supportsReverseShareTest: true)).ReverseShareRequest);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.GetStatusAsync(supportsReverseShareTest: true));
            await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => wrongCertificate.GetStatusAsync(supportsReverseShareTest: true));
            var status = await client.GetStatusAsync(supportsReverseShareTest: true);
            Assert.True(status.ReverseShareTestSupported); Assert.Equal(request, status.ReverseShareRequest);
            Assert.Null((await client.GetStatusAsync(supportsReverseShareTest: true)).ReverseShareRequest);
            await client.GetStatusAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteReverseShareTestAsync(request, new byte[65537]));
            await client.CompleteReverseShareTestAsync(request, [1, 2, 3]);
            Assert.Equal(new byte[] { 1, 2, 3 }, await first.TakeReverseShareTestResultAsync(request));
            Assert.Null(await first.TakeReverseShareTestResultAsync(request));
            var revoked = await first.EnqueueReverseShareTestAsync(ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(30));
            Assert.Equal(revoked, (await client.GetStatusAsync(supportsReverseShareTest: true)).ReverseShareRequest);
            await client.CompleteReverseShareTestAsync(revoked, [8]);
            Assert.True(await vault.RevokeAsync("one"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.TakeReverseShareTestResultAsync(revoked));
            var replacement = await vault.AddConfirmedAsync("one", new string('3', 64));
            await using var replacementClient = new CollaborationClient(Bundle("one", replacement), true, first.Port);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => replacementClient.GetStatusAsync(supportsReverseShareTest: true));
            await second.EnableReverseShareTestAsync();
            var late = await second.EnqueueReverseShareTestAsync(ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(30));
            Assert.Equal(late, (await other.GetStatusAsync(supportsReverseShareTest: true)).ReverseShareRequest);
            Assert.True(await vault.RevokeAsync("two"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.CompleteReverseShareTestAsync(late, [9]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DefaultEndpointNeverAdvertisesAndStoppingClearsTestQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-reverse-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "trust"), new WindowsCredentialProtector());
            var token = await vault.AddConfirmedAsync("one", new string('1', 64));
            await using var control = new PairedControlServer(new() { ProfileDirectory = Path.Combine(root, "cert"), Port = 0, AllowedTargets = ["host", "one"] },
                _ => Task.FromResult(new DeskLinkState()), (_, _) => Task.FromResult(new DeskLinkSwitchResult()));
            await control.StartAsync();
            var cert = Path.Combine(root, "cert", "control.pem");
            await using var ordinary = MultiPeerCollaborationEndpoint.Create(vault, "one", Path.Combine(root, "normal"), cert, IPAddress.Loopback, 0);
            await ordinary.StartAsync();
            var bundle = new DeskLinkPairingBundle { Host = "127.0.0.1", Port = control.Port, WindowsName = "host", MacName = "one", CertificateSha256 = control.CertificateSha256!, Token = token };
            await using var normalClient = new CollaborationClient(bundle, true, ordinary.Port);
            var unsupported = await normalClient.GetStatusAsync(supportsReverseShareTest: true);
            Assert.False(unsupported.ReverseShareTestSupported); Assert.Null(unsupported.ReverseShareRequest);
            await Assert.ThrowsAsync<NotSupportedException>(() => normalClient.CompleteReverseShareTestAsync(new(Guid.NewGuid(), Guid.NewGuid(), ReverseShareOperation.List, null, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)), []));
            await using var experimental = MultiPeerCollaborationEndpoint.CreateForReverseShareTest(vault, "one", Path.Combine(root, "test"), cert);
            await experimental.StartAsync(); await experimental.EnableReverseShareTestAsync();
            await using var client = new CollaborationClient(bundle, true, experimental.Port);
            var request = await experimental.EnqueueReverseShareTestAsync(ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(30));
            Assert.Equal(request, (await client.GetStatusAsync(supportsReverseShareTest: true)).ReverseShareRequest);
            var payload = new byte[65536]; Random.Shared.NextBytes(payload);
            using var frame = new MemoryStream();
            await CollaborationProtocol.WriteAsync(frame, new CollaborationRequest(1, Guid.NewGuid().ToString("N"), token, "reverse-share-test-complete", CollaborationProtocol.Element(new ReverseShareTestCompletion(request.Authorization, request.Id, payload))), default);
            Assert.True(frame.Length < CollaborationProtocol.MaximumFrameBytes);
            await client.CompleteReverseShareTestAsync(request, payload);
            await experimental.StopAsync();
            Assert.Null(await experimental.TakeReverseShareTestResultAsync(request));
            await Assert.ThrowsAsync<InvalidOperationException>(() => experimental.EnableReverseShareTestAsync());
            await experimental.StartAsync();
            await experimental.EnableReverseShareTestAsync();
            Assert.Null(await experimental.TakeReverseShareTestResultAsync(request));
            var next = await experimental.EnqueueReverseShareTestAsync(ReverseShareOperation.List, null, "", TimeSpan.FromSeconds(30));
            Assert.NotEqual(request.Authorization, next.Authorization);
            await using var restartedClient = new CollaborationClient(bundle, true, experimental.Port);
            Assert.Equal(next, (await restartedClient.GetStatusAsync(supportsReverseShareTest: true)).ReverseShareRequest);
            using (var raw = new TcpClient())
            {
                await raw.ConnectAsync(IPAddress.Loopback, experimental.Port);
                await using var tls = new SslStream(raw.GetStream(), false, (_, certificate, _, _) => certificate is not null && DeskLinkFingerprint.Matches(bundle.CertificateSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.GetRawCertData()))));
                await tls.AuthenticateAsClientAsync("nian-desklink");
                var id = Guid.NewGuid().ToString("N");
                var stale = new ReverseShareTestCompletion(request.Authorization, request.Id, [9]);
                await CollaborationProtocol.WriteAsync(tls, new CollaborationRequest(1, id, token, "reverse-share-test-complete", CollaborationProtocol.Element(stale)), default);
                var response = await CollaborationProtocol.ReadAsync<CollaborationResponse>(tls, default);
                Assert.Equal(id, response.Id); Assert.False(response.Ok);
            }
            await restartedClient.CompleteReverseShareTestAsync(next, [7]);
            Assert.Equal(new byte[] { 7 }, await experimental.TakeReverseShareTestResultAsync(next));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
