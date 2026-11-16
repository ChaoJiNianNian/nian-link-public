using System.Buffers.Binary;
namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Bounds decoded pixels before sharing encoded images with either platform renderer. No image decoding.</summary>
internal static class SharedImageBounds
{
    internal static bool IsSafe(byte[] bytes, string type) => type switch
    {
        "image/jpeg" => CollaborationImageBounds.IsBoundedSharedJpeg(bytes),
        "image/png" => CollaborationImageBounds.IsBoundedPng(bytes),
        "image/gif" => Gif(bytes), "image/webp" => Webp(bytes), "image/bmp" => Bmp(bytes), _ => false
    };
    private static bool Size(long width, long height) => width is > 0 and <= 16384 && height is > 0 and <= 16384 && width * height <= 24_000_000;
    private static int U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset,2));
    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset,4));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset,4));
    private static int U24(byte[] bytes, int offset) => bytes[offset] | bytes[offset+1] << 8 | bytes[offset+2] << 16;
    private static bool Bmp(byte[] bytes)
    {
        if (bytes.Length < 26 || bytes[0] != 66 || bytes[1] != 77) return false;
        var dib = I32(bytes,14);
        if (dib == 12) return Size(U16(bytes,18), U16(bytes,20)) && U16(bytes,22) == 1;
        if (dib is not (40 or 52 or 56 or 108 or 124) || bytes.Length < 14 + dib) return false;
        if (U16(bytes,26) != 1 || U32(bytes,30) is 4 or 5) return false; // Embedded JPEG/PNG can disagree with the DIB dimensions.
        return Size(I32(bytes,18), Math.Abs((long)I32(bytes,22)));
    }
    private static bool Gif(byte[] bytes)
    {
        if (bytes.Length < 13 || !(bytes.AsSpan(0,6).SequenceEqual("GIF87a"u8) || bytes.AsSpan(0,6).SequenceEqual("GIF89a"u8))) return false;
        var width = U16(bytes,6); var height = U16(bytes,8); if (!Size(width,height)) return false;
        var offset = 13 + ((bytes[10] & 0x80) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0); var frames = 0;
        while (offset < bytes.Length)
        {
            var marker = bytes[offset++];
            if (marker == 0x3b) return frames == 1 && offset == bytes.Length;
            if (marker == 0x21) { if (offset >= bytes.Length) return false; offset++; if (!SkipBlocks(bytes, ref offset)) return false; continue; }
            if (marker != 0x2c || offset + 9 > bytes.Length || ++frames > 1) return false;
            var frameWidth = U16(bytes,offset+4); var frameHeight = U16(bytes,offset+6);
            if (!Size(frameWidth,frameHeight) || U16(bytes,offset) + frameWidth > width || U16(bytes,offset+2) + frameHeight > height) return false;
            var packed = bytes[offset+8]; offset += 9;
            if ((packed & 0x80) != 0) offset += 3 * (1 << ((packed & 7) + 1));
            if (offset >= bytes.Length || bytes[offset++] is < 2 or > 8 || !SkipBlocks(bytes,ref offset)) return false;
        }
        return false;
    }
    private static bool SkipBlocks(byte[] bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            var count = bytes[offset++]; if (count == 0) return true;
            if (count > bytes.Length - offset) return false; offset += count;
        }
        return false;
    }
    private static bool Webp(byte[] bytes)
    {
        if (bytes.Length < 20 || !bytes.AsSpan(0,4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8,4).SequenceEqual("WEBP"u8) || (long)U32(bytes,4) + 8 != bytes.Length) return false;
        var offset = 12; var canvasWidth = 0; var canvasHeight = 0; var width = 0; var height = 0; var foundImage = false;
        while (offset + 8 <= bytes.Length)
        {
            var kind = bytes.AsSpan(offset,4); var length = U32(bytes,offset+4); offset += 8;
            if (length > bytes.Length - offset) return false;
            if (kind.SequenceEqual("VP8X"u8))
            {
                if (length != 10 || canvasWidth != 0 || (bytes[offset] & 0x02) != 0) return false;
                canvasWidth = U24(bytes,offset+4)+1; canvasHeight = U24(bytes,offset+7)+1; if (!Size(canvasWidth,canvasHeight)) return false;
            }
            else if (kind.SequenceEqual("VP8 "u8))
            {
                if (foundImage || length < 10 || (bytes[offset] & 1) != 0 || !bytes.AsSpan(offset+3,3).SequenceEqual(new byte[] {0x9d,1,0x2a})) return false;
                width = U16(bytes,offset+6) & 0x3fff; height = U16(bytes,offset+8) & 0x3fff; foundImage = true;
            }
            else if (kind.SequenceEqual("VP8L"u8))
            {
                if (foundImage || length < 5 || bytes[offset] != 0x2f) return false;
                var bits = U32(bytes,offset+1); if ((bits >> 29) != 0) return false;
                width = (int)(bits & 0x3fff)+1; height = (int)((bits >> 14) & 0x3fff)+1; foundImage = true;
            }
            else if (kind.SequenceEqual("ANIM"u8) || kind.SequenceEqual("ANMF"u8)) return false;
            if (foundImage && !Size(width,height)) return false;
            offset += (int)length + ((int)length & 1);
        }
        return foundImage && offset == bytes.Length && (canvasWidth == 0 || width <= canvasWidth && height <= canvasHeight);
    }
}
