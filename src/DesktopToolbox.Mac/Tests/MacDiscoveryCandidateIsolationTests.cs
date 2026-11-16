using DesktopToolbox.Mac.Models;
using Nian.Input.Security;
using System.Diagnostics;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDiscoveryCandidateIsolationTests
{
    [Fact]
    public void NormalLaunchUsesOnlyTheDedicatedProfile()
    {
        var root = FreshRoot();
        var expected = Path.Combine(root, "NianLink", "DiscoveryCandidate");
        Assert.Equal(expected, new MacStartupOptions(null, false).ResolveDiscoveryCandidateProfile(root).DataDirectory);
        Assert.Equal(expected, new MacStartupOptions(expected, false).ResolveDiscoveryCandidateProfile(root).DataDirectory);
        Assert.Throws<ArgumentException>(() => new MacStartupOptions(Path.Combine(root, "other"), false).ResolveDiscoveryCandidateProfile(root));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void CheckProfilesCannotOverlapExistingProductProfiles()
    {
        var root = FreshRoot();
        var profiles = new[]
        {
            Path.Combine(root, "NianDesktopToolbox", "DeskLink"),
            Path.Combine(root, "NianLink", "DeskLink"),
            Path.Combine(root, "NianLink", "MultiDeviceCandidate"),
            Path.Combine(root, "NianLink", "PreviewControlCandidate"),
            Path.Combine(root, "NianLink", "NativeInputCandidate"),
            Path.Combine(root, "NianLink", "DiscoveryCandidate")
        };
        foreach (var profile in profiles)
        {
            foreach (var forbidden in new[] { profile, Path.Combine(profile, "child"), Path.GetDirectoryName(profile)! })
                Assert.Throws<ArgumentException>(() => new MacStartupOptions(forbidden, true).ResolveDiscoveryCandidateProfile(root));
        }
        Assert.Throws<ArgumentException>(() => new MacStartupOptions(null, true).ResolveDiscoveryCandidateProfile(root));
        Assert.Throws<ArgumentException>(() => new MacStartupOptions(null, false).ResolveDiscoveryCandidateProfile("relative"));
    }

    [Fact]
    public void CheckProfileMustBeNewAndNeverCreatesIt()
    {
        var root = FreshRoot();
        var check = Path.Combine(root, "check");
        var options = new MacStartupOptions(check, true).ResolveDiscoveryCandidateProfile(root);
        options.ValidateStartupCheckIsolation();
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(check);
        try
        {
            Assert.Throws<IOException>(() => new MacStartupOptions(check, true).ResolveDiscoveryCandidateProfile(root).ValidateStartupCheckIsolation());
        }
        finally { Directory.Delete(check); Directory.Delete(root); }
    }

    [Fact]
    public void AFileCannotBeUsedAsTheDedicatedProfile()
    {
        var root = FreshRoot();
        var product = Path.Combine(root, "NianLink");
        var profile = Path.Combine(product, "DiscoveryCandidate");
        Directory.CreateDirectory(product);
        File.WriteAllText(profile, "test");
        try { Assert.Throws<IOException>(() => new MacStartupOptions(null, false).ResolveDiscoveryCandidateProfile(root)); }
        finally { File.Delete(profile); Directory.Delete(product); Directory.Delete(root); }
    }

    [Fact]
    public void LinkedApplicationDataRootCannotHideAProtectedProfile()
    {
        var root = FreshRoot();
        var actual = Path.Combine(root, "actual");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(actual);
        try
        {
            CreateTestLink(alias, actual);
            var hiddenProductionChild = Path.Combine(actual, "NianLink", "DeskLink", "new");
            Assert.Throws<IOException>(() => new MacStartupOptions(hiddenProductionChild, true).ResolveDiscoveryCandidateProfile(alias));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(actual);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void LinkedProtectedProfileCannotHideItsActualLocation()
    {
        var root = FreshRoot();
        var product = Path.Combine(root, "NianLink");
        var actual = Path.Combine(root, "actual");
        var alias = Path.Combine(product, "DeskLink");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(actual);
        try
        {
            CreateTestLink(alias, actual);
            Assert.Throws<IOException>(() => new MacStartupOptions(Path.Combine(actual, "new"), true).ResolveDiscoveryCandidateProfile(root));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(actual);
            Directory.Delete(product);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void DiscoveryVaultHasASeparateFixedServiceAndDefaultIsUnchanged()
    {
        var legacy = new MacKeychainIdentityVault();
        var discovery = MacKeychainIdentityVault.CreateDiscoveryCandidate();
        var fileless = MacKeychainIdentityVault.CreateFilelessCandidate();
        Assert.Equal("app.niannian.nianlink-standalone.identity.v1", legacy.IdentityServiceName);
        Assert.Equal("app.niannian.nianlink-discovery-test.identity.v1", discovery.IdentityServiceName);
        Assert.NotEqual(legacy.IdentityServiceName, discovery.IdentityServiceName);
        Assert.Equal("app.niannian.nianlink-fileless-test.identity.v1", fileless.IdentityServiceName);
        Assert.NotEqual(legacy.IdentityServiceName, fileless.IdentityServiceName);
        Assert.NotEqual(discovery.IdentityServiceName, fileless.IdentityServiceName);
    }

    private static string FreshRoot() => Path.Combine(Path.GetTempPath(), "nian-discovery-boundary-" + Guid.NewGuid().ToString("N"));

    private static void CreateTestLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not create test junction.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
