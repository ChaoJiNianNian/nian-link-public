using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace VaultQuarantine.Research;

public sealed class VaultQuarantineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateDecodedCredentialRejectsOpenAfterRestartWithoutChangingFile(bool alternateEncoding)
    {
        var fixture = await CreateAsync(1, alternateEncoding ? "duplicate-alternate" : "duplicate-canonical");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(fixture.File));
        for (var restart = 0; restart < 2; restart++)
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await using var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector);
            });
            var after = SHA256.HashData(await File.ReadAllBytesAsync(fixture.File));
            Assert.True(CryptographicOperations.FixedTimeEquals(before, after));
        }
    }

    [Theory]
    [InlineData(1, "single-alternate")]
    [InlineData(1, "distinct")]
    [InlineData(2, "distinct")]
    [InlineData(3, "distinct")]
    public async Task ValidLegacyAndCurrentDocumentsOpenWithoutRewrite(int version, string scenario)
    {
        var fixture = await CreateAsync(version, scenario);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(fixture.File));
        await using (var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector))
        {
            Assert.True(vault.IsAuthorized("alpha", fixture.Token));
            Assert.Equal(scenario == "single-alternate" ? 1 : 2, vault.GetTrustedDevices().Count);
        }
        Assert.True(CryptographicOperations.FixedTimeEquals(before, SHA256.HashData(await File.ReadAllBytesAsync(fixture.File))));
    }

    [Theory]
    [InlineData("short-decoded")]
    [InlineData("invalid-base64")]
    public async Task InvalidLegacyCredentialIsRejectedWithoutRewrite(string scenario)
    {
        var fixture = await CreateAsync(1, scenario);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(fixture.File));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using var vault = await MultiDeviceCredentialVault.OpenAsync(fixture.Directory, fixture.Protector);
        });
        Assert.True(CryptographicOperations.FixedTimeEquals(before, SHA256.HashData(await File.ReadAllBytesAsync(fixture.File))));
    }

    private sealed record Fixture(string Directory, string File, WindowsCredentialProtector Protector, string Token);
    private static async Task<Fixture> CreateAsync(int version, string scenario)
    {
        var baseDirectory = typeof(VaultQuarantineTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "NianLegacyDuplicateRoot").Value ?? throw new InvalidOperationException("Missing isolated test directory.");
        var root = Path.Combine(Path.GetFullPath(baseDirectory), Guid.NewGuid().ToString("N"));
        RejectLinks(root); Directory.CreateDirectory(root); RejectLinks(root);
        var random = RandomNumberGenerator.GetBytes(32);
        string token;
        try { token = Convert.ToBase64String(random); }
        finally { CryptographicOperations.ZeroMemory(random); }
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var index = alphabet.IndexOf(token[42]);
        var alternate = token[..42] + alphabet[index + 1] + token[43..];
        string second;
        if (scenario.StartsWith("duplicate", StringComparison.Ordinal)) second = scenario == "duplicate-alternate" ? alternate : token;
        else
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            try { second = Convert.ToBase64String(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var first = scenario switch
        {
            "single-alternate" => alternate,
            "short-decoded" => Convert.ToBase64String(new byte[31]),
            "invalid-base64" => new string('@', 44),
            _ => token
        };
        var entries = new[] { new { name="alpha", fingerprint=new string('A',64), token=first, platform="windows", slot=1 },
            new { name="beta", fingerprint=new string('B',64), token=second, platform="mac", slot=2 } };
        var devices = scenario is "single-alternate" or "short-decoded" or "invalid-base64" ? entries[..1] : entries;
        object document = version switch
        {
            1 => new { version, devices },
            2 => new { version, devices, transactions = Array.Empty<object>() },
            3 => new { version, devices, transactions = Array.Empty<object>(), retired = "" },
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        };
        var plain = JsonSerializer.SerializeToUtf8Bytes(document);
        var protector = new WindowsCredentialProtector();
        var file = Path.Combine(root, "multi-device-trust.bin");
        try
        {
            RejectLinks(file);
            var encrypted = protector.Protect(plain);
            await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
            await stream.WriteAsync(encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return new Fixture(root, file, protector, token);
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
