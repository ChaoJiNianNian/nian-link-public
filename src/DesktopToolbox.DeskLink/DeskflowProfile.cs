using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopToolbox.DeskLink;

/// <summary>Writes only an explicit private profile; never modifies the installed Deskflow application.</summary>
public static partial class DeskflowProfile
{
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ComputerNamePattern();
    private static readonly HashSet<string> AdvancedKeys = new(StringComparer.Ordinal)
    {
        "core/preventSleep", "core/interface", "core/display", "core/useHooks", "core/language",
        "server/relativeMouseMoves", "server/switchDelay", "server/switchDoubleTap",
        "server/disableLockToComputer", "server/defaultLockToComputerState", "server/heartbeat", "server/enableHeartbeat",
        "server/enableSwitchDelay", "server/enableSwitchDoubleTap", "server/win32KeepForeground", "server/clipboardSize",
        "client/invertYScroll", "client/invertXScroll", "client/yScrollScale", "client/xScrollScale", "client/dynamicConnectionInterval",
        "client/languageSync", "log/level"
    };
    public static void ValidateName(string name)
    {
        if (name is null || !ComputerNamePattern().IsMatch(name)) throw new ArgumentException("Computer names use 1–64 letters, digits, dot, underscore or hyphen.");
    }
    public static void Validate(DeskflowSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateName(options.LocalName); ValidateName(options.RemoteName);
        if (options.AdditionalRemoteNames is null || options.AdditionalRemoteNames.Count > 3)
            throw new ArgumentException("A server can have at most four remote computers.");
        if (options.Role != DeskLinkRole.Server && options.AdditionalRemoteNames.Count != 0)
            throw new ArgumentException("Additional remote computers are only supported in server profiles.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { options.LocalName };
        foreach (var name in new[] { options.RemoteName }.Concat(options.AdditionalRemoteNames))
        {
            ValidateName(name);
            if (!names.Add(name)) throw new ArgumentException("Computer names must be unique, ignoring case.");
        }
        if (!Path.IsPathFullyQualified(options.EnginePath) || !Path.IsPathFullyQualified(options.ProfileDirectory)) throw new ArgumentException("Engine and private profile paths must be absolute.");
        if (options.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (options.Protocol is not ("barrier" or "synergy")) throw new ArgumentException("Unsupported Deskflow protocol.");
        if (options.Role == DeskLinkRole.Client && (string.IsNullOrWhiteSpace(options.ServerAddress) || options.ServerAddress.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || Uri.CheckHostName(options.ServerAddress) == UriHostNameType.Unknown)) throw new ArgumentException("A valid server address is required.");
        if (options.CertificatePath is not null && !Path.IsPathFullyQualified(options.CertificatePath)) throw new ArgumentException("Certificate path must be absolute.");
        if (options.TrustDirectory is not null && !Path.IsPathFullyQualified(options.TrustDirectory)) throw new ArgumentException("Trust directory must be absolute.");
        foreach (var pair in options.AdvancedSettings ?? new Dictionary<string, string>())
            if (!AdvancedKeys.Contains(pair.Key) || pair.Value.Length > 1024 || pair.Value.Any(char.IsControl)) throw new ArgumentException("Unsupported or unsafe advanced setting: " + pair.Key);
    }
    public static async Task<DeskflowProfileInfo> PrepareAsync(DeskflowSessionOptions options, CancellationToken ct = default)
    {
        Validate(options);
        var root = Path.GetFullPath(options.ProfileDirectory);
        PrivateFiles.EnsureDirectory(root);
        using var profileLock = new FileStream(Path.Combine(root, ".session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var tls = Path.Combine(root, "tls");
        PrivateFiles.EnsureDirectory(tls);
        var certificate = options.CertificatePath ?? Path.Combine(tls, "deskflow.pem");
        await PrivateFiles.EnsureCertificateAsync(certificate, ct);
        using var cert = PrivateFiles.LoadCertificate(certificate);
        var settings = Path.Combine(root, "Deskflow.conf");
        var serverConfig = Path.Combine(root, "server.conf");
        if (options.TrustDirectory is { } existing && !string.Equals(Path.GetFullPath(existing), tls, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            foreach (var name in new[] { "trusted-servers", "trusted-clients" })
            {
                var source = Path.Combine(existing, name);
                var destination = Path.Combine(tls, name);
                if (File.Exists(source) && !File.Exists(destination))
                {
                    if (new FileInfo(source).Length > 1024 * 1024) throw new InvalidDataException("Trusted fingerprint database is unexpectedly large.");
                    await PrivateFiles.WriteAsync(destination, await File.ReadAllTextAsync(source, ct), ct);
                }
            }
        var text = new StringBuilder();
        var values = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        void Set(string section, string key, string value)
        {
            if (!values.TryGetValue(section, out var group)) values[section] = group = new(StringComparer.Ordinal);
            group[key] = value;
        }
        Set("core", "computerName", Quote(options.LocalName));
        Set("core", "coreMode", options.Role == DeskLinkRole.Server ? "2" : "1");
        Set("core", "processMode", "1");
        Set("core", "port", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("core", "enableEnterCommand", "false"); Set("core", "enableExitCommand", "false");
        Set("security", "tlsEnabled", "true"); Set("security", "checkPeerFingerprints", "true"); Set("security", "certificate", Quote(certificate));
        Set("gui", "startCoreWithGui", "false");
        Set("log", "toFile", "true"); Set("log", "file", Quote(Path.Combine(root, "core.log"))); Set("log", "level", "INFO");
        Set("server", "enableClipboard", options.ClipboardEnabled ? "true" : "false");
        Set("server", "protocol", options.Protocol);
        Set("server", "externalConfig", "true"); Set("server", "externalConfigFile", Quote(serverConfig));
        Set("server", "gridWidth", "5"); Set("server", "gridHeight", "3");
        if (options.Role == DeskLinkRole.Client) Set("client", "remoteHost", Quote(options.ServerAddress!));
        foreach (var name in new[] { options.LocalName, options.RemoteName }.Concat(options.AdditionalRemoteNames))
        {
            var group = "computer_" + name;
            Set(group, "name", Quote(name)); Set(group, "aliases", ""); Set(group, "switchCornerSize", "0");
            foreach (var key in new[] { "halfDuplexCapsLock", "halfDuplexNumLock", "halfDuplexScrollLock", "xtestIsXineramaUnaware", "switchCornerTopLeft", "switchCornerTopRight", "switchCornerBottomLeft", "switchCornerBottomRight" }) Set(group, key, "false");
            foreach (var key in new[] { "Shift", "Ctrl", "Alt", "Meta", "Super", "AltGr" }) Set(group, "modifier" + key, key.ToLowerInvariant());
        }
        Set("internalConfig", "screens\\size", "15");
        Set("internalConfig", "screens\\8\\name", Quote(options.LocalName)); Set("internalConfig", "screens\\9\\name", Quote(options.RemoteName)); Set("internalConfig", "hotkeys\\size", "0");
        // Keep the existing pair in place, with no additional global hotkeys.
        var extraSlots = new[] { 7, 10, 3 };
        for (var i = 0; i < options.AdditionalRemoteNames.Count; i++)
            Set("internalConfig", $"screens\\{extraSlots[i]}\\name", Quote(options.AdditionalRemoteNames[i]));
        foreach (var pair in options.AdvancedSettings ?? new Dictionary<string, string>())
        {
            var separator = pair.Key.IndexOf('/');
            Set(pair.Key[..separator], pair.Key[(separator + 1)..], pair.Value);
        }
        foreach (var group in values)
        {
            text.Append('[').Append(group.Key).AppendLine("]");
            foreach (var pair in group.Value) text.Append(pair.Key).Append('=').AppendLine(pair.Value);
        }
        var windowsName = options.Role == DeskLinkRole.Server ? options.LocalName : options.RemoteName;
        var macName = options.Role == DeskLinkRole.Server ? options.RemoteName : options.LocalName;
        await PrivateFiles.WriteAsync(serverConfig, $"section: options\n    keystroke(control+alt+F11) = switchToScreen({windowsName})\n    keystroke(control+alt+F12) = switchToScreen({macName})\nend\n", ct);
        await PrivateFiles.WriteAsync(settings, text.ToString(), ct);
        return new(settings, certificate, DeskLinkFingerprint.OfCertificate(cert), tls);
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
