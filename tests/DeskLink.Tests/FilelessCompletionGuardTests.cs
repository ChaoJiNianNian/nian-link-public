using System.Runtime.CompilerServices;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class FilelessCompletionGuardTests
{
    [Fact]
    public async Task UnifiedPeerOperationIsUnavailableUntilEndToEndValidation()
    {
        var peer = (SecurePairingPeer)RuntimeHelpers.GetUninitializedObject(typeof(SecurePairingPeer));
        await Assert.ThrowsAsync<NotSupportedException>(() => peer.AcceptPairingOperationAsync(null!));
    }
}
