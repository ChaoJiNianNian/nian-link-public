using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacPreviewFreshnessTests
{
    [Fact] public void NewArraysWithTheSameRemoteTimestampCannotKeepTheFrameAlive()
    {
        var freshness=new MacPreviewFreshness(1);var stamp=DateTimeOffset.UnixEpoch;
        Assert.True(freshness.Observe(stamp,100));Assert.True(freshness.Observe(stamp,7900));
        Assert.False(freshness.Observe(stamp,8100));Assert.False(freshness.IsFresh(8100));
        Assert.True(freshness.Observe(stamp.AddSeconds(1),8200));
    }
    [Fact] public void ThirtySecondMonitoringKeepsTheFrameUntilItsNextExpectedRefreshAndAGracePeriod()
    {
        var freshness = new MacPreviewFreshness(); var stamp = DateTimeOffset.UnixEpoch;
        Assert.True(freshness.Observe(stamp,100));Assert.True(freshness.IsFresh(30100));Assert.False(freshness.IsFresh(38100));
        freshness.SetRefreshSeconds(60);Assert.True(freshness.Observe(stamp.AddSeconds(30),38100));Assert.True(freshness.IsFresh(98100));Assert.False(freshness.IsFresh(106100));
        freshness.Invalidate();Assert.False(freshness.Observe(stamp.AddSeconds(30),106110));
    }
    [Fact] public void ManualOffCannotRedisplayTheSameFrameAndRemoteClockIsNotTrusted()
    {
        var freshness=new MacPreviewFreshness();var stamp=DateTimeOffset.UnixEpoch;
        Assert.True(freshness.Observe(stamp,500));freshness.Invalidate();Assert.False(freshness.Observe(stamp,510));
        Assert.True(freshness.Observe(stamp.AddDays(-1),520));Assert.True(freshness.IsFresh(530));
    }
    [Fact] public void NewGenerationCanAcceptAFrameWithAnEarlierOrRepeatedSourceTimestamp()
    {
        var freshness=new MacPreviewFreshness();var stamp=DateTimeOffset.UnixEpoch.AddDays(1);
        Assert.True(freshness.Observe(stamp,10));freshness.BeginGeneration();Assert.True(freshness.Observe(stamp,20));
        freshness.BeginGeneration();Assert.True(freshness.Observe(stamp.AddHours(-1),30));
        Assert.False(freshness.Observe(stamp.AddHours(-1),40000));
        Assert.True(freshness.Observe(stamp.AddHours(-1),40010,forceNew:true));
    }
}
