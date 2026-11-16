using System.Diagnostics;
using DesktopToolbox.DeskLink.Collaboration;
namespace DeskLink.Tests;
internal static class CollaborationJpegFixture
{
    internal static byte[] Bytes => Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD8qqKKKAP/2Q==");
    internal static PreviewFrame Frame() => new() { State = "live", Width = 1, Height = 1, JpegBytes = Bytes };
}
public sealed class CollaborationBoundaryTests
{
    [Fact]
    public void AuthorizedRootRejectsAReparsePointInItsAncestors()
    {
        using var folder = new TestFolder(); var actual = Path.Combine(folder.Path, "actual"); Directory.CreateDirectory(Path.Combine(actual, "child")); var alias = Path.Combine(folder.Path, "alias");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", alias, actual }, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(alias, actual);
        try { Assert.Throws<UnauthorizedAccessException>(() => new TransferReceiver(Path.Combine(alias, "child"))); }
        finally { Directory.Delete(alias); }
    }
    [Fact]
    public void PreviewRejectsSofDimensionsThatDisagreeWithDeclaredSize()
    {
        var preview = new CollaborationPreviewExchange(); preview.SetRemoteRequested(true);
        Assert.Throws<ArgumentException>(() => preview.SetLocal(CollaborationJpegFixture.Frame() with { Width = 960, Height = 540 }));
        var jpeg = CollaborationJpegFixture.Bytes;
        var sof = Enumerable.Range(0, jpeg.Length - 1).Single(i => jpeg[i] == 255 && jpeg[i + 1] == 192);
        jpeg[sof + 5] = 0xff; jpeg[sof + 6] = 0xff;
        Assert.Throws<ArgumentException>(() => preview.SetLocal(CollaborationJpegFixture.Frame() with { JpegBytes = jpeg }));
    }
    [Fact]
    public void ClipboardRejectsPngHeaderWithExcessiveDecodedPixels()
    {
        var payload = new ClipboardPayload(Guid.NewGuid().ToString("N"), "Mac", DateTimeOffset.UtcNow, PngBytes: PngHeader(16384, 16384));
        Assert.Throws<ArgumentException>(() => CollaborationClipboardExchange.Validate(payload));
    }
    [Fact]
    public void MaximumManifestValidatesAllParentsWithoutQuadraticScanning()
    {
        var entries = new List<TransferEntry> { new("project", true, 0, "") };
        entries.AddRange(Enumerable.Range(0,9999).Select(i => new TransferEntry($"project/file-{i}.txt", false, 0, CollaborationPaths.Hash([]))));
        TransferReceiver.ValidateManifest(new(Guid.NewGuid().ToString("N"), entries));
        Assert.Equal(10000, entries.Count);
    }
    [Fact]
    public async Task LocalShareCopyRejectsDestinationInsideSource()
    {
        using var folder = new TestFolder(); var shared = Path.Combine(folder.Path, "share"); Directory.CreateDirectory(shared);
        await using var server = new CollaborationServer(new() { ProfileDirectory = folder.Path, SharedDirectories = [new("work", "work", shared)] });
        var nested = Path.Combine(shared, "copy"); await Assert.ThrowsAsync<ArgumentException>(() => server.CopyLocalSharedAsync("work", "", nested)); Assert.False(Directory.Exists(nested));
    }
    internal static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33]; new byte[] {137,80,78,71,13,10,26,10}.CopyTo(bytes,0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8),13); "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16),width); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20),height); bytes[24]=8; bytes[25]=6;
        return bytes;
    }
}
