using NianLink.MultiDevice;

public sealed class DiscoveryVolumePolicyTests
{
    private static DiscoveryVolumeEvidence Good(string id = "volume-D") => new(true, true, true, @"\Device\HarddiskVolume5", id);
    private static DiscoveryVolumeEvidence Missing() => new(false, false, false, null, null);
    private static DiscoveryVolumeEvidence System() => Good("volume-C") with { Device = @"\Device\HarddiskVolume1" };

    [Theory]
    [InlineData("D:")]
    [InlineData("H:")]
    public void OnlyApprovedFixedDisksWithDistinctVolumeCanBeUsed(string drive)
    {
        Assert.True(DiscoveryVolumePolicy.Allowed(drive, Good(), System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("C:", Good(), System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("F:", Good(), System(), Missing()));
    }

    [Fact]
    public void MissingRemoteSubstitutedAliasesAndUnknownEvidenceFailClosed()
    {
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Missing(), System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good() with { Fixed = false }, System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good() with { Ready = false }, System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good() with { Device = @"\??\C:\Users" }, System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good("volume-C"), System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good("volume-F"), System(), Good("volume-F")));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good(), System() with { Volume = null }, Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good(), System(), Good() with { Volume = null }));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good() with { Device = null }, System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good() with { Device = @"\Device\HarddiskVolume5\subdir" }, System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good() with { Device = @"\Device\HarddiskVolumeShadowCopy1" }, System(), Missing()));
        Assert.False(DiscoveryVolumePolicy.Allowed("H:", Good(), System() with { Device = @"\Device\HarddiskVolume5" }, Missing()));
    }
    [LocalDiscoveryVolumeFact]
    public void LocalWindowsDVolumeIsActuallyAllowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(DiscoveryVolumePolicy.IsAllowedCurrentVolume("D:"));
    }
}

public sealed class LocalDiscoveryVolumeFactAttribute : FactAttribute
{
    public LocalDiscoveryVolumeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("NIAN_DISCOVERY_LOCAL_D_TEST") != "1")
            Skip = "Local D drive verification requires explicit opt-in.";
    }
}
