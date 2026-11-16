namespace DesktopToolbox.DeskLink;

public static class DeskflowIpcState
{
    /// <summary>Consumes structured upstream IPC only. Logs never confirm focus or trust.</summary>
    public static DeskLinkState Apply(DeskLinkState state, string command, string argument) => command switch
    {
        "connectionState" when Enum.TryParse<DeskLinkConnectionState>(argument, out var connection) => state with { ConnectionState = connection },
        "connectedClients" => state with { ConnectedComputers = argument.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
        "peerFingerprint" => state with { PendingPeerFingerprint = DeskLinkFingerprint.Normalize(argument) },
        "unrecognisedClient" => state with { LastError = "Unknown Deskflow client: " + argument },
        "connectionRefused" => state with { LastError = "Deskflow connection refused: " + argument },
        _ => state
    };
}
