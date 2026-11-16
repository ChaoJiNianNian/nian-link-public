using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class NativeSurfaceSnapshotTests
{
    [Fact]
    public void UnknownOrNotYetPresentedWindowStartsProtected()
    {
        var surface=new NativeSurfaceSnapshot();
        Assert.True(surface.ProtectsOwnSurface);Assert.Null(surface.AllowedWindowId);
        Assert.True(surface.IsProtectedWindow(7));
        surface.Update(true,false,null);Assert.True(surface.ProtectsOwnSurface);
    }
    [Fact]
    public async Task OnlyTheKnownOrdinaryCompactWindowCanReceivePhysicalModeInput()
    {
        var surface=new NativeSurfaceSnapshot();surface.Update(true,false,7);
        await Task.Run(()=>
        {
            Assert.False(surface.ProtectsOwnSurface);Assert.Equal(7,surface.AllowedWindowId);
            Assert.False(surface.IsProtectedWindow(7));Assert.True(surface.IsProtectedWindow(8));Assert.True(surface.IsProtectedWindow(0));
        });
    }
    [Fact]
    public void SettingsPairingHiddenAndExpandedSurfacesRevokeTheAllowedWindow()
    {
        var surface=new NativeSurfaceSnapshot();surface.Update(true,false,7);
        surface.Update(true,true,7);Assert.True(surface.IsProtectedWindow(7));Assert.Null(surface.AllowedWindowId);
        surface.Update(false,false,7);Assert.True(surface.ProtectsOwnSurface);
        surface.Update(true,false,7);surface.Protect();Assert.True(surface.IsProtectedWindow(7));
    }
    [Fact]
    public void AModalProtectionScopeCannotBeOverriddenByBackgroundRendering()
    {
        var surface=new NativeSurfaceSnapshot();surface.Update(true,false,7);
        var scope=surface.Block();surface.Update(true,false,7);Assert.True(surface.ProtectsOwnSurface);
        scope.Dispose();Assert.True(surface.ProtectsOwnSurface);
        surface.Update(true,false,7);Assert.False(surface.ProtectsOwnSurface);
    }
    [Theory]
    [InlineData("com.nian.desktoptoolbox.desklink",81,80,true)]
    [InlineData("com.niannian.nianlink",81,80,true)]
    [InlineData("com.nian.desktoptoolbox.desklink.native-candidate",81,80,true)]
    [InlineData("com.nian.desktoptoolbox.desklink.preview-candidate",81,80,true)]
    [InlineData("com.example.editor",80,80,true)]
    [InlineData(null,81,80,true)]
    [InlineData("com.example.editor",81,80,false)]
    public void PreviewExcludesProductionCandidateOwnProcessAndUnknownOwner(string? bundle,int owner,int own,bool expected)
        =>Assert.Equal(expected,NativeSurfaceSnapshot.ExcludesPreviewOwner(bundle,owner,own));
}
