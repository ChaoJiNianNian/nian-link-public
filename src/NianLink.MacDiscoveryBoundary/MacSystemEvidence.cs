using System.Runtime.InteropServices;
using System.Text;

namespace NianLink.MacDiscoveryBoundary;

// System-reported VLAN-compatible interfaces only. This is not physical trust or network authorization.
internal static class MacSystemEvidence
{
    internal static IReadOnlyList<string> Read()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS is required.");
        return ReadCore(new NativeSystemConfiguration());
    }

    // Isolated candidate has no public API or path into the application. Injected observations are not system evidence.
    internal static IReadOnlyList<string> ReadForTest(IMacSystemConfiguration native) => ReadCore(native);

    private static IReadOnlyList<string> ReadCore(IMacSystemConfiguration native)
    {
        ArgumentNullException.ThrowIfNull(native);
        var array = native.CopyInterfaces();
        if (array == 0) throw new InvalidOperationException("System interface list unavailable.");
        try
        {
            if (!native.IsArray(array)) throw new InvalidDataException("System interface list has an unexpected type.");
            var count = native.Count(array);
            if (count is < 0 or > 1024) throw new InvalidDataException("System interface list is invalid or too large.");
            var names = new List<string>((int)count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                var item = native.Item(array, i);
                if (item == 0 || !native.IsInterface(item)) throw new InvalidDataException("System interface entry is invalid.");
                var name = native.Name(item);
                if (string.IsNullOrEmpty(name) || name.Length > 63 ||
                    name.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')) ||
                    !seen.Add(name)) throw new InvalidDataException("System interface name is invalid or duplicated.");
                names.Add(name);
            }
            return names.AsReadOnly();
        }
        finally { native.Release(array); }
    }
}

internal interface IMacSystemConfiguration
{
    nint CopyInterfaces();
    bool IsArray(nint value);
    long Count(nint value);
    nint Item(nint value, int index);
    bool IsInterface(nint item);
    string Name(nint item);
    void Release(nint value);
}

internal sealed class NativeSystemConfiguration : IMacSystemConfiguration
{
    private const string Sc = "/System/Library/Frameworks/SystemConfiguration.framework/SystemConfiguration";
    private const string Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    [DllImport(Sc)] private static extern nint SCVLANInterfaceCopyAvailablePhysicalInterfaces();
    [DllImport(Sc)] private static extern nuint SCNetworkInterfaceGetTypeID();
    [DllImport(Sc)] private static extern nint SCNetworkInterfaceGetBSDName(nint item);
    [DllImport(Cf)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(Cf)] private static extern nuint CFArrayGetTypeID();
    [DllImport(Cf)] private static extern nint CFArrayGetCount(nint value);
    [DllImport(Cf)] private static extern nint CFArrayGetValueAtIndex(nint value, nint index);
    [DllImport(Cf)] private static extern nuint CFStringGetTypeID();
    [DllImport(Cf)] private static extern nint CFStringGetLength(nint value);
    [DllImport(Cf)] private static extern byte CFStringGetCString(nint value, byte[] buffer, nint size, uint encoding);
    [DllImport(Cf)] private static extern void CFRelease(nint value);

    public nint CopyInterfaces() => SCVLANInterfaceCopyAvailablePhysicalInterfaces();
    public bool IsArray(nint value) => CFGetTypeID(value) == CFArrayGetTypeID();
    public long Count(nint value) => CFArrayGetCount(value);
    public nint Item(nint value, int index) => CFArrayGetValueAtIndex(value, index);
    public bool IsInterface(nint item) => CFGetTypeID(item) == SCNetworkInterfaceGetTypeID();
    public string Name(nint item)
    {
        var value = SCNetworkInterfaceGetBSDName(item); // Get: borrowed from the owning array.
        if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) throw new InvalidDataException("System interface name unavailable.");
        var length = CFStringGetLength(value);
        if (length is <= 0 or > 63) throw new InvalidDataException("System interface name is too long.");
        var bytes = new byte[64];
        if (CFStringGetCString(value, bytes, bytes.Length, Utf8) == 0) throw new InvalidDataException("System interface name is not representable.");
        var end = Array.IndexOf(bytes, (byte)0);
        if (end != (long)length) throw new InvalidDataException("System interface name contains invalid characters.");
        return Encoding.ASCII.GetString(bytes, 0, end);
    }
    public void Release(nint value) => CFRelease(value); // Copy: owned once; elements and names are borrowed.
}
