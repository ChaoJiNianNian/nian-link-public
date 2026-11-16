using System.IO;
using NianLink.Smoke;

namespace NianLink.Smoke.Tests;

public sealed class IsolationTests
{
    private const string Parent = @"D:\Projects\nian-link\tests\.work\standalone-profile";

    [Fact]
    public void OnlyAnAbsoluteGuidDirectlyUnderTheDedicatedParentIsAccepted()
    {
        var path = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        Assert.Equal(path, SmokeProfilePolicy.Validate(path, Parent));
        foreach (var other in new[] {
            "relative", @"C:\Users\Someone\AppData\Local\DesktopToolbox",
            @"D:\NianDesktopToolbox", Parent, Parent + "-outside\\" + Guid.NewGuid().ToString("N"),
            Parent + "\\..\\" + Guid.NewGuid().ToString("N"), Parent + "\\nested\\" + Guid.NewGuid().ToString("N"),
            Parent + "\\not-a-guid", @"\\?\D:\Projects\nian-link\tests\.work\standalone-profile\" + Guid.NewGuid().ToString("N")
        }) Assert.ThrowsAny<ArgumentException>(() => SmokeProfilePolicy.Validate(other, Parent));
    }

    [Fact]
    public async Task FakeServiceAlwaysRefusesBusinessOperations()
    {
        var service = new OfflineMiniService();
        await service.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartSharingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SwitchAsync("Mac"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetClipboardEnabledAsync(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPreviewEnabledAsync(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendFilesAsync(["synthetic"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListSharedAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadSharedAsync("synthetic", ""));
        Assert.Equal(7, service.RejectedOperationCount);
        Assert.False(service.Snapshot.Connected); Assert.False(service.Snapshot.ClipboardEnabled);
    }
}
