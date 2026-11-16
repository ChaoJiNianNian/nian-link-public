using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nian.Input;

internal static class InputWire
{
    public const int MaximumBytes = 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 12, NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 1 or > MaximumBytes) throw new InvalidDataException("The input frame length is invalid.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("The input frame is empty.");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is < 1 or > MaximumBytes) throw new InvalidDataException("The input frame is too large.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}

internal static class InputValidation
{
    public static byte[] Pairing(InputPairing pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        if (string.IsNullOrWhiteSpace(pairing.Host) || pairing.Host.Length > 253 || pairing.Host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || Uri.CheckHostName(pairing.Host) == UriHostNameType.Unknown || pairing.Port is < 1 or > 65535)
            throw new ArgumentException("The receiver address is invalid.", nameof(pairing));
        if (pairing.Fingerprint is not { Length: 64 } || !pairing.Fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("The receiver fingerprint is invalid.", nameof(pairing));
        if (!TryToken(pairing.Token, out _)) throw new ArgumentException("The receiver token is invalid.", nameof(pairing));
        return Convert.FromHexString(pairing.Fingerprint);
    }
    public static bool TryToken(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length > 64) return false;
        try { bytes = Convert.FromBase64String(value); return bytes.Length == 32; }
        catch (FormatException) { return false; }
    }
    public static bool Packet(InputPacket packet, long previous)
    {
        if (packet.Sequence <= 0 || packet.Sequence <= previous || !double.IsFinite(packet.X) || !double.IsFinite(packet.Y)) return false;
        return packet.Kind switch
        {
            InputKind.MoveRelative => packet.Code == 0 && packet.Delta == 0 && Math.Abs(packet.X) <= 32767 && Math.Abs(packet.Y) <= 32767,
            InputKind.ButtonDown or InputKind.ButtonUp => packet.Code is >= 0 and <= 2 && packet.X == 0 && packet.Y == 0 && packet.Delta == 0,
            InputKind.Wheel => packet.Code == 0 && packet.X == 0 && packet.Y == 0 && packet.Delta != 0 && Math.Abs((long)packet.Delta) <= 12000,
            InputKind.KeyDown or InputKind.KeyUp => Enum.IsDefined((InputKey)packet.Code) && packet.X == 0 && packet.Y == 0 && packet.Delta == 0,
            _ => false
        };
    }
}
