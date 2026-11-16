using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacDisplayBridgeOptionsTests
{
    [Fact] public void ListAcceptsNoAdditionalArguments() { Assert.Equal("list", MacDisplayBridgeOptions.Parse(["list"]).Action); Assert.Throws<ArgumentException>(() => MacDisplayBridgeOptions.Parse(["list", "anything"])); }
    [Fact] public void ReadAcceptsOnlyTwoExactNumericIdentityFields() { var value = MacDisplayBridgeOptions.Parse(["read", "--vendor", "25001", "--product", "10161"]); Assert.Equal(25001u, value.VendorId); Assert.Throws<ArgumentException>(() => MacDisplayBridgeOptions.Parse(["read", "--vendor", "25001", "--product", "10161", "--input", "15"])); }
    [Fact] public void SetAcceptsAnExplicitByteSizedInputOnly() { Assert.Equal((ushort)15, MacDisplayBridgeOptions.Parse(["set", "--vendor", "25001", "--product", "10161", "--input", "15"]).Input); Assert.Throws<ArgumentException>(() => MacDisplayBridgeOptions.Parse(["set", "--vendor", "25001", "--product", "10161", "--input", "256"])); }
    [Fact] public void DuplicateAndArbitraryCommandArgumentsAreRejected() { Assert.Throws<ArgumentException>(() => MacDisplayBridgeOptions.Parse(["set", "--vendor", "25001", "--vendor", "25001", "--product", "10161", "--input", "15"])); Assert.Throws<ArgumentException>(() => MacDisplayBridgeOptions.Parse(["exec", "some-command"])); }
}
