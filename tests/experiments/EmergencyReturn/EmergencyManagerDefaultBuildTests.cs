#if !NIAN_FENCE_RESEARCH
using DesktopToolbox.DeskLink.Coordination;

namespace EmergencyReturn.Research;

public sealed class EmergencyManagerDefaultBuildTests
{
    [Fact]
    public void NormalBuildExposesNoEmergencyResearchMethod() =>
        Assert.Null(typeof(MultiPeerPreviewManager).GetMethod("TryEmergencyReturnAsync"));
}
#endif
