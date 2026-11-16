using System.Runtime.InteropServices;
using Nian.Input.Windows;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration.LivePreview;

/// <summary>Draws the actual shared system cursor; owns only temporary icon-info bitmaps.</summary>
internal sealed class GdiLivePreviewCursor
{
    private nint cachedCursor;
    private int width, height, hotspotX, hotspotY;
    public void Draw(nint destination, WindowsDesktopSourceGeometry source, int imageWidth, int imageHeight)
    {
        var current = new GdiLivePreviewNative.CursorInfo { Size = (uint)Marshal.SizeOf<GdiLivePreviewNative.CursorInfo>() };
        if (!GdiLivePreviewNative.GetCursorInfo(ref current) || (current.Flags & 1) == 0 || current.Cursor == 0) return;
        if (current.Position.X < source.Left || current.Position.X >= source.Left + source.Width ||
            current.Position.Y < source.Top || current.Position.Y >= source.Top + source.Height) return;
        if (current.Cursor != cachedCursor && !ReadShape(current.Cursor)) return;
        var drawWidth = Math.Clamp((int)Math.Round(width * imageWidth / source.Width), 1, 512);
        var drawHeight = Math.Clamp((int)Math.Round(height * imageHeight / source.Height), 1, 512);
        var x = (int)Math.Round((current.Position.X - source.Left) * imageWidth / source.Width - hotspotX * (double)drawWidth / width);
        var y = (int)Math.Round((current.Position.Y - source.Top) * imageHeight / source.Height - hotspotY * (double)drawHeight / height);
        // A cursor can change between the read and draw. A failed transient overlay must not stop capture.
        GdiLivePreviewNative.DrawIconEx(destination, x, y, current.Cursor, drawWidth, drawHeight, 0, 0, 3);
    }
    private bool ReadShape(nint handle)
    {
        if (!GdiLivePreviewNative.GetIconInfo(handle, out var icon)) return false;
        try
        {
            var bitmap = icon.Color != 0 ? icon.Color : icon.Mask;
            if (bitmap == 0 || GdiLivePreviewNative.GetBitmapInfo(bitmap, Marshal.SizeOf<GdiLivePreviewNative.BitmapObject>(), out var shape) == 0) return false;
            var h = icon.Color != 0 ? shape.Height : shape.Height / 2;
            if (shape.Width is < 1 or > 1024 || h is < 1 or > 1024 || icon.HotspotX >= shape.Width || icon.HotspotY >= h) return false;
            width = shape.Width; height = h; hotspotX = (int)icon.HotspotX; hotspotY = (int)icon.HotspotY; cachedCursor = handle;
            return true;
        }
        finally
        {
            if (icon.Color != 0) GdiLivePreviewNative.DeleteObject(icon.Color);
            if (icon.Mask != 0 && icon.Mask != icon.Color) GdiLivePreviewNative.DeleteObject(icon.Mask);
        }
    }
}
