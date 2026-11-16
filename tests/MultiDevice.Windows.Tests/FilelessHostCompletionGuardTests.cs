using System.Runtime.CompilerServices;
using NianLink.CandidateDiscovery;

namespace MultiDevice.Windows.Tests;

public sealed class FilelessHostCompletionGuardTests
{
    [Fact]
    public async Task HostCompletionIsUnavailableBeforeFinalAcknowledgementExists()
    {
        // The guard must run before touching a session or vault: no transaction is started.
        var host = (SecurePairingHost)RuntimeHelpers.GetUninitializedObject(typeof(SecurePairingHost));
        await Assert.ThrowsAsync<NotSupportedException>(() => host.CompletePairingAsync(null!));
    }

    [Fact]
    public async Task HostHistoricalRecoveryIsUnavailableUntilEveryPhaseIsSupported()
    {
        var host = (SecurePairingHost)RuntimeHelpers.GetUninitializedObject(typeof(SecurePairingHost));
        await Assert.ThrowsAsync<NotSupportedException>(() => host.RecoverHistoricalPairingAsync(null!));
    }
}
