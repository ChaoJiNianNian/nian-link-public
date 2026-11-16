using System.Text;

namespace DesktopToolbox.Windows.DeskSwitch;

public static class MonitorDisplayName
{
    public static string Resolve(byte[]? edid, string? fallback)
    {
        var driverName = string.IsNullOrWhiteSpace(fallback) ? "显示器" : fallback.Trim();
        if (edid is null || edid.Length < 128) return driverName;
        ReadOnlySpan<byte> header = [0, 255, 255, 255, 255, 255, 255, 0];
        if (!edid.AsSpan(0, 8).SequenceEqual(header)) return driverName;
        var checksum = 0;
        for (var index = 0; index < 128; index++) checksum += edid[index];
        if ((checksum & 255) != 0) return driverName;

        string? reportedName = null;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0
                || edid[offset + 3] != 0xFC || edid[offset + 4] != 0) continue;
            var length = 0;
            var validText = true;
            for (; length < 13; length++)
            {
                var character = edid[offset + 5 + length];
                if (character is 0 or 10) break;
                if (character is < 32 or > 126) { validText = false; break; }
            }
            if (!validText) continue;
            var name = Encoding.ASCII.GetString(edid, offset + 5, length).Trim();
            if (name.Length > 0) { reportedName = name; break; }
        }

        var manufacturer = (edid[8] << 8) | edid[9];
        var first = (manufacturer >> 10) & 31;
        var second = (manufacturer >> 5) & 31;
        var third = manufacturer & 31;
        var nameToShow = reportedName ?? driverName;
        if ((manufacturer & 0x8000) != 0 || first is < 1 or > 26
            || second is < 1 or > 26 || third is < 1 or > 26) return nameToShow;
        var vendor = new string([(char)(first + 64), (char)(second + 64), (char)(third + 64)]);
        var product = edid[10] | (edid[11] << 8);
        return $"{nameToShow}（{vendor}{product:X4}）";
    }
}
