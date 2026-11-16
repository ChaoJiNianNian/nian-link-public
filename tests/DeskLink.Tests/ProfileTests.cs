namespace DeskLink.Tests;
using DesktopToolbox.DeskLink;
public sealed class ProfileTests
{
    [Fact]
    public async Task ServerProfileListsFourDistinctRemoteComputersWithoutChangingLegacyHotkeys()
    {
        using var folder = new TestFolder();
        var profile = await DeskflowProfile.PrepareAsync(Options(folder.Path) with
        {
            AdditionalRemoteNames = ["Win-Two", "Mac-Two", "Win-Three"]
        });
        var text = await File.ReadAllTextAsync(profile.SettingsPath);
        foreach (var name in new[] { "Win-PC", "My-Mac", "Win-Two", "Mac-Two", "Win-Three" })
            Assert.Contains("[computer_" + name + "]", text);
        Assert.Contains("screens\\8\\name=\"Win-PC\"", text);
        Assert.Contains("screens\\9\\name=\"My-Mac\"", text);
        Assert.Contains("screens\\7\\name=\"Win-Two\"", text);
        Assert.Contains("screens\\10\\name=\"Mac-Two\"", text);
        Assert.Contains("screens\\3\\name=\"Win-Three\"", text);
        var server = await File.ReadAllTextAsync(System.IO.Path.Combine(folder.Path, "server.conf"));
        Assert.Contains("switchToScreen(Win-PC)", server);
        Assert.Contains("switchToScreen(My-Mac)", server);
        Assert.Contains("tlsEnabled=true", text);
        Assert.Contains("checkPeerFingerprints=true", text);
    }

    [Theory]
    [InlineData("Win-PC")]
    [InlineData("my-mac")]
    [InlineData("Other\n[security]")]
    public void RejectsDuplicateOrUnsafeAdditionalNames(string name)
    {
        using var folder = new TestFolder();
        Assert.Throws<ArgumentException>(() => DeskflowProfile.Validate(Options(folder.Path) with { AdditionalRemoteNames = [name] }));
    }

    [Fact]
    public void RejectsFifthRemoteAndAdditionalClientNames()
    {
        using var folder = new TestFolder();
        Assert.Throws<ArgumentException>(() => DeskflowProfile.Validate(Options(folder.Path) with { AdditionalRemoteNames = ["A", "B", "C", "D"] }));
        Assert.Throws<ArgumentException>(() => DeskflowProfile.Validate(Options(folder.Path) with { Role = DeskLinkRole.Client, ServerAddress = "localhost", AdditionalRemoteNames = ["A"] }));
    }

    [Fact]
    public void ExistingSessionRefusesMultiDeviceUntilRoutingAndTrustAreReady()
    {
        using var folder = new TestFolder();
        Assert.Throws<NotSupportedException>(() => new DeskflowSession(
            Options(folder.Path) with { AdditionalRemoteNames = ["Win-Two"] }));
    }

    [Fact]
    public async Task ExistingSessionCannotBeChangedIntoMultiDeviceByMutatingCallerList()
    {
        using var folder = new TestFolder();
        var extraNames = new List<string>();
        await using var session = new DeskflowSession(
            Options(folder.Path) with { AdditionalRemoteNames = extraNames });
        extraNames.Add("Win-Two");
        var profile = await session.PrepareAsync();
        var text = await File.ReadAllTextAsync(profile.SettingsPath);
        Assert.DoesNotContain("[computer_Win-Two]", text);
    }
    [Fact]
    public async Task PreparesPrivateTlsProfileWithoutEnterExitHooksOrAutomaticTrust()
    {
        using var folder = new TestFolder();
        var options = Options(folder.Path);
        var profile = await DeskflowProfile.PrepareAsync(options);
        var text = await File.ReadAllTextAsync(profile.SettingsPath);
        Assert.Contains("tlsEnabled=true", text);
        Assert.Contains("checkPeerFingerprints=true", text);
        Assert.Contains("enableEnterCommand=false", text);
        Assert.Contains("enableExitCommand=false", text);
        Assert.Contains("[computer_My-Mac]", text);
        Assert.False(File.Exists(System.IO.Path.Combine(profile.TrustDirectory, "trusted-clients")));
        Assert.Equal(64, profile.LocalFingerprint.Length);
        Assert.Equal(profile.LocalFingerprint, (await DeskflowProfile.PrepareAsync(options)).LocalFingerprint);
    }
    [Theory]
    [InlineData("Mac\n[security]")]
    [InlineData("../Mac")]
    public async Task RejectsNamesThatCouldEscapeTheConfiguration(string name)
    {
        using var folder = new TestFolder();
        await Assert.ThrowsAsync<ArgumentException>(() => DeskflowProfile.PrepareAsync(Options(folder.Path) with { RemoteName = name }));
    }

    [Fact]
    public async Task RejectsUnsupportedProtocolAndPreservesExplicitKnownAdvancedSettings()
    {
        using var folder = new TestFolder();
        await Assert.ThrowsAsync<ArgumentException>(()=>DeskflowProfile.PrepareAsync(Options(folder.Path) with {Protocol="deskflow"}));
        var profile = await DeskflowProfile.PrepareAsync(Options(folder.Path) with
        {
            AdvancedSettings = new Dictionary<string,string> { ["server/win32KeepForeground"]="true", ["client/yScrollScale"]="1.25" }
        });
        var text = await File.ReadAllTextAsync(profile.SettingsPath);
        Assert.Contains("win32KeepForeground=true",text);
        Assert.Contains("yScrollScale=1.25",text);
        await Assert.ThrowsAsync<ArgumentException>(()=>DeskflowProfile.PrepareAsync(Options(folder.Path) with
        {
            AdvancedSettings = new Dictionary<string,string> { ["core/enterCommand"]="untrusted" }
        }));
    }

    internal static DeskflowSessionOptions Options(string path) => new()
    {
        EnginePath = System.IO.Path.Combine(path, "deskflow-core.exe"), ProfileDirectory = path,
        LocalName = "Win-PC", RemoteName = "My-Mac", Role = DeskLinkRole.Server
    };
}
internal sealed class TestFolder : IDisposable
{
    private static readonly string Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../.work/desklink-security"));
    public string Path { get; } = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
    public TestFolder()
    {
        EnsureNoLinks(Root);
        if (Directory.Exists(Path) || File.Exists(Path)) throw new IOException("Test fixture path already exists.");
        Directory.CreateDirectory(Path);
    }
    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(Path);
        if (!full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture cleanup is outside its test workspace.");
        EnsureNoLinks(full);
        if (!Directory.Exists(full)) return;
        // Do not follow or remove a reparse point even if a test or another
        // process unexpectedly introduces one; preserve the fixture instead.
        var pending = new Stack<string>(); pending.Push(full);
        while (pending.TryPop(out var directory))
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Fixture contains a link; it was retained.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        Directory.Delete(full, true);
    }
    private static void EnsureNoLinks(string path)
    {
        for (var current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Test path contains a link.");
    }
}
