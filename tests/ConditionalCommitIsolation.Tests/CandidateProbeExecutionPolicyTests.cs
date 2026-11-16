using NianLink.PrivateUdpProbe;

namespace ConditionalCommitIsolation.Tests;

public sealed class CandidateProbeExecutionPolicyTests
{
    [Fact]
    public void OnlyDedicatedApphostAndCorrespondingDllAreAccepted()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "candidate-probe-test"));
        var exe = Path.Combine(root, "CandidateSocketProbe.exe");
        var dll = Path.Combine(root, "CandidateSocketProbe.dll");
        Assert.True(ProbeExecutionPolicy.Matches(exe, dll));
        Assert.False(ProbeExecutionPolicy.Matches(Path.Combine(root, "dotnet.exe"), dll));
        Assert.False(ProbeExecutionPolicy.Matches(exe, Path.Combine(root, "other.dll")));
        Assert.False(ProbeExecutionPolicy.Matches(exe, Path.Combine(root, "other", "CandidateSocketProbe.dll")));
        Assert.False(ProbeExecutionPolicy.Matches("CandidateSocketProbe.exe", dll));
        Assert.False(ProbeExecutionPolicy.Matches(exe, null));
    }
}
