using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacCandidateStartupTests
{
    private static string FreshApplicationData() => Path.Combine(Path.GetTempPath(), "nian-candidate-startup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NormalDoubleClickResolvesAStableSiblingOfProductionBeforeConstructingTheUi()
    {
        var applicationData = FreshApplicationData();
        var expected = Path.Combine(applicationData, "NianLink", "NativeInputCandidate");
        var options = MacStartupOptions.Parse([]).ResolveNativeCandidateProfile(applicationData);
        Assert.Equal(expected, options.DataDirectory);
        Assert.Equal(options, MacStartupOptions.Parse(["-psn_0_123"]).ResolveNativeCandidateProfile(applicationData));
        Assert.False(options.StartupCheck);
        Assert.False(Directory.Exists(applicationData));
    }

    [Fact]
    public void CandidateProfilesAlsoRejectStandaloneProductionData()
    {
        var applicationData = FreshApplicationData();
        var standalone = Path.Combine(applicationData, "NianLink", "DeskLink");
        foreach (var forbidden in new[] { standalone, Path.Combine(standalone, "preview-control"), Path.GetDirectoryName(standalone)! })
        {
            Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--data-dir", forbidden]).ResolveNativeCandidateProfile(applicationData));
            Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--data-dir", forbidden]).ResolvePreviewCandidateProfile(applicationData));
        }
        Assert.False(Directory.Exists(applicationData));
    }

    [Fact]
    public void ExplicitIsolatedProfileIsPreserved()
    {
        var applicationData = FreshApplicationData();
        var explicitProfile = Path.Combine(applicationData, "explicit-candidate");
        var options = MacStartupOptions.Parse(["--data-dir", explicitProfile]).ResolveNativeCandidateProfile(applicationData);
        Assert.Equal(explicitProfile, options.DataDirectory);
        Assert.False(Directory.Exists(applicationData));
    }

    [Fact]
    public void ResolvedCandidateStillRejectsTheProductionDirectoryItsDescendantAndItsAncestor()
    {
        var applicationData = FreshApplicationData();
        var production = Path.Combine(applicationData, "NianDesktopToolbox", "DeskLink");
        foreach (var forbidden in new[] { production, Path.Combine(production, "native-input"), Path.GetDirectoryName(production)! })
            Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--data-dir", forbidden]).ResolveNativeCandidateProfile(applicationData));
        Assert.False(Directory.Exists(applicationData));
    }

    [Fact]
    public void StartupCheckNeverUsesTheNormalDoubleClickDefault()
    {
        Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--startup-check"]));
        Assert.Throws<ArgumentException>(() => new MacStartupOptions(null, true).ResolveNativeCandidateProfile(FreshApplicationData()));
    }

    [Fact]
    public void StartupCheckKeepsItsExplicitNewProfileAndOriginalIsolationValidation()
    {
        var applicationData = FreshApplicationData();
        var checkProfile = Path.Combine(applicationData, "startup-check");
        var options = MacStartupOptions.Parse(["--data-dir", checkProfile, "--startup-check"]).ResolveNativeCandidateProfile(applicationData);
        options.ValidateStartupCheckIsolation();
        Assert.True(options.StartupCheck);
        Assert.Equal(checkProfile, options.DataDirectory);
        Assert.False(Directory.Exists(applicationData));
    }

    [Fact]
    public void AnUnknownApplicationDataRootCannotBecomeAnImplicitRelativeProfile()
    {
        Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse([]).ResolveNativeCandidateProfile("relative"));
    }
}
