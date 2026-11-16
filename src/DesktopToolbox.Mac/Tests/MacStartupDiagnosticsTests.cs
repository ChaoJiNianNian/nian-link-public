using System.Text.Json;
using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacStartupDiagnosticsTests
{
    [Fact]
    public void ExistingSnapshotsKeepLegacyBackendByDefault()
    {
        Assert.Equal(MacLinkBackend.Deskflow, new MacLinkSnapshot().Backend);
    }

    [Fact]
    public void LegacyReportKeepsTheExistingCoreDiagnostics()
    {
        var state = new MacLinkSnapshot { Settings = new() { EnginePath = "/fixture/deskflow-core" } };
        string? inspected = null;
        var report = MacStartupDiagnostics.Create(state, "MacLinkService", path => { inspected = path; return true; });
        Assert.Equal("deskflow", report["engineKind"]);
        Assert.Equal("MacLinkService", report["runtimeService"]);
        Assert.Equal(state.Settings.EnginePath, report["EnginePath"]);
        Assert.Equal(true, report["officialCorePresent"]);
        Assert.Equal(state.Settings.EnginePath, inspected);
    }

    [Fact]
    public void NativeReportIdentifiesActualServiceWithoutInspectingOrSerializingAnExternalCore()
    {
        var state = new MacLinkSnapshot { Backend = MacLinkBackend.Native };
        var report = MacStartupDiagnostics.Create(state, "MacNativeLinkService", _ => throw new InvalidOperationException("Native startup must not inspect Deskflow."));
        Assert.Equal("native", report["engineKind"]);
        Assert.Equal("MacNativeLinkService", report["runtimeService"]);
        Assert.False(report.ContainsKey("EnginePath"));
        Assert.False(report.ContainsKey("officialCorePresent"));
        Assert.DoesNotContain("Deskflow", JsonSerializer.Serialize(report));
    }

    [Theory]
    [InlineData(false, "年年电脑切换")]
    [InlineData(true, "年年互联·自研测试")]
    public void CandidateWindowTitleIsDistinctFromTheInstalledApplication(bool candidate, string expected)
    {
        Assert.Equal(expected, MacWindowPresentation.WindowTitle(candidate));
    }
}
