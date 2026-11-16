using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;

namespace NianLink.MacDiscoveryBoundary;

// These inputs are fabricated observations, not claims from macOS or permission to use a network.
internal sealed record SyntheticInterface(string Name, int Index, NetworkInterfaceType Type,
    OperationalStatus Status, IPAddress Address, IPAddress Mask);
internal sealed record SyntheticHardware(string Name, int Index, bool Physical);

internal sealed class SyntheticMacMapping
{
    private readonly record struct Frozen(string Name, int Index, NetworkInterfaceType Type, OperationalStatus Status, uint Address, uint Mask);
    private readonly object gate = new();
    private readonly Frozen original;
    private readonly Func<IReadOnlyList<SyntheticInterface>> readInterfaces;
    private readonly Func<IReadOnlyList<SyntheticHardware>> readHardware;
    private bool invalid;

    private SyntheticMacMapping(Frozen original, Func<IReadOnlyList<SyntheticInterface>> readInterfaces,
        Func<IReadOnlyList<SyntheticHardware>> readHardware)
    { this.original = original; this.readInterfaces = readInterfaces; this.readHardware = readHardware; }

    internal bool HasLiveNetworkOrigin => false;
    internal static int ProtocolPort => DiscoveryProtocol.Port;
    internal static IPAddress ProtocolGroup => DiscoveryProtocol.MulticastGroup;

    internal static SyntheticMacMapping Create(IPAddress address, Func<IReadOnlyList<SyntheticInterface>> interfaces,
        Func<IReadOnlyList<SyntheticHardware>> hardware)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(hardware);
        var selected = Select(ToValue(address), interfaces(), hardware());
        return new(selected, interfaces, hardware);
    }

    internal bool IsStillValid()
    {
        lock (gate)
        {
            if (invalid) return false;
            try { invalid = Select(original.Address, readInterfaces(), readHardware()) != original; }
            catch (Exception) { invalid = true; }
            return !invalid;
        }
    }

    private static Frozen Select(uint address, IReadOnlyList<SyntheticInterface> interfaces, IReadOnlyList<SyntheticHardware> hardware)
    {
        if (interfaces is null || hardware is null) throw new InvalidOperationException("Missing synthetic observations.");
        var items = interfaces.Select(Freeze).ToArray();
        var matches = items.Where(item => item.Address == address).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Ambiguous synthetic address.");
        var selected = matches[0];
        if (string.IsNullOrWhiteSpace(selected.Name) || selected.Index <= 0 || selected.Status != OperationalStatus.Up ||
            selected.Type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211) ||
            items.Any(item => item.Name == selected.Name && item.Index != selected.Index || item.Index == selected.Index && item.Name != selected.Name))
            throw new InvalidOperationException("Invalid synthetic interface mapping.");
        var evidence = hardware.Where(item => item.Name == selected.Name || item.Index == selected.Index).ToArray();
        if (evidence.Length != 1 || evidence[0].Name != selected.Name || evidence[0].Index != selected.Index || !evidence[0].Physical)
            throw new InvalidOperationException("Missing or ambiguous synthetic hardware evidence.");
        try { _ = new DiscoverySubnet(FromValue(selected.Address), FromValue(selected.Mask)); }
        catch (ArgumentException e) { throw new InvalidOperationException("Invalid synthetic subnet.", e); }
        return selected;
    }

    private static Frozen Freeze(SyntheticInterface item)
    {
        if (item is null) throw new InvalidOperationException("Missing synthetic interface.");
        return new(item.Name, item.Index, item.Type, item.Status, ToValue(item.Address), ToValue(item.Mask));
    }
    private static uint ToValue(IPAddress address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("IPv4 required.");
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }
    private static IPAddress FromValue(uint value)
    {
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
