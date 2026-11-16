using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public class DeviceAliasesTests
{
    private static readonly string Peer = "peer:" + new string('A', 64);
    private static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>
    {
        ["local"] = "PC-AAAAAAAAAAAA", [Peer] = "PC-BBBBBBBBBBBB"
    };

    [Fact]
    public void ChineseNamesDoNotChangeStableIdentityOrOtherDevice()
    {
        var aliases = DeviceAliases.Empty.Rename("local", " 台式机 ", Known).Rename(Peer, "Mac 办公室", Known);
        Assert.Equal("台式机", aliases.Resolve("local", Known["local"]));
        Assert.Equal("Mac 办公室", aliases.Resolve(Peer, Known[Peer]));
        Assert.Equal("PC-BBBBBBBBBBBB", Known[Peer]);
        Assert.Equal("台式机", DeviceAliases.From(aliases.Snapshot()).Resolve("local", Known["local"]));
    }

    [Fact]
    public void DuplicateDisplayNamesAreRejectedWithoutChangingPreviousValue()
    {
        var aliases = DeviceAliases.Empty.Rename("local", "工作电脑", Known);
        Assert.Throws<ArgumentException>(() => aliases.Rename(Peer, "工作电脑", Known));
        Assert.Equal(Known[Peer], aliases.Resolve(Peer, Known[Peer]));
        Assert.Throws<ArgumentException>(() => DeviceAliases.Empty.Rename(Peer, Known["local"], Known));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("one\ntwo")]
    [InlineData("123456789012345678901234567890123")]
    public void InvalidDisplayNamesAreRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => DeviceAliases.Empty.Rename("local", value, Known));
    }

    [Fact]
    public void InvalidKeysOrDamagedSavedDataAreNotSilentlyAccepted()
    {
        Assert.Throws<ArgumentException>(() => DeviceAliases.From(new Dictionary<string, string> { ["../path"] = "名称" }));
        Assert.Throws<ArgumentException>(() => DeviceAliases.From(new Dictionary<string, string> { ["local"] = "line\nbreak" }));
    }

    [Fact]
    public void ReplacedDeviceWithSameProtocolNameDoesNotInheritOldAlias()
    {
        var aliases = DeviceAliases.Empty.Rename(Peer, "工作电脑", Known);
        var replacement = "peer:" + new string('B', 64);
        Assert.Equal("PC-BBBBBBBBBBBB", aliases.Resolve(replacement, "PC-BBBBBBBBBBBB"));
    }
}
