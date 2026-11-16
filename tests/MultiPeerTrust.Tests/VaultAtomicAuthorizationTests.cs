using System.Security.Cryptography;
using System.IO;
using DesktopToolbox.DeskLink;

namespace MultiPeerTrust.Tests;

public sealed class VaultAtomicAuthorizationTests
{
    private sealed class Protector : IMultiDeviceSecretProtector
    {
        public bool Fail { get; set; }
        public byte[] Protect(byte[] value) => Fail ? throw new CryptographicException("Simulated write protection failure.") : value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    [Fact]
    public async Task CurrentGrantAloneCanCommitAndRepairedGrantDoesNotInheritIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "nian-vault-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(path, new Protector());
            var token = Convert.FromBase64String(await vault.AddConfirmedAsync("one", new string('1', 64)));
            var other = Convert.FromBase64String(await vault.AddConfirmedAsync("two", new string('2', 64)));
            var commits = 0;
            Assert.True(await vault.TryCommitAuthorizedAsync("one", token, () => commits++));
            Assert.False(await vault.TryCommitAuthorizedAsync("one", other, () => commits++));
            Assert.False(await vault.TryCommitAuthorizedAsync("two", token, () => commits++));
            Assert.False(await vault.TryCommitAuthorizedAsync("one", [1], () => commits++));
            Assert.Equal(1, commits);
            Assert.True(await vault.RevokeAsync("one"));
            await vault.AddConfirmedAsync("one", new string('3', 64));
            Assert.False(await vault.TryCommitAuthorizedAsync("one", token, () => commits++));
            Assert.True(await vault.TryCommitAuthorizedAsync("two", other, () => commits++));
            Assert.Equal(2, commits);
            CryptographicOperations.ZeroMemory(token); CryptographicOperations.ZeroMemory(other);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task FailedRevocationLeavesPreviousGrantValid()
    {
        var path = Path.Combine(Path.GetTempPath(), "nian-vault-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new Protector();
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(path, protector);
            var token = Convert.FromBase64String(await vault.AddConfirmedAsync("one", new string('1', 64)));
            protector.Fail = true;
            await Assert.ThrowsAsync<CryptographicException>(() => vault.RevokeAsync("one"));
            var count = 0;
            Assert.True(await vault.TryCommitAuthorizedAsync("one", token, () => count++));
            Assert.Equal(1, count);
            protector.Fail = false;
            Assert.True(await vault.RevokeAsync("one"));
            Assert.False(await vault.TryCommitAuthorizedAsync("one", token, () => count++));
            CryptographicOperations.ZeroMemory(token);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task CommitAndRevocationAreOrderedAndExceptionsReleaseTheGate()
    {
        var path = Path.Combine(Path.GetTempPath(), "nian-vault-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(path, new Protector());
            var token = Convert.FromBase64String(await vault.AddConfirmedAsync("one", new string('1', 64)));
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var committed = Task.Run(() => vault.TryCommitAuthorizedAsync("one", token, () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var revoke = vault.RevokeAsync("one");
                Assert.False(revoke.IsCompleted);
                release.Set();
                Assert.True(await committed.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(await revoke.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(await vault.TryCommitAuthorizedAsync("one", token, () => throw new Exception("must not run")));
                var newer = Convert.FromBase64String(await vault.AddConfirmedAsync("one", new string('3', 64)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => vault.TryCommitAuthorizedAsync("one", newer, () => throw new InvalidOperationException()));
                Assert.True(await vault.TryCommitAuthorizedAsync("one", newer, () => { }));
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.TryCommitAuthorizedAsync("one", newer, () => { }, cancelled.Token));
                Assert.True(await vault.RevokeAsync("one"));
                CryptographicOperations.ZeroMemory(newer);
            }
            finally { release.Set(); }
            CryptographicOperations.ZeroMemory(token);
            await vault.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => vault.TryCommitAuthorizedAsync("one", token, () => { }));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
