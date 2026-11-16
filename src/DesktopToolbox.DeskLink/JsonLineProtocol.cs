using System.Text;
using System.Text.Json;
namespace DesktopToolbox.DeskLink;

/// <summary>Bounded UTF-8 JSONL framing shared by local control and paired TLS control.</summary>
public static class JsonLineProtocol
{
    public const int MaximumFrameBytes = 16384;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var bytes = new byte[MaximumFrameBytes];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0)
            {
                if (count == 0) return null;
                throw new EndOfStreamException("Connection ended inside a control frame.");
            }
            if (one[0] == (byte)'\n') return StrictUtf8.GetString(bytes, 0, count).TrimEnd('\r');
            if (count >= MaximumFrameBytes) throw new InvalidDataException("Control frame exceeds 16384 bytes.");
            bytes[count++] = one[0];
        }
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("Control frame exceeds 16384 bytes.");
        await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct); await stream.FlushAsync(ct);
    }
}
