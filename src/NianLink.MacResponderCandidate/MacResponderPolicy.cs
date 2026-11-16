using DesktopToolbox.DeskLink;

namespace NianLink.MacResponderCandidate;

internal sealed class MacResponderPolicy : IResponderPolicy
{
    private readonly DiscoveryResponderSession session;
    internal MacResponderPolicy(DiscoveryResponderSession session) => this.session = session;
    public byte[]? Reply(ResponderPacket packet)
    {
        if (packet.Source is null || packet.Destination is null) return null;
        return session.Reply(packet.Data, packet.Source, packet.Destination, packet.InterfaceIndex);
    }
    public void Dispose() => session.Dispose();
}
