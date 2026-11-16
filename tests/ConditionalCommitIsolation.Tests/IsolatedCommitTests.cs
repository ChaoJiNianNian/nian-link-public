using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace ConditionalCommitIsolation.Tests;

public sealed class IsolatedCommitTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(), "nian-isolated-commit-" + Guid.NewGuid().ToString("N"));
    private static void Clean(string root)
    {
        var path = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("nian-isolated-commit-", StringComparison.Ordinal) ||
            Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Unsafe isolated test path.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private static (PairingSessionIdentity, PeerIdentityOffer) Proposal(DateTimeOffset now)
    {
        var nonce = Guid.NewGuid();
        var offer = new PeerIdentityOffer { Name = "peer", Platform = "windows", KeyboardFingerprint = new string('B', 64), Nonce = nonce.ToString("N"), ExpiresAtUtc = now.AddMinutes(2) };
        return (new(Guid.NewGuid(), nonce, Guid.NewGuid(), "host", "peer", new string('A', 64), new string('B', 64), now.AddMinutes(1)), offer);
    }

    [Fact]
    public async Task CommitPreservesLegacyGrantAndUsesPendingTokenSlotAndOneRecord()
    {
        var root = Root(); var protector = new WindowsCredentialProtector(); var now = DateTimeOffset.UtcNow;
        var (identity, offer) = Proposal(now);
        try
        {
            string oldToken, pendingToken; Guid stage;
            await using (var vault = await MultiDeviceCredentialVault.OpenAsync(root, protector))
            {
                oldToken = await vault.AddConfirmedAsync("old", new string('C', 64));
                stage = (await vault.StagePendingGrantAsync(identity, offer, now, clock: () => now)).StageId;
                var frozen = await vault.FreezePendingInvitationForLoopbackTestAsync(stage, identity, IPAddress.Loopback, 24801, 24800, 24802, now, clock: () => now);
                var plain = protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root, "pending-grant.bin")));
                try { using var json = JsonDocument.Parse(plain); pendingToken = json.RootElement.GetProperty("token").GetString()!; }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Assert.False(vault.IsAuthorized("peer", pendingToken));
                var flow = new IsolatedConfirmation.Flow(identity, offer, stage, frozen, () => now);
                flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
                var confirmation = flow.Issue();
                var result = await vault.CommitIsolatedAsync(confirmation, now, () => now);
                Assert.Equal("committed", result);
                Assert.True(vault.IsAuthorized("old", oldToken)); Assert.True(vault.IsAuthorized("peer", pendingToken));
                Assert.Equal(2, vault.GetTrustedDevices().Single(x => x.Name == "peer").Slot);
                var active = protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin")));
                try { using var json = JsonDocument.Parse(active); Assert.Equal(2, json.RootElement.GetProperty("version").GetInt32()); Assert.Equal(1, json.RootElement.GetProperty("transactions").GetArrayLength()); }
                finally { CryptographicOperations.ZeroMemory(active); }
            }
            await using var reopened = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            Assert.True(reopened.IsAuthorized("old", oldToken)); Assert.True(reopened.IsAuthorized("peer", pendingToken));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task RetryAndExactRevokeNeverReviveOldGrantOrRemoveReplacement()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var plain=protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root,"pending-grant.bin"))); string token;
            try { using var json=JsonDocument.Parse(plain); token=json.RootElement.GetProperty("token").GetString()!; }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
            Assert.Equal("committed",await vault.CommitIsolatedAsync(permit,now,()=>now));
            var path=Path.Combine(root,"multi-device-trust.bin"); var committed=await File.ReadAllBytesAsync(path);
            Assert.Equal("committed",await vault.CommitIsolatedAsync(permit,now,()=>now));
            Assert.Equal(committed,await File.ReadAllBytesAsync(path));
            var tokenBytes=Convert.FromBase64String(token); string digest;
            try { using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(System.Text.Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0")); hash.AppendData(tokenBytes); digest=Convert.ToHexString(hash.GetHashAndReset()); }
            finally { CryptographicOperations.ZeroMemory(tokenBytes); }
            var key=new IsolatedGrantKey(identity,stage,offer.Platform,frozen.Slot,frozen.InvitationDigest,digest);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.RevokeIsolatedAsync(key with { Stage=Guid.NewGuid() }));
            Assert.True(vault.IsAuthorized("peer",token));
            Assert.Equal("revoked",await vault.RevokeIsolatedAsync(key));
            Assert.False(vault.IsAuthorized("peer",token));
            var replacement=await vault.AddConfirmedAsync("peer",identity.PeerFingerprint);
            Assert.Equal("revoked",await vault.RevokeIsolatedAsync(key));
            Assert.True(vault.IsAuthorized("peer",replacement));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(permit,now,()=>now));
            Assert.True(vault.IsAuthorized("peer",replacement));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task DurableAbortStopsSameTransactionAfterPendingDeletionAndRestart()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            Guid stage;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
                Assert.Equal("aborted",await vault.AbortIsolatedAsync(stage,identity));
                Assert.Equal("aborted",await vault.AbortIsolatedAsync(stage,identity));
                await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(permit,now,()=>now));
                Assert.True(await vault.AbortPendingGrantAsync(stage,identity));
                Assert.Empty(vault.GetTrustedDevices());
            }
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var newStage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                Assert.NotEqual(stage,newStage);
                var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(newStage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                var flow=new IsolatedConfirmation.Flow(identity,offer,newStage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
                await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
                Assert.Empty(vault.GetTrustedDevices());
            }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task SingleConfirmationClosedCapabilityAndSlotConflictCannotGrantTrust()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity);
            Assert.Throws<InvalidOperationException>(()=>flow.Issue());
            Assert.Throws<InvalidOperationException>(()=>flow.ConfirmRemote(identity with { SessionId=Guid.NewGuid() }));
            flow.ConfirmRemote(identity); var permit=flow.Issue();
            await flow.CloseAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(permit,now,()=>now));
            Assert.Empty(vault.GetTrustedDevices());
            var active=await vault.AddConfirmedAsync("competing",new string('C',64));
            var other=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); other.ConfirmLocal(identity); other.ConfirmRemote(identity);
            var before=await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin"));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(other.Issue(),now,()=>now));
            Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
            Assert.True(vault.IsAuthorized("competing",active));
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task LockedReplacementRejectsFurtherAuthorizationUntilReopen()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            string old;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                old=await vault.AddConfirmedAsync("old",new string('C',64));
                var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
                var before=await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin"));
                using (var locked=new FileStream(Path.Combine(root,"multi-device-trust.bin"),FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    var error=await Record.ExceptionAsync(()=>vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
                    Assert.True(error is IOException or UnauthorizedAccessException);
                    Assert.False(vault.IsAuthorized("old",old));
                }
                Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
            }
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.True(reopened.IsAuthorized("old",old)); Assert.Single(reopened.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task FullLedgerRejectsNewCommitAndAbortWithoutDroppingRecords()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow;
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var old=await vault.AddConfirmedAsync("old",new string('C',64));
            for (var i=0;i<64;i++)
            {
                var (identity,offer)=Proposal(now);
                var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                Assert.Equal("aborted",await vault.AbortIsolatedAsync(stage,identity));
                Assert.True(await vault.AbortPendingGrantAsync(stage,identity));
            }
            var (nextIdentity,nextOffer)=Proposal(now);
            var nextStage=(await vault.StagePendingGrantAsync(nextIdentity,nextOffer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(nextStage,nextIdentity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(nextIdentity,nextOffer,nextStage,frozen,()=>now); flow.ConfirmLocal(nextIdentity); flow.ConfirmRemote(nextIdentity);
            var path=Path.Combine(root,"multi-device-trust.bin"); var before=await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.AbortIsolatedAsync(nextStage,nextIdentity));
            Assert.Equal(before,await File.ReadAllBytesAsync(path)); Assert.True(vault.IsAuthorized("old",old));
        }
        finally { Clean(root); }
    }

    [Theory]
    [InlineData("expire")]
    [InlineData("rollback")]
    [InlineData("cancel")]
    [InlineData("protect")]
    public async Task FailureBeforePublishPreservesActiveAndPending(string failure)
    {
        var root=Root(); var protector=new ControlledProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now); long ticks=now.UtcTicks;
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var old=await vault.AddConfirmedAsync("old",new string('C',64));
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            DateTimeOffset Clock()=>new(Interlocked.Read(ref ticks),TimeSpan.Zero);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,Clock); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
            var activePath=Path.Combine(root,"multi-device-trust.bin"); var pendingPath=Path.Combine(root,"pending-grant.bin");
            var active=await File.ReadAllBytesAsync(activePath); var pending=await File.ReadAllBytesAsync(pendingPath);
            using var cancel=new CancellationTokenSource();
            protector.BeforeProtect=()=>
            {
                if(failure=="expire") Interlocked.Exchange(ref ticks,identity.ExpiresAtUtc.UtcTicks);
                if(failure=="rollback") Interlocked.Exchange(ref ticks,now.AddSeconds(-1).UtcTicks);
                if(failure=="cancel") cancel.Cancel();
                if(failure=="protect") throw new CryptographicException("Synthetic protection failure.");
            };
            var error=await Record.ExceptionAsync(()=>vault.CommitIsolatedAsync(permit,now,Clock,cancel.Token));
            Assert.True(failure switch { "expire" => error is ArgumentException, "rollback" => error is InvalidOperationException,
                "cancel" => error is OperationCanceledException, "protect" => error is CryptographicException, _ => false }, $"Unexpected failure: {error}");
            Assert.Equal(active,await File.ReadAllBytesAsync(activePath)); Assert.Equal(pending,await File.ReadAllBytesAsync(pendingPath));
            Assert.True(vault.IsAuthorized("old",old)); Assert.Single(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ChangedBindingsAndMissingCapabilityCannotCommit()
    {
        var root=Root(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,new WindowsCredentialProtector());
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            await Assert.ThrowsAsync<ArgumentNullException>(()=>vault.CommitIsolatedAsync(null!,now,()=>now));
            var identities=new[] { identity with { TransactionId=Guid.NewGuid() }, identity with { SessionId=Guid.NewGuid() },
                identity with { OfferNonce=Guid.NewGuid() }, identity with { HostName="other-host" }, identity with { PeerName="other-peer" },
                identity with { HostFingerprint=new string('D',64) }, identity with { PeerFingerprint=new string('E',64) }, identity with { ExpiresAtUtc=identity.ExpiresAtUtc.AddSeconds(-1) } };
            foreach(var changed in identities)
            {
                var error=await Record.ExceptionAsync(async () => { var flow=new IsolatedConfirmation.Flow(changed,offer,stage,frozen,()=>now); flow.ConfirmLocal(changed); flow.ConfirmRemote(changed);
                    await vault.CommitIsolatedAsync(flow.Issue(),now,()=>now); });
                Assert.True(error is ArgumentException or InvalidOperationException);
            }
            var frozenChanges=new[] { frozen with { Slot=2 }, frozen with { Host="127.0.0.2" }, frozen with { ControlPort=24901 },
                frozen with { KeyboardPort=24900 }, frozen with { DataPortBase=24902 }, frozen with { DataPort=24902 }, frozen with { InvitationDigest=new string('F',64) } };
            foreach(var changed in frozenChanges)
            {
                var flow=new IsolatedConfirmation.Flow(identity,offer,stage,changed,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
                await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
            }
            var changedStage=Guid.NewGuid(); var other=new IsolatedConfirmation.Flow(identity,offer,changedStage,frozen with { StageId=changedStage },()=>now);
            other.ConfirmLocal(identity); other.ConfirmRemote(identity);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(other.Issue(),now,()=>now));
            Assert.Empty(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ConcurrentRepeatedCommitProducesOneCurrentGrant()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
            var results=await Task.WhenAll(Task.Run(()=>vault.CommitIsolatedAsync(permit,now,()=>now)),Task.Run(()=>vault.CommitIsolatedAsync(permit,now,()=>now)));
            Assert.All(results,result=>Assert.Equal("committed",result)); Assert.Single(vault.GetTrustedDevices());
            var plain=protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
            try { using var json=JsonDocument.Parse(plain); Assert.Equal(1,json.RootElement.GetProperty("transactions").GetArrayLength()); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task InterruptedAfterReplacementFailsClosedAndReopenInspectsWithoutWriting()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            IsolatedGrantKey key; string token, old;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                old=await vault.AddConfirmedAsync("old",new string('C',64));
                var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                var plain=protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root,"pending-grant.bin")));
                try { using var json=JsonDocument.Parse(plain); token=json.RootElement.GetProperty("token").GetString()!; }
                finally { CryptographicOperations.ZeroMemory(plain); }
                var tokenBytes=Convert.FromBase64String(token); string digest;
                try { using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(System.Text.Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0")); hash.AppendData(tokenBytes); digest=Convert.ToHexString(hash.GetHashAndReset()); }
                finally { CryptographicOperations.ZeroMemory(tokenBytes); }
                key=new(identity,stage,offer.Platform,frozen.Slot,frozen.InvitationDigest,digest);
                var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
                vault.AfterIsolatedMove=()=>throw new IOException("Synthetic interruption after replacement.");
                await Assert.ThrowsAsync<IOException>(()=>vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
                Assert.False(vault.IsAuthorized("old",old)); Assert.False(vault.IsAuthorized("peer",token));
            }
            await using (var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var path=Path.Combine(root,"multi-device-trust.bin"); var before=await File.ReadAllBytesAsync(path);
                Assert.Equal("committed",await reopened.InspectIsolatedAsync(key));
                Assert.True(reopened.IsAuthorized("old",old)); Assert.True(reopened.IsAuthorized("peer",token));
                Assert.Equal(before,await File.ReadAllBytesAsync(path));
                await Assert.ThrowsAsync<InvalidOperationException>(()=>reopened.InspectIsolatedAsync(key with { Stage=Guid.NewGuid() }));
            }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task CommittedReceiptSurvivesPendingCleanupAndRestartWithoutCreatingGrant()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            IsolatedCommitReceipt receipt; IsolatedConfirmation permit;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); permit=flow.Issue();
                receipt=await vault.CommitAndLocateIsolatedAsync(permit,now,()=>now);
                Assert.Equal("committed",receipt.State);
                Assert.True(await vault.AbortPendingGrantAsync(stage,identity));
                Assert.DoesNotContain(receipt.Locator.CredentialDigest,receipt.ToString());
            }
            await using (var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var path=Path.Combine(root,"multi-device-trust.bin"); var bytes=await File.ReadAllBytesAsync(path);
                Assert.Equal("committed",await reopened.InspectIsolatedAsync(receipt.Locator));
                Assert.Equal("committed",await reopened.ConfirmExistingIsolatedAsync(permit,receipt.Locator));
                Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
                await Assert.ThrowsAsync<InvalidOperationException>(()=>reopened.ConfirmExistingIsolatedAsync(permit,receipt.Locator with { Stage=Guid.NewGuid() }));
                Assert.Equal("revoked",await reopened.RevokeIsolatedAsync(receipt.Locator));
                await Assert.ThrowsAsync<InvalidOperationException>(()=>reopened.ConfirmExistingIsolatedAsync(permit,receipt.Locator));
                Assert.Empty(reopened.GetTrustedDevices());
            }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task AbortedReceiptSurvivesPendingCleanupAndRestartWithoutGrant()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            IsolatedCommitReceipt receipt;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                receipt=await vault.AbortAndLocateIsolatedAsync(stage,identity);
                Assert.Equal("aborted",receipt.State); Assert.True(await vault.AbortPendingGrantAsync(stage,identity));
            }
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.Equal("aborted",await reopened.InspectIsolatedAsync(receipt.Locator));
            Assert.Empty(reopened.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ClosingConfirmationWaitsForInProgressPublicationAndThenDisablesIt()
    {
        var root=Root(); var protector=new ControlledProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
            protector.BeforeProtect=()=> { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
            var commit=Task.Run(()=>vault.CommitIsolatedAsync(permit,now,()=>now));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var close=flow.CloseAsync(); Assert.False(close.IsCompleted);
                release.Set(); Assert.Equal("committed",await commit.WaitAsync(TimeSpan.FromSeconds(5))); await close.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(permit,now,()=>now));
                Assert.Single(vault.GetTrustedDevices());
            }
            finally { release.Set(); }
        }
        finally { Clean(root); }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("revoke")]
    public async Task OrdinaryMutationWaitsForCommitAndPreservesTransaction(string operation)
    {
        var root=Root(); var protector=new ControlledProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
            var first=1; protector.BeforeProtect=()=> { if(Interlocked.Exchange(ref first,0)==1) { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); } };
            var commit=Task.Run(()=>vault.CommitIsolatedAsync(permit,now,()=>now));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Task mutation=operation=="add" ? vault.AddConfirmedAsync("other",new string('C',64)) : vault.RevokeAsync("peer");
                Assert.False(mutation.IsCompleted); release.Set();
                Assert.Equal("committed",await commit.WaitAsync(TimeSpan.FromSeconds(5))); await mutation.WaitAsync(TimeSpan.FromSeconds(5));
                var plain=protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
                try { using var json=JsonDocument.Parse(plain); Assert.Equal(1,json.RootElement.GetProperty("transactions").GetArrayLength());
                    Assert.Equal(operation=="add" ? "committed" : "revoked",json.RootElement.GetProperty("transactions")[0].GetProperty("state").GetString()); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Assert.Equal(operation=="add" ? 2 : 0,vault.GetTrustedDevices().Count);
            }
            finally { release.Set(); }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task ExactOldRevokeCannotRemoveConcurrentSameNameReplacement()
    {
        var root=Root(); var protector=new ControlledProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
            var receipt=await vault.CommitAndLocateIsolatedAsync(flow.Issue(),now,()=>now);
            Assert.True(await vault.RevokeAsync("peer"));
            protector.BeforeProtect=()=> { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); };
            var add=Task.Run(()=>vault.AddConfirmedAsync("peer",identity.PeerFingerprint));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var oldRevoke=vault.RevokeIsolatedAsync(receipt.Locator); Assert.False(oldRevoke.IsCompleted);
                release.Set(); var replacement=await add.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("revoked",await oldRevoke.WaitAsync(TimeSpan.FromSeconds(5))); Assert.True(vault.IsAuthorized("peer",replacement));
            }
            finally { release.Set(); }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task RecoveryWithoutReceiptUsesPendingAndCannotCreateGrant()
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            IsolatedConfirmation permit;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
                var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
                var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); permit=flow.Issue();
                await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.RecoverFromPendingIsolatedAsync(permit));
                Assert.Empty(vault.GetTrustedDevices());
                vault.AfterIsolatedMove=()=>throw new IOException("Synthetic interruption after replacement.");
                await Assert.ThrowsAsync<IOException>(()=>vault.CommitAndLocateIsolatedAsync(permit,now,()=>now));
            }
            await using (var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var activePath=Path.Combine(root,"multi-device-trust.bin"); var pendingPath=Path.Combine(root,"pending-grant.bin");
                var active=await File.ReadAllBytesAsync(activePath); var pending=await File.ReadAllBytesAsync(pendingPath);
                var recovered=await reopened.RecoverFromPendingIsolatedAsync(permit);
                Assert.Equal("committed",recovered.State); Assert.Equal("committed",await reopened.InspectIsolatedAsync(recovered.Locator));
                Assert.Equal(active,await File.ReadAllBytesAsync(activePath)); Assert.Equal(pending,await File.ReadAllBytesAsync(pendingPath));
                Assert.Single(reopened.GetTrustedDevices());
            }
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task DifferentTransactionCannotReuseAbortedSessionAndOffer()
    {
        var root=Root(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,new WindowsCredentialProtector());
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            await vault.AbortIsolatedAsync(stage,identity); Assert.True(await vault.AbortPendingGrantAsync(stage,identity));
            var replay=identity with { TransactionId=Guid.NewGuid() };
            var nextStage=(await vault.StagePendingGrantAsync(replay,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(nextStage,replay,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(replay,offer,nextStage,frozen,()=>now); flow.ConfirmLocal(replay); flow.ConfirmRemote(replay);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.AbortIsolatedAsync(nextStage,replay));
            Assert.Empty(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Theory]
    [InlineData("platform")]
    [InlineData("expiry")]
    [InlineData("version")]
    public async Task ReplacedOfferAfterConfirmationCannotBeCommitted(string change)
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
            var path=Path.Combine(root,"pending-grant.bin"); var plain=protector.Unprotect(await File.ReadAllBytesAsync(path));
            try
            {
                var json=System.Text.Json.Nodes.JsonNode.Parse(plain)!;
                if(change=="platform") json["offer"]!["platform"]="mac";
                if(change=="expiry") json["offer"]!["expiresAtUtc"]=offer.ExpiresAtUtc.AddSeconds(1);
                if(change=="version") json["offer"]!["version"]=2;
                var altered=JsonSerializer.SerializeToUtf8Bytes(json);
                try { await File.WriteAllBytesAsync(path,protector.Protect(altered)); }
                finally { CryptographicOperations.ZeroMemory(altered); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var before=await File.ReadAllBytesAsync(path);
            var error=await Record.ExceptionAsync(()=>vault.CommitIsolatedAsync(permit,now,()=>now));
            Assert.True(error is InvalidOperationException or InvalidDataException,$"Unexpected result: {error}");
            Assert.Equal(before,await File.ReadAllBytesAsync(path)); Assert.Empty(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Fact]
    public async Task RecoveryRejectsCapabilityExpirationDuringPendingRead()
    {
        var root=Root(); var protector=new ControlledProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now); long ticks=now.UtcTicks;
        try
        {
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            DateTimeOffset Clock()=>new(Interlocked.Read(ref ticks),TimeSpan.Zero);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,Clock); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity); var permit=flow.Issue();
            Assert.Equal("committed",await vault.CommitIsolatedAsync(permit,now,Clock));
            var before=await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin"));
            protector.BeforeUnprotect=()=>Interlocked.Exchange(ref ticks,identity.ExpiresAtUtc.UtcTicks);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>vault.RecoverFromPendingIsolatedAsync(permit));
            Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
            Assert.Single(vault.GetTrustedDevices());
        }
        finally { Clean(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyTokenMigrationNormalizesEquivalentEncodingButRejectsDuplicate(bool duplicate)
    {
        var root=Root(); var protector=new WindowsCredentialProtector(); var now=DateTimeOffset.UtcNow; var (identity,offer)=Proposal(now);
        var canonical=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var index=alphabet.IndexOf(canonical[42]); var noncanonical=canonical[..42]+alphabet[index+1]+canonical[43..];
        Assert.True(Convert.FromBase64String(canonical).SequenceEqual(Convert.FromBase64String(noncanonical)));
        try
        {
            Directory.CreateDirectory(root);
            var devices=duplicate ? new[] { new { name="old",fingerprint=new string('C',64),token=noncanonical,platform="windows",slot=1 }, new { name="other",fingerprint=new string('D',64),token=canonical,platform="mac",slot=2 } }
                : new[] { new { name="old",fingerprint=new string('C',64),token=noncanonical,platform="windows",slot=1 } };
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new { version=1,devices });
            try { await File.WriteAllBytesAsync(Path.Combine(root,"multi-device-trust.bin"),protector.Protect(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.True(vault.IsAuthorized("old",canonical));
            var stage=(await vault.StagePendingGrantAsync(identity,offer,now,clock:()=>now)).StageId;
            var frozen=await vault.FreezePendingInvitationForLoopbackTestAsync(stage,identity,IPAddress.Loopback,24801,24800,24802,now,clock:()=>now);
            var flow=new IsolatedConfirmation.Flow(identity,offer,stage,frozen,()=>now); flow.ConfirmLocal(identity); flow.ConfirmRemote(identity);
            var path=Path.Combine(root,"multi-device-trust.bin"); var before=await File.ReadAllBytesAsync(path);
            if(duplicate)
            {
                await Assert.ThrowsAsync<InvalidDataException>(()=>vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
                Assert.Equal(before,await File.ReadAllBytesAsync(path)); Assert.Equal(2,vault.GetTrustedDevices().Count);
            }
            else
            {
                Assert.Equal("committed",await vault.CommitIsolatedAsync(flow.Issue(),now,()=>now));
                Assert.True(vault.IsAuthorized("old",canonical));
                var plain=protector.Unprotect(await File.ReadAllBytesAsync(path));
                try { using var json=JsonDocument.Parse(plain); Assert.True(canonical.AsSpan().SequenceEqual(json.RootElement.GetProperty("devices")[0].GetProperty("token").GetString().AsSpan())); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
        }
        finally { Clean(root); }
    }

    private sealed class ControlledProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner=new(); internal Action? BeforeProtect { get; set; } internal Action? BeforeUnprotect { get; set; }
        public byte[] Protect(byte[] plaintext) { BeforeProtect?.Invoke(); return inner.Protect(plaintext); }
        public byte[] Unprotect(byte[] ciphertext) { BeforeUnprotect?.Invoke(); return inner.Unprotect(ciphertext); }
    }
}
