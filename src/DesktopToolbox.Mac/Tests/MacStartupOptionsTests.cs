using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacStartupOptionsTests
{
    [Fact]
    public void DefaultLaunchDoesNotRequestDiagnosticsOrChangeTheProfile()
    {
        var options = MacStartupOptions.Parse([]);
        Assert.Null(options.DataDirectory);
        Assert.False(options.StartupCheck);
    }

    [Fact]
    public void StartupCheckRequiresAnExplicitAbsoluteIsolatedProfile()
    {
        Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--startup-check"]));
        Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--data-dir", "relative", "--startup-check"]));
        var directory = Path.Combine(Path.GetTempPath(), "nian-mac-startup-check");
        var options = MacStartupOptions.Parse(["--data-dir", directory, "--startup-check"]);
        Assert.Equal(Path.GetFullPath(directory), options.DataDirectory);
        Assert.True(options.StartupCheck);
    }

    [Fact]
    public void StartupCheckCannotReadAnExistingProfile()
    {
        var existing = Path.GetTempPath();
        var options = MacStartupOptions.Parse(["--data-dir", existing, "--startup-check"]);
        Assert.Throws<IOException>(options.ValidateStartupCheckIsolation);
        MacStartupOptions.Parse(["--data-dir", existing]).ValidateStartupCheckIsolation();
    }

    [Fact]
    public void StartupCheckAcceptsANewDirectoryWithoutCreatingOrReadingAProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), "nian-startup-" + Guid.NewGuid().ToString("N"));
        var options = MacStartupOptions.Parse(["--data-dir", path, "--startup-check"]);
        options.ValidateStartupCheckIsolation();
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void DuplicateOrMissingProfileArgumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--data-dir"]));
        var directory = Path.GetTempPath();
        Assert.Throws<ArgumentException>(() => MacStartupOptions.Parse(["--data-dir", directory, "--data-dir", directory]));
    }
    [Fact]
    public void PreviewCandidateHasItsOwnDefaultAndExplicitDirectoryWins()
    {
        var root=Path.Combine(Path.GetTempPath(),"nian-preview-default-"+Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.Combine(root,"NianLink","PreviewControlCandidate"),new MacStartupOptions(null,false).ResolvePreviewCandidateProfile(root).DataDirectory);
        var explicitPath=Path.Combine(root,"explicit");
        Assert.Equal(explicitPath,new MacStartupOptions(explicitPath,false).ResolvePreviewCandidateProfile(root).DataDirectory);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void PreviewCandidateRejectsProductionProfilesAndImplicitStartupChecks()
    {
        var root=Path.Combine(Path.GetTempPath(),"nian-preview-boundary-"+Guid.NewGuid().ToString("N"));
        var production=Path.Combine(root,"NianDesktopToolbox","DeskLink");
        Assert.Throws<ArgumentException>(()=>new MacStartupOptions(production,false).ResolvePreviewCandidateProfile(root));
        Assert.Throws<ArgumentException>(()=>new MacStartupOptions(Path.Combine(production,"child"),false).ResolvePreviewCandidateProfile(root));
        Assert.Throws<ArgumentException>(()=>new MacStartupOptions(null,true).ResolvePreviewCandidateProfile(root));
    }

    [Fact]
    public void MultiDeviceCandidateIsSeparateFromProductionAndOtherCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-multi-boundary-" + Guid.NewGuid().ToString("N"));
        var multi = new MacStartupOptions(null, false).ResolveMultiDeviceCandidateProfile(root);
        Assert.Equal(Path.Combine(root, "NianLink", "MultiDeviceCandidate"), multi.DataDirectory);
        foreach (var other in new[] { Path.Combine(root, "NianLink", "DeskLink"), Path.Combine(root, "NianLink", "PreviewControlCandidate"),
                     Path.Combine(root, "NianLink", "NativeInputCandidate"), Path.Combine(root, "NianDesktopToolbox", "DeskLink") })
        {
            Assert.Throws<ArgumentException>(() => new MacStartupOptions(other, false).ResolveMultiDeviceCandidateProfile(root));
            Assert.Throws<ArgumentException>(() => new MacStartupOptions(Path.Combine(other, "child"), false).ResolveMultiDeviceCandidateProfile(root));
        }
        Assert.Throws<ArgumentException>(() => new MacStartupOptions(null, true).ResolveMultiDeviceCandidateProfile(root));
        Assert.False(Directory.Exists(root));
    }
}
