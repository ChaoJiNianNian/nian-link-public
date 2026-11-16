// Protocol reference: MIT m1ddc / MonitorControl. See project-root THIRD-PARTY-NOTICES/mac-ddc-mit.txt.
namespace DesktopToolbox.Mac.Services.Display;
public static class MacDdcProtocol
{
    public static byte[] CreateReadInput(bool standardChecksum = false) => [0x82, 0x01, 0x60, standardChecksum ? (byte)0xDC : (byte)0x8D];
    public static byte[] CreateSetInput(ushort input)
    {
        byte[] bytes = [0x84, 0x03, 0x60, (byte)(input >> 8), (byte)input, 0];
        bytes[^1] = 0x6E ^ 0x51;
        for (int i = 0; i < bytes.Length - 1; i++) bytes[^1] ^= bytes[i];
        return bytes;
    }
    public static bool TryParseInput(ReadOnlySpan<byte> bytes, out ushort current, out ushort maximum)
    {
        current = maximum = 0;
        if (bytes.Length != 11 || bytes[0] != 0x6E || bytes[1] != 0x88 || bytes[2] != 0x02 || bytes[3] != 0 || bytes[4] != 0x60) return false;
        byte checksum = 0x50;
        foreach (byte value in bytes) checksum ^= value;
        if (checksum != 0) return false;
        maximum = (ushort)((bytes[6] << 8) | bytes[7]);
        current = (ushort)((bytes[8] << 8) | bytes[9]);
        return true;
    }
}


