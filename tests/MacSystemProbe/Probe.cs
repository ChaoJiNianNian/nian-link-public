using System.Text.Json;

namespace NianLink.MacDiscoveryBoundary;

// This diagnostic is intentionally not referenced by the Mac app or discovery transport.
internal static class MacProbe
{
    internal static int Run(string[] args, bool isMac, Func<IReadOnlyList<string>> collect, TextWriter output, TextWriter errors)
    {
        if (args is null || args.Length != 1 || !string.Equals(args[0], "--run", StringComparison.Ordinal))
        {
            errors.WriteLine("Explicit --run required.");
            return 2;
        }
        if (!isMac)
        {
            errors.WriteLine("macOS required.");
            return 3;
        }
        string json;
        try
        {
            var names = collect();
            if (names is null || names.Count > 1024) throw new InvalidDataException();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var frozen = new string[names.Count];
            for (int i = 0; i < frozen.Length; i++)
            {
                var name = names[i];
                if (string.IsNullOrEmpty(name) || name.Length > 63 ||
                    name.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')) ||
                    !unique.Add(name)) throw new InvalidDataException();
                frozen[i] = name;
            }
            json = JsonSerializer.Serialize(new { version = 1, vlan_compatible_candidates = frozen });
        }
        catch (Exception)
        {
            errors.WriteLine("System interface query failed.");
            return 4;
        }
        output.WriteLine(json);
        return 0;
    }
}
