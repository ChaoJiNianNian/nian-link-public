using NianLink.MultiDevice;

public sealed class BetaPathPolicyTests
{
    [Fact]
    public void BetaDerivesOwnedPathsFromKnownFolderAndPackageRatherThanEnvironmentArguments()
    {
        using var f = new Fixture();
        Assert.True(BetaPathPolicy.TryResolveUnderRoot(["--beta-portable"], f.ProfileRoot, f.App, out var args));
        Assert.Equal(f.Args, args);
        Assert.False(BetaPathPolicy.TryResolveUnderRoot(["--beta-portable", f.Profile], f.ProfileRoot, f.App, out _));
        File.WriteAllText(f.Marker, "../../escape");
        Assert.False(BetaPathPolicy.TryResolveUnderRoot(["--beta-portable"], f.ProfileRoot, f.App, out _));
    }

    [Fact]
    public void BetaRequiresItsOwnProfileAndTheAdjacentKeyboardCore()
    {
        using var f = new Fixture();
        Assert.True(BetaPathPolicy.IsLaunchUnderRoot(f.Args, f.ProfileRoot, f.App));
        Assert.False(BetaPathPolicy.IsLaunchUnderRoot([f.Profile, f.Engine, "--discovery-portable"], f.ProfileRoot, f.App));
        Assert.False(BetaPathPolicy.IsLaunchUnderRoot([Path.Combine(f.Root, "DesktopToolbox"), f.Engine, "--beta-portable"], f.ProfileRoot, f.App));
        Assert.False(BetaPathPolicy.IsLaunchUnderRoot([f.Profile, Path.Combine(f.Root, "other.exe"), "--beta-portable"], f.ProfileRoot, f.App));
    }

    [Fact]
    public void BetaRequiresValidMatchingPackageIdentity()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Marker, new string('b', 32));
        Assert.False(BetaPathPolicy.IsLaunchUnderRoot(f.Args, f.ProfileRoot, f.App));
        File.WriteAllText(f.Marker, "bad-id");
        Assert.False(BetaPathPolicy.IsLaunchUnderRoot(f.Args, f.ProfileRoot, f.App));
        File.Delete(f.Marker);
        Assert.False(BetaPathPolicy.IsLaunchUnderRoot(f.Args, f.ProfileRoot, f.App));
    }

    [Fact]
    public void BetaNeverAdoptsExistingUnownedProfile()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Profile);
        File.WriteAllText(Path.Combine(f.Profile, "other-data.txt"), "preserve");
        Assert.False(BetaPathPolicy.ClaimProfileUnderRoot(f.Profile, f.ProfileRoot));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(f.Profile, "other-data.txt")));
        Assert.False(File.Exists(Path.Combine(f.Profile, ".discovery-owner")));
    }

    [Fact]
    public void BetaCanReopenOnlyItsOwnedProfile()
    {
        using var f = new Fixture();
        Assert.True(BetaPathPolicy.ClaimProfileUnderRoot(f.Profile, f.ProfileRoot));
        Assert.True(BetaPathPolicy.ClaimProfileUnderRoot(f.Profile, f.ProfileRoot));
        Assert.False(BetaPathPolicy.ClaimProfileUnderRoot(Path.Combine(f.ProfileRoot, "test-" + new string('b', 32)), Path.Combine(f.Root, "different")));
    }

    [Fact]
    public void BetaRejectsJunctionInProfilePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var parent = Path.GetDirectoryName(f.ProfileRoot)!;
        var target = Path.Combine(f.Root, "unrelated-data");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(target);
        try
        {
            MakeJunction(f.ProfileRoot, target);
            Assert.False(BetaPathPolicy.IsLaunchUnderRoot(f.Args, f.ProfileRoot, f.App));
            Assert.False(BetaPathPolicy.ClaimProfileUnderRoot(f.Profile, f.ProfileRoot));
        }
        finally
        {
            if (Directory.Exists(f.ProfileRoot)) Directory.Delete(f.ProfileRoot);
            Directory.Delete(target);
        }
    }

    [Fact]
    public void BetaRejectsJunctionInCorePath()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var keyboard = Path.GetDirectoryName(f.Engine)!;
        var target = Path.Combine(f.Root, "foreign-keyboard");
        File.Delete(f.Engine);
        Directory.Delete(keyboard);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "deskflow-core.exe"), "test core");
        try
        {
            MakeJunction(keyboard, target);
            Assert.False(BetaPathPolicy.IsLaunchUnderRoot(f.Args, f.ProfileRoot, f.App));
        }
        finally
        {
            if (Directory.Exists(keyboard)) Directory.Delete(keyboard);
            File.Delete(Path.Combine(target, "deskflow-core.exe"));
            Directory.Delete(target);
            Directory.CreateDirectory(keyboard);
            File.WriteAllText(f.Engine, "test core");
        }
    }

    private static void MakeJunction(string path, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ArgumentList = { "/c", "mklink", "/J", path, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nian-beta-policy-" + Guid.NewGuid().ToString("N"));
        public string App => Path.Combine(Root, "App");
        public string ProfileRoot => Path.Combine(Root, "NianLinkMultiDeviceBeta", "Profiles");
        public string Profile => Path.Combine(ProfileRoot, "test-" + new string('a', 32));
        public string Engine => Path.Combine(Root, "Keyboard", "deskflow-core.exe");
        public string Marker => Path.Combine(App, "discovery-portable-id.txt");
        public string[] Args => [Profile, Engine, "--beta-portable"];
        public Fixture()
        {
            Directory.CreateDirectory(App);
            Directory.CreateDirectory(Path.GetDirectoryName(Engine)!);
            File.WriteAllText(Marker, new string('a', 32));
            File.WriteAllText(Engine, "test core");
        }
        public void Dispose()
        {
            if (File.Exists(Path.Combine(Profile, "other-data.txt"))) File.Delete(Path.Combine(Profile, "other-data.txt"));
            if (File.Exists(Path.Combine(Profile, ".discovery-owner"))) File.Delete(Path.Combine(Profile, ".discovery-owner"));
            if (Directory.Exists(Profile)) Directory.Delete(Profile);
            if (Directory.Exists(ProfileRoot)) Directory.Delete(ProfileRoot);
            var profilesParent = Path.GetDirectoryName(ProfileRoot)!;
            if (Directory.Exists(profilesParent)) Directory.Delete(profilesParent);
            if (File.Exists(Marker)) File.Delete(Marker);
            File.Delete(Engine);
            Directory.Delete(App);
            Directory.Delete(Path.GetDirectoryName(Engine)!);
            Directory.Delete(Root);
        }
    }
}
