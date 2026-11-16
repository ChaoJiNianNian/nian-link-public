using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using DesktopToolbox.DeskLink;
using NianLink.MacCandidateDiscovery;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacCandidateBindingTests
{
    private static readonly IPAddress Address = IPAddress.Parse("10.37.1.4");
    private static MacNetworkRecord Good() => new("en1", "en1", 15, NetworkInterfaceType.Wireless80211,
        OperationalStatus.Up, Address, IPAddress.Parse("255.255.255.0"));
    private static MacWifiEvidence Evidence() => new("en1", 15);

    [Fact]
    public void ValidSyntheticInputCannotBecomeLive()
    {
        var binding = MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => [Evidence()]);
        Assert.True(binding.IsStillAvailable());
        Assert.False(binding.HasLiveNetworkOrigin);
        Assert.Equal(Address, binding.Address);
        var factory = typeof(MacCandidateNetworkBinding).GetMethod("CreateForCurrentNetwork")!;
        Assert.Single(factory.GetParameters());
        Assert.Equal("DesktopToolbox.DeskLink.VerifiedLanBinding", factory.GetParameters()[0].ParameterType.FullName);
    }

    [Fact]
    public void LegacySynchronousFactoryCannotMintLiveBinding()
    {
        var placeholder = (VerifiedLanBinding)RuntimeHelpers.GetUninitializedObject(typeof(VerifiedLanBinding));
        Assert.Throws<PlatformNotSupportedException>(() => MacCandidateNetworkBinding.CreateForCurrentNetwork(placeholder));
        Assert.Throws<ArgumentNullException>(() => MacCandidateNetworkBinding.CreateForCurrentNetwork(null!));
    }

    [Fact]
    public async Task SyntheticBindingCannotSupplySharedBindingOrPassLiveRevalidation()
    {
        using var binding = MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => [Evidence()]);
        Assert.False(binding.HasLiveNetworkOrigin);
        Assert.True(binding.RevocationToken.IsCancellationRequested);
        Assert.False(await binding.IsStillAvailableAsync());
        Assert.Throws<InvalidOperationException>(() => binding.SharedBinding);
    }

    [Fact]
    public async Task PublicAsyncFactoryRejectsNonMacAndDoesNotAcceptInjectedEvidence()
    {
        var factory = typeof(MacCandidateNetworkBinding).GetMethod("CreateForCurrentNetworkAsync")!;
        Assert.Equal(new[] { typeof(VerifiedLanBinding), typeof(CancellationToken) }, factory.GetParameters().Select(p => p.ParameterType));
        if (!OperatingSystem.IsMacOS())
        {
            var placeholder = (VerifiedLanBinding)RuntimeHelpers.GetUninitializedObject(typeof(VerifiedLanBinding));
            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => MacCandidateNetworkBinding.CreateForCurrentNetworkAsync(placeholder));
        }
    }

    [Fact]
    public void MissingOrAmbiguousSystemEvidenceCannotCreateBinding()
    {
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => []));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => [Evidence(), Evidence()]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => [Evidence() with { Index = 16 }]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => [Evidence() with { BsdName = "en2" }]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good(), Good()], () => [Evidence()]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good(), Good() with { Id = "other", Address = IPAddress.Parse("10.37.1.9") }], () => [Evidence()]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => [Evidence(), Evidence() with { BsdName = "en2" }]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()], () => [Evidence() with { BsdName = "../en1" }]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => Enumerable.Repeat(Good(), 1025).ToArray(), () => [Evidence()]));
    }

    [Fact]
    public void EntireEvidenceListMustHaveUniqueNamesAndIndices()
    {
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()],
            () => [Evidence(), new("en2", 16), new("en2", 16)]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()],
            () => [Evidence(), new("en2", 16), new("en2", 17)]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good()],
            () => [Evidence(), new("en2", 16), new("en3", 16)]));
    }

    [Fact]
    public void NetworkIdentityMustBeOneToOneAcrossTheEntireSnapshot()
    {
        var other = Good() with { Id = "en2", Name = "en2", Index = 16, Address = IPAddress.Parse("10.37.1.9") };
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(),
            () => [Good(), other, other], () => [Evidence()]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(),
            () => [Good(), other, other with { Index = 17, Address = IPAddress.Parse("10.37.1.10") }], () => [Evidence()]));
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(),
            () => [Good(), other, other with { Id = "en3", Address = IPAddress.Parse("10.37.1.10") }], () => [Evidence()]));
    }

    [Fact]
    public void OneInterfaceMayHaveMultipleDistinctAddresses()
    {
        var additional = Good() with { Address = IPAddress.Parse("10.37.1.9") };
        var binding = MacCandidateNetworkBinding.FromSyntheticForTest(Good(), () => [Good(), additional], () => [Evidence()]);
        Assert.True(binding.IsStillAvailable());
        Assert.False(binding.HasLiveNetworkOrigin);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("status")]
    public void SameInterfaceCannotHaveConflictingMetadata(string conflict)
    {
        var additional = Good() with { Address = IPAddress.Parse("10.37.1.9") };
        additional = conflict switch
        {
            "name" => additional with { Name = "en2" },
            "type" => additional with { Type = NetworkInterfaceType.Ethernet },
            "status" => additional with { Status = OperationalStatus.Down },
            _ => throw new ArgumentOutOfRangeException(nameof(conflict))
        };
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(Good(),
            () => [Good(), additional], () => [Evidence()]));

        var invalid = false;
        var binding = MacCandidateNetworkBinding.FromSyntheticForTest(Good(),
            () => invalid ? [Good(), additional] : [Good()], () => [Evidence()]);
        invalid = true;
        Assert.False(binding.IsStillAvailable());
        invalid = false;
        Assert.False(binding.IsStillAvailable());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnrelatedDuplicateDuringRecheckPermanentlyRevokes(bool duplicateWifi)
    {
        var invalid = false;
        var other = Good() with { Id = "en2", Name = "en2", Index = 16, Address = IPAddress.Parse("10.37.1.9") };
        var binding = MacCandidateNetworkBinding.FromSyntheticForTest(Good(),
            () => invalid && !duplicateWifi ? [Good(), other, other] : [Good()],
            () => invalid && duplicateWifi ? [Evidence(), new("en2", 16), new("en2", 16)] : [Evidence()]);
        invalid = true;
        Assert.False(binding.IsStillAvailable());
        invalid = false;
        Assert.False(binding.IsStillAvailable());
    }

    [Theory]
    [InlineData("id")][InlineData("name")][InlineData("index")][InlineData("type")]
    [InlineData("address")][InlineData("mask")][InlineData("status")][InlineData("evidence")][InlineData("error")]
    public void ChangedOrUnavailableObservationPermanentlyRevokes(string change)
    {
        var item = Good(); var evidence = Evidence(); var fail = false;
        var binding = MacCandidateNetworkBinding.FromSyntheticForTest(item,
            () => fail ? throw new IOException() : [item], () => [evidence]);
        switch (change)
        {
            case "id": item = item with { Id = "other" }; break;
            case "name": item = item with { Name = "en2" }; break;
            case "index": item = item with { Index = 16 }; break;
            case "type": item = item with { Type = NetworkInterfaceType.Ethernet }; break;
            case "address": item = item with { Address = IPAddress.Parse("10.37.1.8") }; break;
            case "mask": item = item with { Mask = IPAddress.Parse("255.255.0.0") }; break;
            case "status": item = item with { Status = OperationalStatus.Down }; break;
            case "evidence": evidence = evidence with { Index = 16 }; break;
            case "error": fail = true; break;
        }
        Assert.False(binding.IsStillAvailable());
        item = Good(); evidence = Evidence(); fail = false;
        Assert.False(binding.IsStillAvailable());
        Assert.False(binding.HasLiveNetworkOrigin);
    }

    [Theory]
    [InlineData("8.8.8.8", "255.255.255.0")]
    [InlineData("10.37.1.0", "255.255.255.0")]
    [InlineData("10.37.1.255", "255.255.255.0")]
    [InlineData("10.37.1.4", "255.255.255.254")]
    [InlineData("10.37.1.4", "255.0.255.0")]
    public void InvalidHostSubnetIsRejected(string address, string mask)
    {
        var item = Good() with { Address = IPAddress.Parse(address), Mask = IPAddress.Parse(mask) };
        Assert.Throws<InvalidOperationException>(() => MacCandidateNetworkBinding.FromSyntheticForTest(item, () => [item], () => [Evidence()]));
    }

    [Fact]
    public void CallerCannotChangeFrozenAddresses()
    {
        var address = IPAddress.Parse("10.37.1.4");
        var item = Good() with { Address = address };
        var binding = MacCandidateNetworkBinding.FromSyntheticForTest(item, () => [item], () => [Evidence()]);
        var exposed = binding.Address;
#pragma warning disable CS0618
        exposed.Address = IPAddress.Parse("10.37.1.5").Address;
#pragma warning restore CS0618
        Assert.Equal(IPAddress.Parse("10.37.1.4"), binding.Address);
        Assert.True(binding.IsStillAvailable());
    }
}
