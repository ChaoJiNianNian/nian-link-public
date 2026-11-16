using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NianLink.PrivateUdpProbe;

internal static class ProbeEnvironment
{
    internal static ProbeInterface[] ProjectInterface(string rawId, IEnumerable<(IPAddress Address, IPAddress Mask)> addresses,
        Func<Guid, bool> isPhysical, bool up, Func<int> getIndex)
    {
        var ipv4 = addresses.Where(value => value.Address.AddressFamily == AddressFamily.InterNetwork).ToArray();
        if (ipv4.Length == 0) return [];
        if (!Guid.TryParse(rawId, out var id) || id == Guid.Empty) throw new InvalidOperationException("IPv4 adapter identity unavailable.");
        int index = getIndex();
        if (index <= 0) throw new InvalidOperationException("IPv4 interface index unavailable.");
        return ipv4.Select(value => new ProbeInterface(id, index, value.Address, value.Mask, isPhysical(id), up)).ToArray();
    }

    internal static async Task<ProbeInterface[]> ReadAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This isolated probe is Windows-only.");
        var command = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { $_.InterfaceGuid.ToString() }";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Physical interface enumeration is unavailable.");
        var output = process.StandardOutput.ReadToEndAsync(ct); var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } throw; }
        var text = await output; _ = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException("Physical interface enumeration failed.");
        var physical = new HashSet<Guid>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(line, out var id)) throw new InvalidOperationException("Physical interface identity is invalid.");
            physical.Add(id);
        }
        var result = new List<ProbeInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(item => (item.Address, item.IPv4Mask ?? IPAddress.Any));
            result.AddRange(ProjectInterface(adapter.Id, addresses, physical.Contains, adapter.OperationalStatus == OperationalStatus.Up,
                () => properties.GetIPv4Properties()?.Index ?? throw new InvalidOperationException("IPv4 interface metadata unavailable.")));
        }
        return result.ToArray();
    }
}
