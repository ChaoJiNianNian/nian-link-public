using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacWebRequestTests
{
    [Theory] [InlineData("connection.start")] [InlineData("connection.stop")] [InlineData("preview.setRefreshSeconds")]
    public void SharingCanBeControlledDirectlyFromTheApprovedMiniPage(string action) =>
        Assert.True(MacWebRequest.TryParse("{\"id\":\"sharing\",\"action\":\"" + action + "\",\"payload\":{}}", out _));
    [Fact] public void ParsesOnlyApprovedActionsAndBoundedObjectPayloads()
    {
        Assert.True(MacWebRequest.TryParse("{\"id\":\"a\",\"action\":\"preview.setEnabled\",\"payload\":{\"enabled\":true}}", out var request));
        Assert.Equal("preview.setEnabled", request!.Action);
        Assert.False(MacWebRequest.TryParse("{\"id\":\"a\",\"action\":\"shell\",\"payload\":{}}", out _));
        Assert.False(MacWebRequest.TryParse("{\"id\":\"a\",\"action\":\"ready\",\"payload\":[]}", out _));
        Assert.False(MacWebRequest.TryParse(new string('x', 1024 * 1024 + 1), out _));
    }
    [Fact] public void NativeFilePathsCannotBePassedAsOrdinaryWebDrop()
    {
        Assert.False(MacWebRequest.TryParse("{\"id\":\"a\",\"action\":\"files.nativeDrop\",\"payload\":{\"paths\":[\"/private/file\"]}}", out _));
        Assert.True(MacWebRequest.TryParse("{\"action\":\"files.nativeDrop\",\"native\":true,\"payload\":{\"paths\":[\"/fixture/file\"]}}", out _));
    }
    [Theory] [InlineData(398,548,true)] [InlineData(83,164,true)] [InlineData(374,524,false)] [InlineData(398,-4,false)] [InlineData(398,3000,false)]
    public void ViewportIncludesShadowPaddingAndIsBounded(double width,double height,bool valid)
        => Assert.Equal(valid, MacWebRequest.IsValidSize(width,height));
}
