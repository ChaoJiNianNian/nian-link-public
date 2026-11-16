using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class NativeCandidateProfileTests
{
    [Fact]
    public void CandidateRequiresAnExplicitAbsoluteProfileBeforeAnyServiceCanBeConstructed()
    {
        var production=Path.Combine(Path.GetTempPath(),"nian-production-profile");
        Assert.Throws<ArgumentException>(()=>NativeCandidateProfile.Require(null,production));
        Assert.Throws<ArgumentException>(()=>NativeCandidateProfile.Require("relative",production));
    }
    [Fact]
    public void CandidateRejectsProductionDirectoryItsChildrenAndAnAncestorThatWouldContainIt()
    {
        var root=Path.Combine(Path.GetTempPath(),"nian-profile-boundary");var production=Path.Combine(root,"production");
        Assert.Throws<ArgumentException>(()=>NativeCandidateProfile.Require(production,production));
        Assert.Throws<ArgumentException>(()=>NativeCandidateProfile.Require(Path.Combine(production,"native-input"),production));
        Assert.Throws<ArgumentException>(()=>NativeCandidateProfile.Require(root,production));
        Assert.Throws<ArgumentException>(()=>NativeCandidateProfile.Require(Path.GetPathRoot(production),production));
    }
    [Fact]
    public void ExplicitSiblingIsAcceptedWithoutCreatingOrReadingAProfile()
    {
        var root=Path.Combine(Path.GetTempPath(),"nian-candidate-boundary-"+Guid.NewGuid().ToString("N"));
        var candidate=Path.Combine(root,"candidate");var production=Path.Combine(root,"production");
        Assert.Equal(candidate,NativeCandidateProfile.Require(candidate,production));Assert.False(Directory.Exists(root));
    }
}
