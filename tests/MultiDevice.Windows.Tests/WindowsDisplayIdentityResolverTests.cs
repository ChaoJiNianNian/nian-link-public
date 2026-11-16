using DesktopToolbox.Windows.DeskSwitch;

namespace MultiDevice.Windows.Tests;

public sealed class WindowsDisplayIdentityResolverTests
{
    [Fact]
    public void FindsOnlyAnUnambiguousLocalCandidateForTheApprovedDisplayModel()
    {
        const string host = @"\\?\DISPLAY#XMI27B1#HOST";
        const string peer = @"\\?\DISPLAY#XMI27B1#PEER";
        var monitors = new[] { Monitor(peer), Monitor(@"\\?\DISPLAY#ABC1234#OTHER") };
        Assert.Equal(peer, WindowsDisplayIdentityResolver.FindCandidate(host, monitors));
        Assert.Null(WindowsDisplayIdentityResolver.FindCandidate(host, [Monitor(peer), Monitor(@"\\?\DISPLAY#XMI27B1#SECOND")]));
        Assert.Null(WindowsDisplayIdentityResolver.FindCandidate("unknown", monitors));
    }

    private static DisplayMonitorInfo Monitor(string id) => new(id, "monitor", 15, [new(15, "DP 1"), new(16, "DP 2")], true, "");
}
