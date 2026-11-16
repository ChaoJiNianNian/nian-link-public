using System.Security.Cryptography;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace LegacyDuplicateVault.Research;

// This fixture constructs a fresh encrypted v1 vault with synthetic identities;
// it never opens an existing application profile or reports credential values.
public sealed class LegacyDuplicateVaultTests
{
    [Theory]
    [InlineData("alpha")]
    [InlineData("beta")]
    public async Task DuplicateCredentialMustNotAuthorizeEitherDevice(string name)
    {
        var fixture = await CreateAsync(duplicate: true);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector);
        Assert.False(vault.IsAuthorized(name, fixture.Canonical));
    }

    [Fact]
    public async Task DuplicateCredentialMustNotBeExportedToPerDeviceEndpoint()
    {
        var fixture = await CreateAsync(duplicate: true);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector);
        byte[]? supplied = null;
        try
        {
            try { supplied = vault.ReadTokenForEndpoint("beta"); }
            catch (UnauthorizedAccessException) { return; }
            Assert.Fail("Ambiguous credential was exposed to a per-device endpoint.");
        }
        finally { if (supplied is not null) CryptographicOperations.ZeroMemory(supplied); }
    }

    [Fact]
    public async Task DuplicateCredentialMustNotShareThePerDeviceSettingsKey()
    {
        var fixture = await CreateAsync(duplicate: true);
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector);
        await using var runtime = new MultiPeerRuntime(vault, Path.Combine(fixture.Directory, "runtime"), "host",
            System.Net.IPAddress.Loopback, 0, 0, _ => throw new InvalidOperationException("Synthetic test must not start a keyboard."));
        var key = typeof(MultiPeerRuntime).GetMethod("PeerSettingsKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (string)key.Invoke(runtime, ["alpha"])!;
        var second = (string)key.Invoke(runtime, ["beta"])!;
        // Report only a Boolean; do not write token-derived keys to test logs.
        Assert.False(string.Equals(first, second, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemovingOneDuplicateMustNotMakeItsOldCredentialAuthorizeTheOtherAfterReopen()
    {
        var fixture = await CreateAsync(duplicate: true);
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector))
            Assert.True(await vault.RevokeAsync("beta"));
        await using var reopened = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector);
        Assert.False(reopened.IsAuthorized("alpha", fixture.Canonical));
    }

    [Fact]
    public async Task SingleLegacyNoncanonicalCredentialStillAuthorizesWithoutRewritingVault()
    {
        var fixture = await CreateAsync(duplicate: false);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(fixture.File));
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector))
            Assert.True(vault.IsAuthorized("alpha", fixture.Canonical));
        var after = SHA256.HashData(await File.ReadAllBytesAsync(fixture.File));
        Assert.True(CryptographicOperations.FixedTimeEquals(before, after));
    }

    private sealed record Fixture(string Directory, string File, WindowsCredentialProtector Protector, string Canonical);

    private static async Task<Fixture> CreateAsync(bool duplicate)
    {
        var baseDirectory = typeof(LegacyDuplicateVaultTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "NianLegacyDuplicateRoot").Value ?? throw new InvalidOperationException("Missing isolated research directory.");
        var root = Path.Combine(Path.GetFullPath(baseDirectory), Guid.NewGuid().ToString("N"));
        RejectLinks(root);
        Directory.CreateDirectory(root);
        RejectLinks(root);
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        string canonical;
        try { canonical = Convert.ToBase64String(tokenBytes); }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var index = alphabet.IndexOf(canonical[42]);
        var noncanonical = canonical[..42] + alphabet[index + 1] + canonical[43..];
        var first = Convert.FromBase64String(canonical);
        var second = Convert.FromBase64String(noncanonical);
        try { Assert.True(CryptographicOperations.FixedTimeEquals(first, second)); }
        finally { CryptographicOperations.ZeroMemory(first); CryptographicOperations.ZeroMemory(second); }
        var devices = duplicate
            ? new[] { new { name = "alpha", fingerprint = new string('A', 64), token = noncanonical, platform = "windows", slot = 1 },
                      new { name = "beta", fingerprint = new string('B', 64), token = canonical, platform = "mac", slot = 2 } }
            : new[] { new { name = "alpha", fingerprint = new string('A', 64), token = noncanonical, platform = "windows", slot = 1 } };
        var plain = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, devices });
        var protector = new WindowsCredentialProtector();
        var file = Path.Combine(root, "multi-device-trust.bin");
        try
        {
            RejectLinks(file);
            var encrypted = protector.Protect(plain);
            await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await stream.WriteAsync(encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return new Fixture(root, file, protector, canonical);
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Research vault cannot use a linked path."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
