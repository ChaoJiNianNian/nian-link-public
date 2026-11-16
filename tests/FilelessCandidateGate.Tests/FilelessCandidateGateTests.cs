using DesktopToolbox.DeskLink;

namespace FilelessCandidateGate.Tests;

public sealed class FilelessCandidateGateTests
{
    [Fact]
    public void ExplicitIsolatedCandidateCanEnterAfterOtherSafetyChecks()
    {
        FilelessPairingAvailability.EnsureCompletionAvailable();
    }
}
