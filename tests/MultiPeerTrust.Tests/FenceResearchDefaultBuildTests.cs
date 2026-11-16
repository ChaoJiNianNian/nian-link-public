using System.Reflection;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;

namespace MultiPeerTrust.Tests;

public sealed class FenceResearchDefaultBuildTests
{
    [Fact]
    public void DefaultBuildContainsNoResearchFactory()
    {
        Assert.Null(typeof(MultiPeerControlServer).GetMethod("CreateForFenceResearch", BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Null(typeof(MultiPeerControlServer).GetMethod("CreateForEmergencyResearch", BindingFlags.NonPublic | BindingFlags.Static));
        Assert.Null(typeof(MultiDeviceCredentialVault).GetMethod("TryCommitUniqueResearchAuthorizedAsync", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(typeof(MultiPeerPreviewManager).GetMethod("ObserveEmergencyRevisionAsync", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(typeof(MultiPeerPreviewManager).GetMethod("TryEmergencyReturnAtRevisionAsync", BindingFlags.NonPublic | BindingFlags.Instance));
        var request = typeof(MultiPeerControlServer).GetNestedType("Request", BindingFlags.NonPublic)!;
        var response = typeof(MultiPeerControlServer).GetNestedType("Response", BindingFlags.NonPublic)!;
        Assert.Null(request.GetProperty("ExpectedRevision"));
        Assert.Null(response.GetProperty("KeyboardRevision"));
    }
}
