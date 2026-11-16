namespace DesktopToolbox.DeskLink;

/// <summary>Pairing could not start, and at least one owned resource was not confirmed closed.</summary>
public sealed class PairingStartupCleanupException : InvalidOperationException
{
    public PairingStartupCleanupException(Exception startup, IEnumerable<Exception> cleanup)
        : base("Pairing startup cleanup failed.", new AggregateException([startup, .. cleanup])) { }
}
