using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacScreenCaptureReplyTests
{
    [Fact]
    public void PermissionDeniedDoesNotRetainAnOlderDesktopImage()
    {
        var reply = MacScreenCaptureReply.Parse("{\"status\":\"permissionRequired\",\"message\":\"请在系统设置允许屏幕录制\"}");
        Assert.False(reply.IsFrame); Assert.Null(reply.JpegBytes); Assert.Equal("permissionRequired", reply.Status);
    }

    [Fact]
    public void AFrameMustContainBoundedValidJpegAndPositiveDimensions()
    {
        var reply = MacScreenCaptureReply.Parse("{\"status\":\"frame\",\"width\":320,\"height\":180,\"jpeg\":\"/9j/2Q==\"}");
        Assert.True(reply.IsFrame); Assert.Equal(320, reply.Width); Assert.Equal(new byte[] {255,216,255,217}, reply.JpegBytes);
        Assert.Throws<InvalidDataException>(() => MacScreenCaptureReply.Parse("{\"status\":\"frame\",\"width\":0,\"height\":180,\"jpeg\":\"/9j/2Q==\"}"));
        Assert.Throws<InvalidDataException>(() => MacScreenCaptureReply.Parse("{\"status\":\"frame\",\"width\":320,\"height\":180,\"jpeg\":\"aGVsbG8=\"}"));
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("noDisplay")]
    [InlineData("sourceUnavailable")]
    [InlineData("captureFailed")]
    public void UnavailableStatesAreExplicitRatherThanAFabricatedOnlineFrame(string status)
    {
        var reply = MacScreenCaptureReply.Parse("{\"status\":\"" + status + "\"}");
        Assert.False(reply.IsFrame); Assert.Null(reply.JpegBytes); Assert.Equal(status, reply.Status);
    }

    [Fact]
    public void NativeGeometryUsesTheActualDisplayLogicalBoundsRatherThanEncodedPixels()
    {
        var reply = MacScreenCaptureReply.Parse("""
            {"status":"frame","width":320,"height":180,"jpeg":"/9j/2Q==","geometry":{"sourceId":"mac-display-7","revision":"layout-1","left":-1512,"top":72,"width":1512,"height":982,"imageWidth":320,"imageHeight":180}}
            """);
        Assert.NotNull(reply.Geometry);
        Assert.Equal(-1512, reply.Geometry.Left); Assert.Equal(982, reply.Geometry.Height);
        Assert.Equal(320, reply.Geometry.ImageWidth); Assert.Equal(180, reply.Geometry.ImageHeight);
    }

    [Fact]
    public void LegacyNativeReplyHasNoControllableGeometryAndUnavailableReplyDiscardsIt()
    {
        Assert.Null(MacScreenCaptureReply.Parse("{\"status\":\"frame\",\"width\":320,\"height\":180,\"jpeg\":\"/9j/2Q==\"}").Geometry);
        Assert.Null(MacScreenCaptureReply.Parse("{\"status\":\"locked\",\"geometry\":{}}").Geometry);
    }

    [Fact]
    public void SourceGeometryQueryAndCaptureShareStrictBoundedGeometryParsing()
    {
        const string geometry = """
            {"sourceId":"mac-display-7","revision":"layout-1","left":0,"top":0,"width":1512,"height":982,"imageWidth":320,"imageHeight":180}
            """;
        Assert.NotNull(MacScreenCaptureReply.ParseSourceGeometry("{\"status\":\"sourceGeometry\",\"geometry\":" + geometry + "}", 320, 180));
        Assert.Null(MacScreenCaptureReply.ParseSourceGeometry("{\"status\":\"sourceUnavailable\"}", 320, 180));
        Assert.Throws<InvalidDataException>(() => MacScreenCaptureReply.ParseSourceGeometry("{\"status\":\"sourceGeometry\",\"geometry\":" + geometry + "}", 640, 360));
        Assert.Throws<InvalidDataException>(() => MacScreenCaptureReply.Parse("{\"status\":\"frame\",\"width\":320,\"height\":180,\"jpeg\":\"/9j/2Q==\",\"geometry\":" + geometry.Replace("\"width\":1512", "\"width\":0") + "}"));
    }
}
