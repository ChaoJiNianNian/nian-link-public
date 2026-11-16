namespace DesktopToolbox.DeskLink.Collaboration.LivePreview;

internal static class LivePreviewFrameValidation
{
    internal static bool IsFailureCode(string? code) => code is "permission" or "locked" or "source-unavailable" or "source-changed" or "capture-failed" or "unsupported" or "stopped";
    internal static void Validate(LivePreviewCapturePacket packet)
    {
        if (packet.Sequence <= 0 || packet.JpegBytes is null) throw new ArgumentException("Invalid media packet.");
        if (packet.Kind == LivePreviewCaptureKind.Image)
        {
            if (packet.CapturedAtUtc <= DateTimeOffset.UnixEpoch || packet.JpegBytes.Length > LivePreviewCaptureOptions.MaximumFrameBytes ||
                packet.FailureCode is not null || !CollaborationImageBounds.IsBoundedJpeg(packet.JpegBytes, packet.Width, packet.Height))
                throw new ArgumentException("Invalid bounded media image.");
            return;
        }
        if (packet.Width != 0 || packet.Height != 0 || packet.CapturedAtUtc != default || packet.JpegBytes.Length != 0 ||
            (packet.Kind == LivePreviewCaptureKind.HealthyIdle ? packet.FailureCode is not null :
                packet.Kind != LivePreviewCaptureKind.Unavailable || !IsFailureCode(packet.FailureCode))) throw new ArgumentException("Invalid media health packet.");
    }
}
