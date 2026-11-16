using System.Net;
using System.Net.Sockets;
using MacManagedDatagramProbe;

public sealed class DatagramRulesTests
{
    private readonly IPEndPoint source = new(IPAddress.Parse("192.168.8.3"), 49002);
    private readonly IPAddress group = IPAddress.Parse("239.255.82.197");
    private readonly byte[] nonce = Convert.FromHexString("00112233445566778899aabbccddeeff");

    [Fact]
    public void AcceptsOnlyMatchingUntruncatedPacket()
    {
        var payload = DatagramRules.Query(nonce);
        Assert.True(DatagramRules.Accept(payload, source, group, 7, SocketFlags.None, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, source, group, 8, SocketFlags.None, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, source, IPAddress.Loopback, 7, SocketFlags.None, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, source, null, 7, SocketFlags.None, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, new IPEndPoint(source.Address, 49003), group, 7, SocketFlags.None, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, new IPEndPoint(IPAddress.Parse("192.168.8.4"), source.Port), group, 7, SocketFlags.None, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, source, group, 7, SocketFlags.Truncated, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, source, group, 7, SocketFlags.ControlDataTruncated, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload.Concat(new byte[] { 0 }).ToArray(), source, group, 7, SocketFlags.None, source, 7, nonce));
        Assert.False(DatagramRules.Accept(payload, source, group, 7, SocketFlags.None, source, 7, new byte[16]));
    }

    [Fact]
    public void InteroperableFramesUseFixedTagAndBinaryNonce()
    {
        Assert.Equal("4e49414e2d534f434b45542d50524f42452d51554552592d76313a00112233445566778899aabbccddeeff", Convert.ToHexStringLower(DatagramRules.Query(nonce)));
        Assert.Equal("4e49414e2d534f434b45542d50524f42452d5245504c592d76313a00112233445566778899aabbccddeeff", Convert.ToHexStringLower(DatagramRules.Reply(nonce)));
    }

    [Fact]
    public void RejectsInvalidParametersAndCannotUseProductPort()
    {
        Assert.True(DatagramRules.Valid(IPAddress.Parse("192.168.8.2"), source.Address, 7, 49001, source.Port, nonce));
        Assert.False(DatagramRules.Valid(IPAddress.Parse("8.8.8.8"), source.Address, 7, 49001, source.Port, nonce));
        Assert.False(DatagramRules.Valid(IPAddress.Parse("192.168.8.2"), source.Address, 0, 49001, source.Port, nonce));
        Assert.False(DatagramRules.Valid(IPAddress.Parse("192.168.8.2"), source.Address, 7, 49473, source.Port, nonce));
        Assert.False(DatagramRules.Valid(IPAddress.Parse("192.168.8.2"), source.Address, 7, 49001, 49001, nonce));
        Assert.False(DatagramRules.Valid(IPAddress.Parse("192.168.8.2"), source.Address, 7, 49001, source.Port, new byte[15]));
    }
}
