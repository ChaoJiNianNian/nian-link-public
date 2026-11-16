using System.Buffers.Binary;
namespace DesktopToolbox.DeskLink.Collaboration;

internal static class CollaborationImageBounds
{
    internal static bool IsBoundedPng(byte[] bytes)
    {
        if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8,4)) != 13 || !bytes.AsSpan(12,4).SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4)); var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4));
        return width is > 0 and <= 16384 && height is > 0 and <= 16384 && (long)width * height <= 24_000_000;
    }
    /// <summary>Walks JPEG marker segments and entropy scans without decoding attacker-controlled pixels.</summary>
    internal static bool IsBoundedJpeg(byte[] bytes, int declaredWidth, int declaredHeight) => ValidateJpeg(bytes, 1920, 1080, 2_073_600, declaredWidth, declaredHeight);
    internal static bool IsBoundedSharedJpeg(byte[] bytes) => ValidateJpeg(bytes, 16384, 16384, 24_000_000, null, null);
    private static bool ValidateJpeg(byte[] bytes, int maxWidth, int maxHeight, long maxPixels, int? declaredWidth, int? declaredHeight)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var offset = 2; var foundSof = false; var foundScan = false;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            var marker = bytes[offset++];
            if (marker == 0xd9) return foundSof && foundScan && offset == bytes.Length;
            if (marker is 0x00 or 0xd8 || marker is >= 0xd0 and <= 0xd7) return false;
            if (marker == 0x01) continue;
            if (offset + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset,2));
            if (length < 2 || length > bytes.Length - offset) return false;
            if (IsSof(marker))
            {
                if (foundSof || length < 11) return false;
                var precision = bytes[offset+2]; var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset+3,2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset+5,2)); var components = bytes[offset+7];
                if (precision != 8 || components is not (1 or 3 or 4) || length != 8 + components * 3 || width < 1 || width > maxWidth || height < 1 || height > maxHeight ||
                    (long)width * height > maxPixels || declaredWidth.HasValue && width != declaredWidth || declaredHeight.HasValue && height != declaredHeight) return false;
                foundSof = true;
            }
            offset += length;
            if (marker != 0xda) continue;
            if (!foundSof) return false;
            foundScan = true;
            while (offset < bytes.Length)
            {
                if (bytes[offset] != 0xff) { offset++; continue; }
                var markerStart = offset++;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) return false;
                if (bytes[offset] == 0x00 || bytes[offset] is >= 0xd0 and <= 0xd7) { offset++; continue; }
                offset = markerStart; break;
            }
        }
        return false;
    }
    private static bool IsSof(byte marker) => marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;
}
