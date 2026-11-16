using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace MultiDevice.Windows.Tests;

public sealed class WindowsHardwareInterfaceTests
{
    private static readonly Guid Id = Guid.Parse("d12e3ee6-7539-491b-89c8-fcb5b2d4ff16");
    private static NativeHardwareInterfaceSnapshot Good() => new(0, 12, Id, 1);

    [Fact]
    public void NativeLayoutMatchesWindowsX64Headers()
    {
        Assert.Equal(1352, Marshal.SizeOf<MibIfRow2>());
        Assert.Equal(8, Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceIndex)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceGuid)).ToInt32());
        Assert.Equal(1152, Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.Flags)).ToInt32());
    }

    [Fact]
    public void OnlySuccessfulMatchingHardwareResultPasses()
    {
        var valid = Good();
        Assert.True(WindowsHardwareInterface.Matches(12, Id.ToString(), valid));
        Assert.False(WindowsHardwareInterface.Matches(12, Id.ToString(), valid with { Status = 2 }));
        Assert.False(WindowsHardwareInterface.Matches(12, Id.ToString(), valid with { Index = 13 }));
        Assert.False(WindowsHardwareInterface.Matches(12, Id.ToString(), valid with { Id = Guid.NewGuid() }));
        Assert.False(WindowsHardwareInterface.Matches(12, Id.ToString(), valid with { Flags = 0 }));
        Assert.False(WindowsHardwareInterface.Matches(12, Id.ToString(), valid with { Flags = 2 }));
        Assert.False(WindowsHardwareInterface.Matches(0, Id.ToString(), valid));
        Assert.False(WindowsHardwareInterface.Matches(12, "invalid", valid));
        Assert.False(WindowsHardwareInterface.Matches(12, Guid.Empty.ToString(), valid));
    }

    [Fact]
    public void CandidateBuildDoesNotExposeInjectableNativeQuery()
    {
        var methods = typeof(WindowsHardwareInterface).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "IsHardware").ToArray();
        var method = Assert.Single(methods);
        Assert.Single(method.GetParameters());
        Assert.DoesNotContain(typeof(WindowsHardwareInterface).GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType.IsGenericType &&
                parameter.ParameterType.GetGenericTypeDefinition() == typeof(Func<,>)));
    }

    [Fact]
    public void CandidateTransportDoesNotExposeTestInjectionOrArbitraryPayloadSend()
    {
        var runs = typeof(CandidateDiscoveryRun).GetMethods(BindingFlags.Static | BindingFlags.NonPublic);
        Assert.DoesNotContain(runs, method => method.Name is "ForScanTest" or "ForResponderTest" or "ForScanHandoffTest" or "ForScanWithCancellationTest");
        Assert.Empty(typeof(CandidateReviewSelection).GetConstructors());
        var sends = typeof(ICandidateDiscoveryTransport).GetMethods();
        Assert.DoesNotContain(sends.SelectMany(method => method.GetParameters()), parameter =>
            parameter.ParameterType == typeof(byte[]) || parameter.ParameterType == typeof(System.Net.IPEndPoint));
        var socketMethods = typeof(WindowsDiscoveryDatagramSocket).GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.DoesNotContain(socketMethods, method => method.Name.Contains("Synthetic", StringComparison.Ordinal));
        Assert.Null(typeof(WindowsDiscoveryDatagramSocket).Assembly.GetType("NianLink.CandidateDiscovery.CandidateSyntheticRules"));
    }

    [Fact]
    public void SharedDiscoveryReviewRequiresLeaseAndContainsNoTestFactory()
    {
        var reviews = typeof(MultiPeerPairingCoordinator).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "ReviewForDiscovery").ToArray();
        var review = Assert.Single(reviews);
        Assert.Equal(typeof(DiscoveryReviewLease), review.GetParameters().Last().ParameterType);
        var helpers = typeof(DiscoveryReviewLease).GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        Assert.DoesNotContain(helpers, method => method.Name is "ForTest" or "RecordForTest" or "ClaimForTest");
    }

    [LocalHardwareFact]
    public async Task ActualSelectedInterfaceAgreesWithWindowsPhysicalAdapterListing()
    {
        var addresses = VerifiedLanBinding.GetEligibleAddresses();
        Assert.True(addresses.Count == 1);
        var approved = VerifiedLanBinding.VerifyCurrentNetwork(addresses[0], true);
        var binding = DiscoveryNetworkBinding.CreateForCurrentNetwork(approved);
        Assert.True(Guid.TryParse(binding.InterfaceId, out var expected));
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                     "Get-NetAdapter -Physical -ErrorAction Stop | ForEach-Object { $_.InterfaceGuid.ToString() }" })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Physical adapter listing unavailable.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } throw; }
        var text = await output;
        _ = await errors;
        Assert.Equal(0, process.ExitCode);
        var listed = new HashSet<Guid>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.True(Guid.TryParse(line, out var id));
            listed.Add(id);
        }
        if (!listed.Contains(expected)) throw new InvalidOperationException("Selected interface is not listed as physical.");
        Assert.True(WindowsHardwareInterface.IsHardware(binding));
    }
}

public sealed class LocalHardwareFactAttribute : FactAttribute
{
    public LocalHardwareFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            Environment.GetEnvironmentVariable("NIAN_HARDWARE_INTERFACE_TEST") != "1")
            Skip = "Windows x64 physical interface verification requires explicit local test opt-in.";
    }
}
