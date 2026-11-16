namespace DesktopToolbox.DeskLink.Coordination;

/// <summary>Protected topology with every non-host computer bound to its current pairing grant.</summary>
public sealed record MultiScreenPeerGrant(string DeviceName, string GrantKey)
{
    public override string ToString() => $"MultiScreenPeerGrant {{ DeviceName = {DeviceName}, GrantKey = [redacted] }}";
}
/// <summary>Safe UI-facing topology; does not expose pairing-grant identifiers.</summary>
public sealed record MultiScreenLayout(string HostName, IReadOnlyList<string> Peers, IReadOnlyList<MultiScreenDefinition> Screens);
public sealed record MultiScreenConfiguration(string HostName, IReadOnlyList<MultiScreenPeerGrant> Peers, IReadOnlyList<MultiScreenDefinition> Screens)
{
    public MultiScreenConfiguration ValidateAndCopy()
    {
        ArgumentNullException.ThrowIfNull(Peers); ArgumentNullException.ThrowIfNull(Screens);
        if (Peers.Count > 4 || Peers.Any(peer => peer is null || peer.GrantKey is not { Length: 64 } || peer.GrantKey.Any(c => !char.IsAsciiHexDigitUpper(c))) ||
            Peers.Select(peer => peer.GrantKey).Distinct(StringComparer.Ordinal).Count() != Peers.Count)
            throw new ArgumentException("屏幕配置的设备授权无效。");
        var topology = MultiScreenTopology.Create(HostName, Peers.Select(peer => peer.DeviceName), Screens);
        var attached = topology.Screens.SelectMany(screen => screen.Attachments).Select(item => item.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Peers.Any(peer => !attached.Contains(peer.DeviceName))) throw new ArgumentException("屏幕配置包含未使用的设备授权。");
        return new(HostName, Array.AsReadOnly(Peers.ToArray()), Array.AsReadOnly(topology.Screens.ToArray()));
    }
}
