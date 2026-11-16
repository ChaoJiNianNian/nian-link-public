using DesktopToolbox.App.DeskLink;
using System.Text.Json;

namespace NianLink.Smoke.Tests;

public sealed class MiniSettingsBridgeTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ConnectionSettingsRequestAllowsOnlyEmptyPayload()
    {
        var request = MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "connection.openSettings", payload = new { } }));
        Assert.Equal("connection.openSettings", request.Action);
        Assert.Empty(request.Payload.EnumerateObject());
    }

    [Fact]
    public void ConnectionSettingsRequestRejectsExtraParameters()
    {
        Assert.Throws<ArgumentException>(() => MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "connection.openSettings", payload = new { path = "elsewhere" } })));
    }

    [Fact]
    public void MultiDeviceSelectionAcceptsOnlyOneValidDeviceName()
    {
        var request = MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "device.select", payload = new { name = "PC-ONE" } }));
        Assert.Equal("PC-ONE", request.Payload.GetProperty("name").GetString());
        Assert.Throws<ArgumentException>(() => MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "device.select", payload = new { name = "bad name" } })));
        Assert.Throws<ArgumentException>(() => MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "device.select", payload = new { name = "PC-ONE", path = "elsewhere" } })));
    }

    [Fact]
    public void DeviceRenderAcknowledgementRequiresNameAndNonnegativeVersion()
    {
        var value = MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "device.rendered", payload = new { name = "PC-ONE", version = 2 } }));
        Assert.Equal(2, value.Payload.GetProperty("version").GetInt64());
        Assert.Throws<ArgumentException>(() => MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "device.rendered", payload = new { name = "PC-ONE", version = -1 } })));
        Assert.Throws<ArgumentException>(() => MiniBridgePolicy.Parse(JsonSerializer.Serialize(new { id = Id, action = "device.rendered", payload = new { name = "PC-ONE", version = 2, secret = "ignored" } })));
    }
}
