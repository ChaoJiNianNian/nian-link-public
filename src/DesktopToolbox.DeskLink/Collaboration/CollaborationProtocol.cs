using System.Buffers.Binary;
using System.Text.Json;
namespace DesktopToolbox.DeskLink.Collaboration;

internal static class CollaborationProtocol
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonLineProtocol.Json);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Collaboration response exceeds the data frame limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct); var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Invalid collaboration data frame length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes, JsonLineProtocol.Json) ?? throw new InvalidDataException("Empty collaboration frame.");
    }
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, JsonLineProtocol.Json);
    public static T Parse<T>(JsonElement value) => value.Deserialize<T>(JsonLineProtocol.Json) ?? throw new InvalidDataException("Missing collaboration request body.");
}
internal sealed record CollaborationRequest(int Version, string Id, string Token, string Action, JsonElement Data);
internal sealed record CollaborationResponse(int Version, string Id, bool Ok, JsonElement Data, string? Code = null, string? Error = null);
internal sealed record TransferFileRequest(string Id, string RelativePath, long Offset = 0, byte[]? Bytes = null, string? Sha256 = null);
internal sealed record SharedRequest(string? RootId, string RelativePath);
internal sealed class CollaborationBandwidth(int bytesPerSecond)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long next;
    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (next > now) await Task.Delay(TimeSpan.FromSeconds((next - now) / (double)System.Diagnostics.Stopwatch.Frequency), ct);
            now = System.Diagnostics.Stopwatch.GetTimestamp();
            next = now + (long)(bytes / (double)bytesPerSecond * System.Diagnostics.Stopwatch.Frequency);
        }
        finally { gate.Release(); }
    }
}
