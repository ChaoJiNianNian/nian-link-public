namespace DesktopToolbox.DeskLink;

/// <summary>Protects the candidate entry points until final acknowledgement and recovery are complete.</summary>
public static class FilelessPairingAvailability
{
    public static void EnsureCompletionAvailable()
    {
#if !FILELESS_PAIRING_ISOLATED_TEST
        throw new NotSupportedException("Fileless pairing is unavailable until transaction recovery and end-to-end validation are complete.");
#endif
    }
}
