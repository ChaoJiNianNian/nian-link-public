using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed partial class LiveTlsConsentCapabilityTests
{
    private static string VaultRoot() => Path.Combine(Path.GetTempPath(), "nian-live-vault-" + Guid.NewGuid().ToString("N"));
    private static void CleanVault(string root)
    {
        var path=Path.GetFullPath(root); var temp=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(path),temp,StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("nian-live-vault-",StringComparison.Ordinal) ||
            Directory.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("Unsafe test directory.");
        if (Directory.Exists(path)) Directory.Delete(path,true);
    }

    [Fact]
    public async Task ConfirmedRealTlsCapabilityCommitsFrozenPendingOnlyInTestAssembly()
    {
        var root=VaultRoot(); var protector=new WindowsCredentialProtector();
        try
        {
            await using var f=await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
            var key=f.Host.IssueIsolatedTestCapability(); var now=f.Clock();
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var old=await vault.AddConfirmedAsync("old",new string('C',64));
                var stage=(await vault.StagePendingGrantAsync(f.Identity,f.Offer,now,clock:f.Clock)).StageId;
                var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,f.Identity,IPAddress.Loopback,24801,24800,24802,now,clock:f.Clock);
                var plain=protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root,"pending-grant.bin"))); string token;
                try { using var json=JsonDocument.Parse(plain); token=json.RootElement.GetProperty("token").GetString()!; }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Assert.False(vault.IsAuthorized("peer",token));
                Assert.Equal("committed",await vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock));
                Assert.True(vault.IsAuthorized("old",old)); Assert.True(vault.IsAuthorized("peer",token));
                Assert.Equal(frozen.Slot,vault.GetTrustedDevices().Single(x=>x.Name=="peer").Slot);
                plain=protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
                try { using var json=JsonDocument.Parse(plain); Assert.Equal(2,json.RootElement.GetProperty("version").GetInt32()); Assert.Equal(1,json.RootElement.GetProperty("transactions").GetArrayLength()); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock));
            }
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("close")]
    [InlineData("expiry")]
    [InlineData("cancel")]
    public async Task WaitingForVaultGateIsInterruptedWithoutPublishing(string interruption)
    {
        var root=VaultRoot(); var protector=new GateProtector();
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        try
        {
            await using var f=await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now=f.Clock();
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var old=await vault.AddConfirmedAsync("old",new string('C',64));
            var stage=(await vault.StagePendingGrantAsync(f.Identity,f.Offer,now,clock:f.Clock)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,f.Identity,IPAddress.Loopback,24801,24800,24802,now,clock:f.Clock);
            var activePath=Path.Combine(root,"multi-device-trust.bin"); var pendingPath=Path.Combine(root,"pending-grant.bin");
            var active=await File.ReadAllBytesAsync(activePath); var pending=await File.ReadAllBytesAsync(pendingPath);
            protector.BeforeProtect=()=> { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
            var blocker=Task.Run(()=>vault.AddConfirmedAsync("other",new string('D',64)));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                using var cancel=new CancellationTokenSource();
                var key=f.Host.IssueIsolatedTestCapability();
                var commit=vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock,cancel.Token);
                Assert.False(commit.IsCompleted);
                if(interruption=="close") await f.Host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if(interruption=="expiry") { Interlocked.Exchange(ref f.UtcTicks,f.Identity.ExpiresAtUtc.UtcTicks); Assert.Equal(PairingConsentPhase.Aborted,f.Host.Phase); }
                if(interruption=="cancel") cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>commit.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(active,await File.ReadAllBytesAsync(activePath)); Assert.Equal(pending,await File.ReadAllBytesAsync(pendingPath));
                Assert.True(vault.IsAuthorized("old",old));
            }
            finally { release.Set(); await blocker.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { CleanVault(root); }
    }

    private sealed class GateProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner=new(); internal Action? BeforeProtect { get; set; }
        public byte[] Protect(byte[] plaintext) { BeforeProtect?.Invoke(); return inner.Protect(plaintext); }
        public byte[] Unprotect(byte[] ciphertext)=>inner.Unprotect(ciphertext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseAndFinalPublicationFollowSessionGateOrder(bool publicationWins)
    {
        var root=VaultRoot(); var protector=new WindowsCredentialProtector(); var barrier=new ControlledLivePublishBarrier();
        try
        {
            await using var f=await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now=f.Clock();
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var old=await vault.AddConfirmedAsync("old",new string('C',64));
            var stage=(await vault.StagePendingGrantAsync(f.Identity,f.Offer,now,clock:f.Clock)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,f.Identity,IPAddress.Loopback,24801,24800,24802,now,clock:f.Clock);
            var active=await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin"));
            if(publicationWins) f.Host.BeforeLiveMove=barrier; else vault.BeforeLivePublish=barrier;
            var key=f.Host.IssueIsolatedTestCapability();
            var commit=Task.Run(()=>vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock));
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var closeStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var close=Task.Run(async()=> { closeStarted.TrySetResult(); await f.Host.DisposeAsync(); });
                await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if(!publicationWins)
                {
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while(f.Host.Phase!=PairingConsentPhase.Aborted) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                }
                Assert.False(close.IsCompleted); barrier.Release();
                if(publicationWins)
                {
                    Assert.Equal("committed",await commit.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.Equal(2,vault.GetTrustedDevices().Count);
                }
                else
                {
                    var error=await Record.ExceptionAsync(()=>commit.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.True(error is InvalidOperationException or OperationCanceledException,$"Unexpected result: {error}");
                    Assert.Equal(active,await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
                    Assert.Single(vault.GetTrustedDevices());
                }
                await close.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(vault.IsAuthorized("old",old));
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task ExpiryBeforeMoveInsideSessionGateRejectsPublication()
    {
        var root=VaultRoot(); var protector=new WindowsCredentialProtector(); var barrier=new ControlledLivePublishBarrier();
        try
        {
            await using var f=await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now=f.Clock();
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var old=await vault.AddConfirmedAsync("old",new string('C',64));
            var stage=(await vault.StagePendingGrantAsync(f.Identity,f.Offer,now,clock:f.Clock)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,f.Identity,IPAddress.Loopback,24801,24800,24802,now,clock:f.Clock);
            var before=await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")); f.Host.BeforeLiveMove=barrier;
            var key=f.Host.IssueIsolatedTestCapability(); var commit=Task.Run(()=>vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock));
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref f.UtcTicks,f.Identity.ExpiresAtUtc.UtcTicks); barrier.Release();
                var error=await Record.ExceptionAsync(()=>commit.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error is InvalidOperationException or OperationCanceledException,$"Unexpected result: {error}");
                Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
                Assert.True(vault.IsAuthorized("old",old)); Assert.Single(vault.GetTrustedDevices());
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(root); }
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("after")]
    public async Task UncertainReplacementFailsClosedAndReopenReadsCompleteSnapshot(string failure)
    {
        var root=VaultRoot(); var protector=new WindowsCredentialProtector();
        try
        {
            string old;
            await using (var f=await Fixture.OpenAsync())
            {
                await f.BeginAsync(); await f.ConfirmAsync(); var now=f.Clock();
                await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
                {
                    old=await vault.AddConfirmedAsync("old",new string('C',64));
                    var stage=(await vault.StagePendingGrantAsync(f.Identity,f.Offer,now,clock:f.Clock)).StageId;
                    var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,f.Identity,IPAddress.Loopback,24801,24800,24802,now,clock:f.Clock);
                    var key=f.Host.IssueIsolatedTestCapability();
                    if(failure=="locked")
                    {
                        using var held=new FileStream(Path.Combine(root,"multi-device-trust.bin"),FileMode.Open,FileAccess.Read,FileShare.Read);
                        var error=await Record.ExceptionAsync(()=>vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock));
                        Assert.True(error is IOException or UnauthorizedAccessException,$"Unexpected result: {error}");
                    }
                    else
                    {
                        vault.AfterIsolatedMove=()=>throw new IOException("Synthetic post-replacement interruption.");
                        await Assert.ThrowsAsync<IOException>(()=>vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock));
                    }
                    Assert.False(vault.IsAuthorized("old",old));
                }
            }
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.True(reopened.IsAuthorized("old",old));
            Assert.Equal(failure=="locked" ? 1 : 2,reopened.GetTrustedDevices().Count);
            Assert.True(File.Exists(Path.Combine(root,"pending-grant.bin")));
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task ObservedEofBeforePublicationRejectsTheFrozenGrant()
    {
        var root=VaultRoot(); var protector=new WindowsCredentialProtector(); var barrier=new ControlledLivePublishBarrier();
        try
        {
            await using var f=await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync(); var now=f.Clock();
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var old=await vault.AddConfirmedAsync("old",new string('C',64));
            var stage=(await vault.StagePendingGrantAsync(f.Identity,f.Offer,now,clock:f.Clock)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,f.Identity,IPAddress.Loopback,24801,24800,24802,now,clock:f.Clock);
            var path=Path.Combine(root,"multi-device-trust.bin"); var before=await File.ReadAllBytesAsync(path);
            vault.BeforeLivePublish=barrier;
            var key=f.Host.IssueIsolatedTestCapability(); var commit=vault.CommitLiveTlsIsolatedAsync(f.Host,key,stage,f.Identity,f.Offer,frozen,now,f.Clock);
            try
            {
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reading=f.Host.ObserveIsolatedTestAsync(started,CancellationToken.None); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await f.Peer.DisposeAsync();
                await Assert.ThrowsAnyAsync<IOException>(()=>reading.WaitAsync(TimeSpan.FromSeconds(5)));
                barrier.Release();
                var error=await Record.ExceptionAsync(()=>commit.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(error is InvalidOperationException or OperationCanceledException,$"Unexpected result: {error}");
                Assert.Equal(before,await File.ReadAllBytesAsync(path)); Assert.True(vault.IsAuthorized("old",old));
            }
            finally { barrier.Release(); }
        }
        finally { CleanVault(root); }
    }

    [Fact]
    public async Task OtherConnectionCapabilityCannotAuthorizeLiveVaultTest()
    {
        var root=VaultRoot(); var protector=new WindowsCredentialProtector();
        try
        {
            await using var a=await Fixture.OpenAsync(); await using var b=await Fixture.OpenAsync(sameIdentity:a);
            await a.BeginAsync(); await a.ConfirmAsync(); await b.BeginAsync(); await b.ConfirmAsync(); var now=a.Clock();
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(a.Identity,a.Offer,now,clock:a.Clock)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,a.Identity,IPAddress.Loopback,24801,24800,24802,now,clock:a.Clock);
            var other=b.Host.IssueIsolatedTestCapability();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitLiveTlsIsolatedAsync(a.Host,other,stage,a.Identity,a.Offer,frozen,now,a.Clock));
            Assert.Empty(vault.GetTrustedDevices()); Assert.True(File.Exists(Path.Combine(root,"pending-grant.bin")));
        }
        finally { CleanVault(root); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string folder;
        private TcpListener? listener;
        private TcpClient? client, accepted;
        private X509Certificate2? hostCert, peerCert;
        internal X509Certificate2 HostCertificate => hostCert!;
        internal X509Certificate2 PeerCertificate => peerCert!;
        internal string HostCertificatePath => Path.Combine(folder, "host.pem");
        internal string PeerCertificatePath => Path.Combine(folder, "peer.pem");
        private bool ownsCertificates;
        private SslStream? hostStream, peerStream;
        internal LoopbackTlsProxy? Proxy;
        internal PairingTlsConsentSession Host = null!, Peer = null!;
        internal SslStream HostStream => hostStream!;
        internal PairingSessionIdentity Identity = null!;
        internal PeerIdentityOffer Offer = null!;
        internal long UtcTicks;
        internal int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;
        internal DateTimeOffset Clock() => new(Interlocked.Read(ref UtcTicks), TimeSpan.Zero);
        private Fixture(string? certificateFolder) => folder = certificateFolder ?? Path.Combine(Path.GetTempPath(), "nian-consent-capability-" + Guid.NewGuid().ToString("N"));

        internal static async Task<Fixture> OpenAsync(bool differingOffer = false, Fixture? sameIdentity = null, bool throughProxy = false, string? certificateFolder = null)
        {
            var f = new Fixture(certificateFolder);
            try
            {
                Directory.CreateDirectory(f.folder);
                var h = Path.Combine(f.folder, "host.pem"); var p = Path.Combine(f.folder, "peer.pem");
                if (sameIdentity is null)
                {
                    await PrivateFiles.EnsureCertificateAsync(h, CancellationToken.None);
                    await PrivateFiles.EnsureCertificateAsync(p, CancellationToken.None);
                    f.ownsCertificates = true;
                    f.hostCert = PrivateFiles.LoadTlsCertificate(h); f.peerCert = PrivateFiles.LoadTlsCertificate(p);
                }
                else
                {
                    f.hostCert = sameIdentity.hostCert; f.peerCert = sameIdentity.peerCert;
                }
                var hostCert = f.hostCert ?? throw new InvalidOperationException("Missing host test certificate.");
                var peerCert = f.peerCert ?? throw new InvalidOperationException("Missing peer test certificate.");
                f.listener = new TcpListener(IPAddress.Loopback, 0); f.listener.Start(1);
                if (throughProxy) f.Proxy = new LoopbackTlsProxy(((IPEndPoint)f.listener.LocalEndpoint).Port);
                f.client = new TcpClient(AddressFamily.InterNetwork);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var connect = f.client.ConnectAsync(IPAddress.Loopback, f.Proxy?.Port ?? ((IPEndPoint)f.listener.LocalEndpoint).Port, timeout.Token).AsTask();
                f.accepted = await f.listener.AcceptTcpClientAsync(timeout.Token); await connect;
                f.hostStream = new SslStream(f.accepted.GetStream(), false, (_, cert, _, _) => Matches(peerCert, cert));
                f.peerStream = new SslStream(f.client.GetStream(), false, (_, cert, _, _) => Matches(hostCert, cert), (_, _, _, _, _) => peerCert);
                await Task.WhenAll(
                    f.hostStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = hostCert, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token),
                    f.peerStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "pairing-test", ClientCertificates = new X509CertificateCollection { peerCert }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token));
                var now = DateTimeOffset.UtcNow; f.UtcTicks = now.UtcTicks; var nonce = Guid.NewGuid();
                f.Offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(peerCert), Nonce = nonce.ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
                f.Identity = new PairingSessionIdentity(Guid.NewGuid(), nonce, Guid.NewGuid(), "host", "peer", DeskLinkFingerprint.OfCertificate(hostCert), DeskLinkFingerprint.OfCertificate(peerCert), now.AddMinutes(1));
                if (sameIdentity is not null) { f.UtcTicks = sameIdentity.UtcTicks; f.Identity = sameIdentity.Identity; f.Offer = sameIdentity.Offer; }
                f.Host = new(f.hostStream, f.Identity, f.Offer, true, f.Clock);
                f.Peer = new(f.peerStream, f.Identity, differingOffer ? f.Offer with { Platform = "mac" } : f.Offer, false, f.Clock);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        private static bool Matches(X509Certificate2 expected, X509Certificate? actual) => actual is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected.RawData), SHA256.HashData(actual.GetRawCertData()));
        internal async Task BeginAsync() => await Task.WhenAll(Host.BeginAsync(), Peer.BeginAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        internal async Task ConfirmAsync()
        {
            await Host.ConfirmLocalAsync(); await Peer.ReceiveRemoteConfirmationAsync();
            await Peer.ConfirmLocalAsync(); await Host.ReceiveRemoteConfirmationAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (Host is not null) await Host.DisposeAsync();
            if (Peer is not null) await Peer.DisposeAsync();
            if (hostStream is not null) await hostStream.DisposeAsync();
            if (peerStream is not null) await peerStream.DisposeAsync();
            accepted?.Dispose(); client?.Dispose(); listener?.Stop();
            if (Proxy is not null) await Proxy.DisposeAsync();
            if (ownsCertificates) { hostCert?.Dispose(); peerCert?.Dispose(); }
            var path = Path.GetFullPath(folder); var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Directory.Exists(path) && string.Equals(Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("nian-consent-capability-", StringComparison.Ordinal) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task CapabilityRequiresBothConfirmationsAndIsOneShot()
    {
        await using var f = await Fixture.OpenAsync(); await f.BeginAsync();
        Assert.Throws<InvalidOperationException>(() => f.Host.IssueIsolatedTestCapability());
        await f.Host.ConfirmLocalAsync(); await f.Peer.ReceiveRemoteConfirmationAsync();
        Assert.Throws<InvalidOperationException>(() => f.Host.IssueIsolatedTestCapability());
        await f.Peer.ConfirmLocalAsync(); await f.Host.ReceiveRemoteConfirmationAsync();
        var key = f.Host.IssueIsolatedTestCapability();
        Assert.Throws<InvalidOperationException>(() => f.Host.IssueIsolatedTestCapability());
        Assert.DoesNotContain(f.Identity.SessionId.ToString(), key.ToString()!);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(key, key.GetType()));
        var receiver = new ControlledConsentTestReceiver(); receiver.Release();
        await f.Host.RunIsolatedTestActionAsync(key, receiver);
        Assert.Equal(1, receiver.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.RunIsolatedTestActionAsync(key, receiver));
    }

    [Fact]
    public async Task WrongOfferCannotIssueCapability()
    {
        await using var f = await Fixture.OpenAsync(differingOffer: true);
        var hostBegin = f.Host.BeginAsync(); var peerBegin = f.Peer.BeginAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(hostBegin, peerBegin).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(hostBegin.IsCompleted && peerBegin.IsCompleted);
        Assert.Contains(new[] { hostBegin.Exception, peerBegin.Exception }.Where(x => x is not null).SelectMany(x => x!.Flatten().InnerExceptions), error => error is InvalidDataException);
        Assert.Throws<InvalidOperationException>(() => f.Host.IssueIsolatedTestCapability());
    }

    [Fact]
    public async Task ForgedOrOtherConnectionCapabilityCannotBeUsed()
    {
        await using var a = await Fixture.OpenAsync(); await using var b = await Fixture.OpenAsync(sameIdentity: a);
        Assert.NotEqual(a.Port, b.Port); Assert.Equal(a.Identity, b.Identity); Assert.Equal(a.Offer, b.Offer);
        await a.BeginAsync(); await a.ConfirmAsync(); await b.BeginAsync(); await b.ConfirmAsync();
        var key = a.Host.IssueIsolatedTestCapability(); var other = b.Host.IssueIsolatedTestCapability();
        var receiver = new ControlledConsentTestReceiver(); receiver.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Host.RunIsolatedTestActionAsync(key, receiver));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.Host.RunIsolatedTestActionAsync(new object(), receiver));
        await b.Host.RunIsolatedTestActionAsync(other, receiver); Assert.Equal(1, receiver.Completed);
    }

    [Fact]
    public async Task CloseBeforeActionDeniesAndActionBeforeCloseDrains()
    {
        await using var a = await Fixture.OpenAsync(); await a.BeginAsync(); await a.ConfirmAsync();
        var closedKey = a.Host.IssueIsolatedTestCapability(); await a.Host.DisposeAsync();
        var denied = new ControlledConsentTestReceiver(); denied.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Host.RunIsolatedTestActionAsync(closedKey, denied)); Assert.Equal(0, denied.Completed);
        await using var b = await Fixture.OpenAsync(); await b.BeginAsync(); await b.ConfirmAsync();
        var key = b.Host.IssueIsolatedTestCapability(); var receiver = new ControlledConsentTestReceiver();
        try
        {
            var action = b.Host.RunIsolatedTestActionAsync(key, receiver); await receiver.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = b.Host.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            receiver.Release(); await action.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, receiver.Completed);
        }
        finally { receiver.Release(); }
    }

    [Fact]
    public async Task ExpiryBeforeActionDeniesWhileExpiryAfterGrantDrains()
    {
        await using var a = await Fixture.OpenAsync(); await a.BeginAsync(); await a.ConfirmAsync();
        var key = a.Host.IssueIsolatedTestCapability(); Interlocked.Exchange(ref a.UtcTicks, a.Identity.ExpiresAtUtc.UtcTicks);
        Assert.Equal(PairingConsentPhase.Aborted, a.Host.Phase);
        var denied = new ControlledConsentTestReceiver(); denied.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Host.RunIsolatedTestActionAsync(key, denied)); Assert.Equal(0, denied.Completed);
        await using var b = await Fixture.OpenAsync(); await b.BeginAsync(); await b.ConfirmAsync();
        key = b.Host.IssueIsolatedTestCapability(); var receiver = new ControlledConsentTestReceiver();
        try
        {
            var action = b.Host.RunIsolatedTestActionAsync(key, receiver); await receiver.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            Interlocked.Exchange(ref b.UtcTicks, b.Identity.ExpiresAtUtc.UtcTicks); Assert.Equal(PairingConsentPhase.Aborted, b.Host.Phase);
            var closing = b.Host.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); receiver.Release();
            await action.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, receiver.Completed);
        }
        finally { receiver.Release(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObservedEofOrCancellationInvalidatesCapability(bool cancel)
    {
        await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
        var key = f.Host.IssueIsolatedTestCapability(); using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = f.Host.ObserveIsolatedTestAsync(started, cts.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) cts.Cancel(); else await f.Peer.DisposeAsync();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAnyAsync<IOException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PairingConsentPhase.Aborted, f.Host.Phase);
        var receiver = new ControlledConsentTestReceiver(); receiver.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.RunIsolatedTestActionAsync(key, receiver)); Assert.Equal(0, receiver.Completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObservedEofOrCancellationAfterGrantWaitsForAction(bool cancel)
    {
        await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
        var key = f.Host.IssueIsolatedTestCapability(); var receiver = new ControlledConsentTestReceiver();
        try
        {
            var action = f.Host.RunIsolatedTestActionAsync(key, receiver); await receiver.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = f.Host.ObserveIsolatedTestAsync(started, cts.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) cts.Cancel(); else await f.Peer.DisposeAsync();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
            else await Assert.ThrowsAnyAsync<IOException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
            var closing = f.Host.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            receiver.Release(); await action.WaitAsync(TimeSpan.FromSeconds(5)); await closing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, receiver.Completed);
        }
        finally { receiver.Release(); }
    }

    [Fact]
    public async Task ConcurrentActionsOnlyGrantOneAndFailedActionStillDrains()
    {
        await using var f = await Fixture.OpenAsync(); await f.BeginAsync(); await f.ConfirmAsync();
        var key = f.Host.IssueIsolatedTestCapability(); var receiver = new ControlledConsentTestReceiver(fail: true);
        try
        {
            var first = f.Host.RunIsolatedTestActionAsync(key, receiver); await receiver.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.RunIsolatedTestActionAsync(key, receiver));
            var closing = f.Host.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); receiver.Release();
            await Assert.ThrowsAsync<InvalidOperationException>(() => first); await closing.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, receiver.Completed);
        }
        finally { receiver.Release(); }
    }
}
