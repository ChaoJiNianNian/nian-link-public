using System.Globalization;
using System.Text;

namespace DesktopToolbox.DeskLink;

/// <summary>Local labels only. Keys never replace authenticated device identities.</summary>
public sealed class DeviceAliases
{
    private readonly Dictionary<string, string> values;
    private DeviceAliases(Dictionary<string, string> values) => this.values = values;
    public static DeviceAliases Empty => new(new Dictionary<string, string>(StringComparer.Ordinal));

    public static DeviceAliases From(IReadOnlyDictionary<string, string> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Count > 128) throw new ArgumentException("保存的设备名称过多。");
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in saved)
        {
            ValidateKey(key);
            var normalized = Normalize(value);
            if (!string.Equals(value, normalized, StringComparison.Ordinal) || !copy.TryAdd(key, value))
                throw new ArgumentException("保存的设备名称无效。");
        }
        return new(copy);
    }

    public string Resolve(string key, string fallback)
    {
        ValidateKey(key);
        return values.TryGetValue(key, out var alias) ? alias : fallback;
    }

    public DeviceAliases Rename(string key, string value, IReadOnlyDictionary<string, string> knownDevices)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(knownDevices);
        if (!knownDevices.ContainsKey(key)) throw new ArgumentException("设备已不可用，请重新选择。");
        var alias = Normalize(value);
        foreach (var (otherKey, fallback) in knownDevices)
        {
            ValidateKey(otherKey);
            if (otherKey != key && string.Equals(alias, Resolve(otherKey, fallback), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("这个名称已被另一台电脑使用，请换一个。");
        }
        var copy = new Dictionary<string, string>(values, StringComparer.Ordinal) { [key] = alias };
        if (copy.Count > 128) throw new ArgumentException("保存的设备名称过多。");
        return new(copy);
    }

    public IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string>(values, StringComparer.Ordinal);

    private static string Normalize(string value)
    {
        if (value is null) throw new ArgumentException("请输入电脑名称。");
        var result = value.Trim();
        if (result.Length is < 1 or > 32 || result.EnumerateRunes().Any(r => Rune.GetUnicodeCategory(r) is UnicodeCategory.Control or UnicodeCategory.Format))
            throw new ArgumentException("名称需为 1 至 32 个字符，且不能包含控制字符。");
        return result;
    }

    private static void ValidateKey(string key)
    {
        if (key == "local") return;
        if (key is not { Length: 69 } || !key.StartsWith("peer:", StringComparison.Ordinal) ||
            key.AsSpan(5).ContainsAnyExcept("0123456789ABCDEF"))
            throw new ArgumentException("设备标识无效。");
    }
}
