using NianLink.MultiDevice;

public sealed class CandidatePathPolicyTests
{
    [Fact]
    public void RejectsFormalDirectoryItsChildrenAndItsParents()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nian-policy"));
        var formal = Path.Combine(parent, "formal");
        Assert.False(CandidatePathPolicy.IsIsolated(formal, formal));
        Assert.False(CandidatePathPolicy.IsIsolated(Path.Combine(formal, "child"), formal));
        Assert.False(CandidatePathPolicy.IsIsolated(parent, formal));
        Assert.True(CandidatePathPolicy.IsIsolated(Path.Combine(parent, "formal-other"), formal));
    }
    [Fact]
    public void DiscoveryPortableAcceptsOnlyItsDirectRandomProfileAndRejectsOldProfiles()
    {
        var local = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nian-discovery-policy"));
        var parent = Path.Combine(local, "nian-link", "test-profiles", "discovery-portable");
        var valid = Path.Combine(parent, "test-" + new string('a', 32));
        Assert.True(CandidatePathPolicy.IsDiscoveryProfileUnderRoot(valid, parent));
        Assert.False(CandidatePathPolicy.IsDiscoveryProfileUnderRoot(Path.Combine(local, "NianLinkMultiDeviceCandidate"), parent));
        Assert.False(CandidatePathPolicy.IsDiscoveryProfileUnderRoot(Path.Combine(parent, "test-short"), parent));
        Assert.False(CandidatePathPolicy.IsDiscoveryProfileUnderRoot(Path.Combine(valid, "nested"), parent));
    }
    [Fact]
    public void DiscoveryEntryRequiresExplicitFlagAndDedicatedProfile()
    {
        var local = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nian-discovery-policy"));
        var parent = Path.Combine(local, "nian-link", "test-profiles", "discovery-portable");
        var profile = Path.Combine(parent, "test-" + new string('a', 32));
        var engine = Path.Combine(local, "fake-engine.exe");
        Assert.True(CandidatePathPolicy.IsDiscoveryLaunchUnderRoot([profile, engine, "--discovery-portable"], parent));
        Assert.False(CandidatePathPolicy.IsDiscoveryLaunchUnderRoot([profile, engine], parent));
        Assert.False(CandidatePathPolicy.IsDiscoveryLaunchUnderRoot([profile, engine, "--wrong"], parent));
        Assert.False(CandidatePathPolicy.IsDiscoveryLaunchUnderRoot([Path.Combine(local, "NianLinkMultiDeviceCandidate"), engine, "--discovery-portable"], parent));
    }
    [Fact]
    public void DiscoveryEntryNeverAcceptsLegacyLocalAppData()
    {
        var oldProfile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NianLinkMultiDeviceCandidate"));
        var engine = Path.Combine(Path.GetTempPath(), "fake-engine.exe");
        Assert.False(CandidatePathPolicy.IsDiscoveryLaunch([oldProfile, engine, "--discovery-portable"]));
        var oldDiscovery = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NianLinkDiscoveryCandidate", "test-" + new string('a', 32)));
        Assert.False(CandidatePathPolicy.IsDiscoveryLaunch([oldDiscovery, engine, "--discovery-portable"]));
    }
    [Fact]
    public void DiscoveryProfileMustMatchThePackageRandomId()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "discovery-package-id"));
        var profile = Path.Combine(root, "test-" + new string('a', 32));
        Assert.True(CandidatePathPolicy.MatchesPackageId(profile, new string('a', 32)));
        Assert.False(CandidatePathPolicy.MatchesPackageId(profile, new string('b', 32)));
        Assert.False(CandidatePathPolicy.MatchesPackageId(profile, "short"));
    }
    [Fact]
    public void ExistingUnownedProfileIsNeverAdoptedOrOverwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-owner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var profile = Path.Combine(root, "test-" + id);
        Directory.CreateDirectory(profile);
        var sentinel = Path.Combine(profile, "existing.txt");
        File.WriteAllText(sentinel, "untouched");
        try
        {
            Assert.False(CandidatePathPolicy.ClaimDiscoveryProfileUnderRoot(profile, root, id));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Path.Combine(profile, ".discovery-owner")));
        }
        finally { File.Delete(sentinel); Directory.Delete(profile); Directory.Delete(root); }
    }

    [Fact]
    public void NewOwnedProfileCanBeReopenedOnlyBySamePackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-owner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var profile = Path.Combine(root, "test-" + id);
        try
        {
            Assert.True(CandidatePathPolicy.ClaimDiscoveryProfileUnderRoot(profile, root, id));
            Assert.True(CandidatePathPolicy.ClaimDiscoveryProfileUnderRoot(profile, root, id));
            Assert.False(CandidatePathPolicy.ClaimDiscoveryProfileUnderRoot(profile, root, Guid.NewGuid().ToString("N")));
        }
        finally { File.Delete(Path.Combine(profile, ".discovery-owner")); Directory.Delete(profile); Directory.Delete(root); }
    }
    [Fact]
    public void DirectoryCreatedDuringClaimIsNotAdopted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "nian-owner-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var profile = Path.Combine(root, "test-" + id);
        var sentinel = Path.Combine(profile, "existing.txt");
        try
        {
            Assert.False(CandidatePathPolicy.ClaimDiscoveryProfileForTest(profile, root, id, () =>
            { Directory.CreateDirectory(profile); File.WriteAllText(sentinel, "untouched"); }));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Path.Combine(profile, ".discovery-owner")));
        }
        finally { File.Delete(sentinel); Directory.Delete(profile); Directory.Delete(root); }
    }
}
