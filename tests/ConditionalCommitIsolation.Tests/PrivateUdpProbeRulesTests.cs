using System.Net;
using System.Net.Sockets;
using NianLink.PrivateUdpProbe;

namespace ConditionalCommitIsolation.Tests;

public sealed class PrivateUdpProbeRulesTests
{
    private static readonly IPAddress Local = IPAddress.Parse("10.12.34.56");
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");
    private static ProbeInterface Candidate() => new(Guid.NewGuid(), 17, Local, Mask, true, true);

    [Fact]
    public void NoIpv4SkipsIndexButIpv4IndexFailureStopsEnumeration()
    {
        var id = Guid.NewGuid().ToString("D");
        Assert.Empty(ProbeEnvironment.ProjectInterface(id, [(IPAddress.IPv6Loopback, IPAddress.Any)], _ => true, true,
            () => throw new InvalidOperationException("Index must not be queried.")));
        Assert.Throws<InvalidOperationException>(() => ProbeEnvironment.ProjectInterface(id, [(Local, Mask)], _ => true, true,
            () => throw new InvalidOperationException("Index unavailable.")));
        var entry = Assert.Single(ProbeEnvironment.ProjectInterface(id, [(Local, Mask)], _ => true, true, () => 17));
        Assert.Equal(17, entry.Index);
    }

    [Fact]
    public void InvalidAdapterIdWithIpv4FailsClosed()
    {
        Assert.Empty(ProbeEnvironment.ProjectInterface("invalid", [], _ => true, true, () => throw new InvalidOperationException()));
        Assert.Throws<InvalidOperationException>(() => ProbeEnvironment.ProjectInterface("invalid", [(Local, Mask)], _ => true, true, () => 17));
    }

    [Fact]
    public async Task ExclusiveLoopbackSocketReleasesPortAfterCancellation()
    {
        using var first = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)first.LocalEndPoint!;
        using var conflict = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        Assert.Throws<SocketException>(() => conflict.Bind(endpoint));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await first.ReceiveFromAsync(new byte[23].AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellation.Token));
        first.Dispose();
        using var replacement = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        replacement.Bind(endpoint);
    }

    [Fact]
    public void ParentIdentityRequiresActualServerParentPathAndChallenge()
    {
        byte[] challenge = Guid.NewGuid().ToByteArray();
        Assert.True(ProbeParentIdentity.Matches(42, 42, 42, "same.exe", "same.exe", challenge, challenge));
        Assert.False(ProbeParentIdentity.Matches(42, 43, 42, "same.exe", "same.exe", challenge, challenge));
        Assert.False(ProbeParentIdentity.Matches(42, 42, 43, "same.exe", "same.exe", challenge, challenge));
        Assert.False(ProbeParentIdentity.Matches(42, 42, 42, "other.exe", "same.exe", challenge, challenge));
        Assert.False(ProbeParentIdentity.Matches(42, 42, 42, "same.exe", "same.exe", challenge, Guid.NewGuid().ToByteArray()));
    }

    [Fact]
    public async Task FailedIdentityReadNeverKillsButWaitsForChildExit()
    {
        bool killed = false, waited = false;
        await ProbeChildCleanup.CompleteAsync(false, () => throw new InvalidOperationException(),
            () => killed = true, _ => { waited = true; return Task.CompletedTask; });
        Assert.False(killed);
        Assert.True(waited);
    }

    [Fact]
    public async Task FailedIdentityRecheckStillWaitsAndReportsFailure()
    {
        bool killed = false, waited = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProbeChildCleanup.CompleteAsync(true,
            () => throw new InvalidOperationException(), () => killed = true,
            _ => { waited = true; return Task.CompletedTask; }));
        Assert.False(killed);
        Assert.True(waited);
    }

    [Fact]
    public void AutomaticSelectionRefusesMultipleEligibleInterfaces()
    {
        var one = Candidate();
        var two = one with { InterfaceId = Guid.NewGuid(), Index = 18, Address = IPAddress.Parse("10.12.34.57") };
        Assert.Equal(one, PrivateUdpProbeRules.SelectUnique([one]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.SelectUnique([]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.SelectUnique([one, two]));
    }

    [Fact]
    public void ExplicitOptInAndUniquePhysicalLivePrivateMappingAreRequired()
    {
        var one = Candidate();
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(false, Local, [one]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(true, Local, []));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(true, Local, [one, one with { InterfaceId = Guid.NewGuid() }]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(true, Local, [one with { Physical = false }]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(true, Local, [one with { Up = false }]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(true, Local, [one with { Index = 0 }]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(true, IPAddress.Loopback, [one with { Address = IPAddress.Loopback }]));
        Assert.Throws<InvalidOperationException>(() => PrivateUdpProbeRules.Select(true, IPAddress.Parse("8.8.8.8"), [one with { Address = IPAddress.Parse("8.8.8.8") }]));
        Assert.Equal(one, PrivateUdpProbeRules.Select(true, Local, [one]));
    }

    [Fact]
    public void EveryRelevantInterfaceChangeInvalidatesSelection()
    {
        var one = Candidate();
        Assert.True(PrivateUdpProbeRules.IsCurrent(one, [one]));
        Assert.False(PrivateUdpProbeRules.IsCurrent(one, [one with { Index = 18 }]));
        Assert.False(PrivateUdpProbeRules.IsCurrent(one, [one with { InterfaceId = Guid.NewGuid() }]));
        Assert.False(PrivateUdpProbeRules.IsCurrent(one, [one with { Mask = IPAddress.Parse("255.255.0.0") }]));
        Assert.False(PrivateUdpProbeRules.IsCurrent(one, [one with { Address = IPAddress.Parse("10.12.34.57") }]));
        Assert.False(PrivateUdpProbeRules.IsCurrent(one, [one with { Up = false }]));
        Assert.False(PrivateUdpProbeRules.IsCurrent(one, [one, one with { InterfaceId = Guid.NewGuid() }]));
    }

    [Fact]
    public void PacketRequiresExactNonceLengthDestinationInterfaceAndSource()
    {
        var selected = Candidate(); var group = IPAddress.Parse("239.255.100.101"); var nonce = Guid.NewGuid().ToByteArray();
        var query = PrivateUdpProbeRules.BuildPacket(false, nonce);
        Assert.True(PrivateUdpProbeRules.Accept(query, false, nonce, selected, group, group, selected.Index, Local, 50001, 0, false));
        Assert.False(PrivateUdpProbeRules.Accept(query, false, Guid.NewGuid().ToByteArray(), selected, group, group, selected.Index, Local, 50001, 0, false));
        Assert.False(PrivateUdpProbeRules.Accept(query, false, nonce, selected, group, Local, selected.Index, Local, 50001, 0, false));
        Assert.False(PrivateUdpProbeRules.Accept(query, false, nonce, selected, group, group, selected.Index + 1, Local, 50001, 0, false));
        Assert.False(PrivateUdpProbeRules.Accept(query, false, nonce, selected, group, group, selected.Index, IPAddress.Parse("10.12.34.2"), 50001, 0, false));
        Assert.False(PrivateUdpProbeRules.Accept(query[..^1], false, nonce, selected, group, group, selected.Index, Local, 50001, 0, false));
        Assert.False(PrivateUdpProbeRules.Accept(query.Append((byte)0).ToArray(), false, nonce, selected, group, group, selected.Index, Local, 50001, 0, false));
        Assert.False(PrivateUdpProbeRules.Accept(query, false, nonce, selected, group, group, selected.Index, Local, 50001, 0, true));
        var reply = PrivateUdpProbeRules.BuildPacket(true, nonce);
        Assert.True(PrivateUdpProbeRules.Accept(reply, true, nonce, selected, group, Local, selected.Index, Local, 50002, 50002, false));
        Assert.False(PrivateUdpProbeRules.Accept(reply, true, nonce, selected, group, Local, selected.Index, Local, 50003, 50002, false));
    }
}
