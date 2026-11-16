using System.Net;
using System.Text;

namespace DesktopToolbox.Windows.DeskSwitch;

/// <summary>Produces isolated settings for the pinned Deskflow engine without starting it.</summary>
public static class DeskflowConfiguration
{
    public static (string WindowsSettings, string ServerConfig, string MacSettings) Generate(
        DeskSwitchSettings settings, string directory, string bridgeExecutable, string configurationFile, string serverAddress)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        directory = AbsolutePath(directory, nameof(directory));
        bridgeExecutable = AbsolutePath(bridgeExecutable, nameof(bridgeExecutable));
        configurationFile = AbsolutePath(configurationFile, nameof(configurationFile));
        if (!string.Equals(Path.GetFileName(bridgeExecutable), "DisplayBridge.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("切屏桥必须使用工具箱提供的 DisplayBridge.exe。", nameof(bridgeExecutable));
        if (string.IsNullOrEmpty(serverAddress) || serverAddress.Any(char.IsWhiteSpace) || serverAddress.Any(char.IsControl) ||
            (!IPAddress.TryParse(serverAddress, out _) && !DeskSwitchSettings.IsDnsName(serverAddress, 253)))
            throw new ArgumentException("请输入 Windows 电脑的有效局域网 IP 地址或主机名。", nameof(serverAddress));

        var windows = CommonSettings(settings.LocalName, serverMode: true);
        windows.AppendLine("enableEnterCommand=" + Boolean(settings.LinkDisplay));
        windows.AppendLine("enableExitCommand=" + Boolean(settings.LinkDisplay));
        if (settings.LinkDisplay)
        {
            windows.AppendLine("enterCommand=" + IniString(BridgeCommand(bridgeExecutable, configurationFile, "local")));
            windows.AppendLine("exitCommand=" + IniString(BridgeCommand(bridgeExecutable, configurationFile, "remote")));
        }
        AddSecurityAndGui(windows, Path.Combine(directory, "tls", "deskflow.pem"));
        windows.AppendLine("[server]");
        windows.AppendLine("gridWidth=5");
        windows.AppendLine("gridHeight=3");
        windows.AppendLine("enableClipboard=false");
        windows.AppendLine("externalConfig=true");
        windows.AppendLine("externalConfigFile=" + IniString(Path.Combine(directory, "server.conf")));
        AddComputer(windows, settings.LocalName);
        AddComputer(windows, settings.RemoteName);
        AddServerLayout(windows, settings.LocalName, settings.RemoteName);

        var mac = CommonSettings(settings.RemoteName, serverMode: false);
        mac.AppendLine("enableEnterCommand=false");
        mac.AppendLine("enableExitCommand=false");
        AddSecurityAndGui(mac, certificate: null);
        mac.AppendLine("[client]");
        mac.AppendLine("remoteHost=" + IniString(serverAddress));
        AddComputer(mac, settings.LocalName);
        AddComputer(mac, settings.RemoteName);

        var server = $"section: options\n" +
            $"    keystroke(control+alt+F11) = switchToScreen({settings.LocalName})\n" +
            $"    keystroke(control+alt+F12) = switchToScreen({settings.RemoteName})\n" +
            "end\n";
        return (windows.ToString(), server, mac.ToString());
    }

    private static StringBuilder CommonSettings(string computerName, bool serverMode)
    {
        var text = new StringBuilder();
        text.AppendLine("[core]");
        text.AppendLine("computerName=" + IniString(computerName));
        text.AppendLine("coreMode=" + (serverMode ? "2" : "1"));
        text.AppendLine("processMode=1"); // Deskflow Settings::ProcessMode: Service = 0, Desktop = 1.
        return text;
    }

    private static void AddSecurityAndGui(StringBuilder text, string? certificate)
    {
        text.AppendLine("[security]");
        text.AppendLine("tlsEnabled=true");
        text.AppendLine("checkPeerFingerprints=true");
        if (certificate is not null)
            text.AppendLine("certificate=" + IniString(certificate));
        text.AppendLine("[gui]");
        text.AppendLine("startCoreWithGui=false");
    }

    private static void AddComputer(StringBuilder text, string name)
    {
        // Deskflow 1.27 reads these groups before parsing server.conf options.
        // Full modern defaults also prevent its layout migration from reading missing legacy arrays.
        text.AppendLine("[computer_" + name + "]");
        text.AppendLine("name=" + IniString(name));
        text.AppendLine("aliases=");
        text.AppendLine("switchCornerSize=0");
        text.AppendLine("halfDuplexCapsLock=false");
        text.AppendLine("halfDuplexNumLock=false");
        text.AppendLine("halfDuplexScrollLock=false");
        text.AppendLine("xtestIsXineramaUnaware=false");
        text.AppendLine("switchCornerTopLeft=false");
        text.AppendLine("switchCornerTopRight=false");
        text.AppendLine("switchCornerBottomLeft=false");
        text.AppendLine("switchCornerBottomRight=false");
        text.AppendLine("modifierShift=shift");
        text.AppendLine("modifierCtrl=ctrl");
        text.AppendLine("modifierAlt=alt");
        text.AppendLine("modifierMeta=meta");
        text.AppendLine("modifierSuper=super");
        text.AppendLine("modifierAltGr=altgr");
    }

    private static void AddServerLayout(StringBuilder text, string localName, string remoteName)
    {
        // QSettings arrays are one-based on disk. Keep both computers in the GUI layout so
        // saving the server dialog cannot remove the remote computer_* group needed by F12.
        text.AppendLine("[internalConfig]");
        text.AppendLine("screens\\size=15");
        text.AppendLine("screens\\8\\name=" + IniString(localName));
        text.AppendLine("screens\\9\\name=" + IniString(remoteName));
        text.AppendLine("hotkeys\\size=0");
    }

    private static string Boolean(bool value) => value ? "true" : "false";

    private static string BridgeCommand(string executable, string configuration, string target) =>
        $"\"{executable}\" --config \"{configuration}\" --target {target}";

    private static string IniString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string AbsolutePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) ||
            value.Any(char.IsControl) || value.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0)
            throw new ArgumentException("配置和切屏桥路径必须是绝对路径，且不能包含控制字符、引号或通配符。", parameterName);
        return Path.GetFullPath(value);
    }
}

