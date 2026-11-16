using System.Diagnostics;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PortableEnginePackagingTests
{
    [Fact]
    public async Task StagedWindowsCoreCanPassPortableMarkerAndVersionStartup()
    {
        // Optional explicit integration fixture; normal CI has no packaged core.
        var engine = Environment.GetEnvironmentVariable("NIAN_TEST_PACKAGED_CORE");
        if (string.IsNullOrEmpty(engine)) return;
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(Path.IsPathFullyQualified(engine));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(engine)!, "settings", "Deskflow.conf")),
            "The staged core needs the portable settings marker before it may run.");
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(engine)! };
        start.ArgumentList.Add("--version"); start.Environment["QT_QPA_PLATFORM"] = "offscreen";
        await using var process = await DeskflowOwnedProcess.StartAsync(start, default);
        Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
