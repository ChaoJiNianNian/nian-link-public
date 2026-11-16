using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDiscoveryTestStartupTests
{
    [Fact]
    public void NormalLaunchRequiresThePreparedRandomProfile()
    {
        var home = FreshHome();
        var parent = TestParent(home);
        var profile = Path.Combine(parent, "profile");
        Directory.CreateDirectory(profile);
        try
        {
            var appData = Path.Combine(home, "Library", "Application Support");
            Assert.Equal(profile, new MacStartupOptions(profile, false).ResolveDiscoveryTestProfile(appData, home).DataDirectory);
            Assert.Throws<ArgumentException>(() => new MacStartupOptions(null, false).ResolveDiscoveryTestProfile(appData, home));
            Assert.Throws<ArgumentException>(() => new MacStartupOptions(Path.Combine(parent, "other"), false).ResolveDiscoveryTestProfile(appData, home));
            Assert.Throws<ArgumentException>(() => new MacStartupOptions(Path.Combine(home, "outside", "profile"), false).ResolveDiscoveryTestProfile(appData, home));
        }
        finally { Directory.Delete(profile); Directory.Delete(parent); Directory.Delete(Path.Combine(home, "Projects")); Directory.Delete(home); }
    }

    [Fact]
    public void StartupCheckUsesAnotherNewPathAndNeverCreatesIt()
    {
        var home = FreshHome();
        var parent = TestParent(home);
        var check = Path.Combine(parent, "startup-check-" + new string('b', 32));
        Directory.CreateDirectory(parent);
        try
        {
            var appData = Path.Combine(home, "Library", "Application Support");
            var options = new MacStartupOptions(check, true).ResolveDiscoveryTestProfile(appData, home);
            options.ValidateStartupCheckIsolation();
            Assert.False(Directory.Exists(check));
            Assert.Throws<ArgumentException>(() => new MacStartupOptions(Path.Combine(parent, "profile"), true).ResolveDiscoveryTestProfile(appData, home));
            Directory.CreateDirectory(check);
            Assert.Throws<IOException>(() => new MacStartupOptions(check, true).ResolveDiscoveryTestProfile(appData, home));
            Directory.Delete(check);
        }
        finally { Directory.Delete(parent); Directory.Delete(Path.Combine(home, "Projects")); Directory.Delete(home); }
    }

    [Fact]
    public void ProfileAndParentMustExistAndUseTheExactRandomName()
    {
        var home = FreshHome();
        var appData = Path.Combine(home, "Library", "Application Support");
        var profile = Path.Combine(TestParent(home), "profile");
        Assert.Throws<IOException>(() => new MacStartupOptions(profile, false).ResolveDiscoveryTestProfile(appData, home));
        var invalidParent = Path.Combine(home, "Projects", "nian-link-test-NOT-RANDOM", "profile");
        Assert.Throws<ArgumentException>(() => new MacStartupOptions(invalidParent, false).ResolveDiscoveryTestProfile(appData, home));
        Assert.False(Directory.Exists(home));
    }

    private static string FreshHome() => Path.Combine(Path.GetTempPath(), "nian-test-home-" + Guid.NewGuid().ToString("N"));
    private static string TestParent(string home) => Path.Combine(home, "Projects", "nian-link-test-" + new string('a', 32));
}
