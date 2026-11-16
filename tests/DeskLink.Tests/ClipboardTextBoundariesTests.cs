using DesktopToolbox.DeskLink.Collaboration;

namespace DeskLink.Tests;

public sealed class ClipboardTextBoundariesTests
{
    private static ClipboardPayload Text(string text) => new(Guid.NewGuid().ToString("N"), "test", DateTimeOffset.UtcNow, Text: text);

    [Fact]
    public void IncomingLegacyImageClearsStaleRemoteWithoutRemovingLocalCopy()
    {
        var exchange = new CollaborationClipboardExchange();
        exchange.SetLocalEnabled(true); exchange.SetRemoteEnabled(true);
        exchange.SetRemote(Text("old remote"));
        var png = CollaborationBoundaryTests.PngHeader(1, 1);
        exchange.SetRemote(new(Guid.NewGuid().ToString("N"), "old peer", DateTimeOffset.UtcNow, PngBytes: png));
        Assert.Null(exchange.GetRemote());
        var local = Text("new local"); exchange.SetLocal(local);
        exchange.SetRemote(new(Guid.NewGuid().ToString("N"), "old peer", DateTimeOffset.UtcNow, PngBytes: png));
        Assert.Equal(local.Id, exchange.GetLocal()?.Id);
        Assert.Null(exchange.GetRemote());
        var next = Text("new remote"); exchange.SetRemote(next);
        Assert.Equal(next.Id, exchange.GetRemote()?.Id);
    }

    [Fact]
    public void TextLimitCountsUtf8BytesInsteadOfCharacters()
    {
        CollaborationClipboardExchange.Validate(Text(new string('a', 262144)));
        CollaborationClipboardExchange.Validate(Text(new string('界', 87381) + "a"));
        Assert.Throws<ArgumentException>(() => CollaborationClipboardExchange.Validate(Text(new string('a', 262145))));
        Assert.Throws<ArgumentException>(() => CollaborationClipboardExchange.Validate(Text(new string('界', 87382))));
    }
}
