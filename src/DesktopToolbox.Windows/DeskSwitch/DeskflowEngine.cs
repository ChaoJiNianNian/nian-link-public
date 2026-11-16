using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed record DeskflowPrepared(string Directory, string WinSettingsPath, string ServerConfigPath, string MacSettingsPath, string Message);

/// <summary>Prepares a verified, private copy of the unmodified Deskflow distribution.</summary>
public sealed class DeskflowEngine
{
    private const string PackageVersion = "1.27.0";
    private const string ManifestName = "package-manifest.json";
    private readonly string _packageDirectory;
    private readonly string _configurationDirectory;
    private readonly string _bridgeExecutable;
    private readonly string _settingsFile;
    private readonly string _engineDirectory;
    private readonly string _engineExecutable;
    private readonly string _windowsSettingsPath;
    private readonly Func<string, bool> _runningProbe;
    private readonly Action<ProcessStartInfo> _start;
    private readonly Func<string, bool> _conflictProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DeskflowEngine(string packagedDirectory, string configDirectory, string bridgeExecutable, string settingsFile)
        : this(packagedDirectory, configDirectory, bridgeExecutable, settingsFile, ProbeRunning, StartProcess, ProbeConflictingGui) { }

    internal DeskflowEngine(string packagedDirectory, string configDirectory, string bridgeExecutable, string settingsFile,
        Func<string, bool> runningProbe, Action<ProcessStartInfo> start, Func<string, bool>? conflictProbe = null)
    {
        _packageDirectory = NormalizePath(packagedDirectory);
        _configurationDirectory = NormalizePath(configDirectory);
        _bridgeExecutable = NormalizePath(bridgeExecutable);
        _settingsFile = NormalizePath(settingsFile);
        if (IsWithin(_packageDirectory, _configurationDirectory) || IsWithin(_configurationDirectory, _packageDirectory))
            throw new ArgumentException("原版引擎目录和私有配置目录不能重叠。");
        _engineDirectory = Path.Combine(_configurationDirectory, "engine");
        _engineExecutable = Path.Combine(_engineDirectory, "deskflow.exe");
        _windowsSettingsPath = Path.Combine(_engineDirectory, "settings", "Deskflow.conf");
        _runningProbe = runningProbe;
        _start = start;
        _conflictProbe = conflictProbe ?? (_ => false);
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                foreach (var relative in new[] { ManifestName, "deskflow.exe", "settings/Deskflow.conf" })
                {
                    var file = ResolveRelative(_packageDirectory, relative);
                    RejectReparseAncestors(file);
                    if (!File.Exists(file)) return false;
                }
                ParseManifest(File.ReadAllText(CheckedManifestPath()));
                return true;
            }
            catch (Exception exception) when (IsStateReadError(exception)) { return false; }
        }
    }

    public bool IsRunning => _runningProbe(_engineExecutable);

    public bool IsPrepared(DeskSwitchSettings settings)
    {
        try
        {
            settings.Validate();
            if (!IsAvailable) return false;
            RejectReparseAncestors(_engineExecutable);
            var portableMarker = _windowsSettingsPath;
            RejectReparseAncestors(portableMarker);
            if (!File.Exists(_engineExecutable) || !File.Exists(portableMarker) || new FileInfo(portableMarker).Length == 0) return false;
            RejectReparseAncestors(Path.Combine(_configurationDirectory, "state"));
            if (!Directory.Exists(Path.Combine(_configurationDirectory, "state"))) return false;
            var stampPath = Path.Combine(_configurationDirectory, "prepared.json");
            RejectReparseAncestors(stampPath);
            if (!File.Exists(stampPath) || new FileInfo(stampPath).Length > 65536) return false;
            var stamp = JsonSerializer.Deserialize<PreparationStamp>(File.ReadAllText(stampPath), JsonOptions);
            if (stamp is null || stamp.Version != PackageVersion || stamp.SettingsSha256 != SettingsHash(settings) || stamp.Files is null ||
                !string.Equals(stamp.ActualWinPath, _windowsSettingsPath, StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (var relative in new[] { "server.conf", "mac.ini" })
            {
                var file = Path.Combine(_configurationDirectory, relative);
                RejectReparseAncestors(file);
                if (!File.Exists(file) || new FileInfo(file).Length > 65536 ||
                    !stamp.Files.TryGetValue(relative, out var expected) || Hash(File.ReadAllBytes(file)) != expected)
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (IsStateReadError(exception)) { return false; }
    }

    public async Task<DeskflowPrepared> PrepareAsync(DeskSwitchSettings settings, string serverAddress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await PrepareCoreAsync(settings, serverAddress, ct); }
        finally { _gate.Release(); }
    }

    public async Task<string> OpenAsync(DeskSwitchSettings settings, string serverAddress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (IsRunning) return "本工具箱的 Deskflow 窗口或共享进程仍在运行。请先正常停止共享并退出，再重新打开或准备配置。";
            EnsureNoOtherGui();
            if (IsPrepared(settings))
                await PrepareRuntimeAsync(ct);
            else
                await PrepareCoreAsync(settings, serverAddress, ct);
            ct.ThrowIfCancellationRequested();
            EnsureNoOtherGui();
            await DisableAutomaticCoreStartAsync(ct);
            RejectReparseAncestors(_engineExecutable);
            var info = new ProcessStartInfo(_engineExecutable)
            {
                UseShellExecute = false,
                WorkingDirectory = _engineDirectory
            };
            // GUI accepts no --settings option; its portable marker selects this private profile.
            info.Environment["XDG_STATE_HOME"] = Path.Combine(_configurationDirectory, "state");
            _start(info);
            return "已打开使用私有配置的原版 Deskflow。请在原版窗口手动启动并核对 Mac 指纹；打开窗口不代表已连接或共享。";
        }
        finally { _gate.Release(); }
    }

    private async Task<DeskflowPrepared> PrepareCoreAsync(DeskSwitchSettings settings, string serverAddress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureNotRunning();
        var generated = DeskflowConfiguration.Generate(settings, _configurationDirectory, _bridgeExecutable, _settingsFile, serverAddress);
        RejectReparseAncestors(_configurationDirectory);
        RejectReparseAncestors(_bridgeExecutable);
        RejectReparseAncestors(_settingsFile);
        await PrepareRuntimeAsync(ct);

        var files = new Dictionary<string, string>
        {
            ["win.ini"] = generated.WindowsSettings,
            ["server.conf"] = generated.ServerConfig,
            ["mac.ini"] = generated.MacSettings
        };
        foreach (var (relative, contents) in files)
            await AtomicWriteAsync(Path.Combine(_configurationDirectory, relative), Encoding.UTF8.GetBytes(contents), overwrite: true, ct);
        await AtomicWriteAsync(_windowsSettingsPath, Encoding.UTF8.GetBytes(generated.WindowsSettings), overwrite: true, ct);
        var stamp = new PreparationStamp(PackageVersion, SettingsHash(settings), _windowsSettingsPath,
            files.Where(file => file.Key != "win.ini").ToDictionary(file => file.Key, file => Hash(Encoding.UTF8.GetBytes(file.Value))));
        await AtomicWriteAsync(Path.Combine(_configurationDirectory, "prepared.json"), JsonSerializer.SerializeToUtf8Bytes(stamp, JsonOptions), overwrite: true, ct);
        return new(_configurationDirectory, _windowsSettingsPath,
            Path.Combine(_configurationDirectory, "server.conf"), Path.Combine(_configurationDirectory, "mac.ini"),
            "已准备独立原版引擎和私有配置。共享尚未启动，Mac 指纹需在原版窗口确认。");
    }

    private async Task PrepareRuntimeAsync(CancellationToken ct)
    {
        EnsureNotRunning();
        RejectReparseAncestors(_configurationDirectory);
        RejectReparseAncestors(Path.Combine(_configurationDirectory, "state"));
        RejectReparseAncestors(Path.Combine(_configurationDirectory, "tls"));
        var portableTlsDirectory = Path.Combine(_engineDirectory, "settings", "tls");
        RejectReparseAncestors(portableTlsDirectory);
        var manifest = await ReadManifestAsync(ct);
        var expectedFiles = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

        // Validate every source before creating or changing the user's private engine.
        foreach (var file in EnumerateSafeFiles(_packageDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (file == ManifestName) continue;
            if (!expectedFiles.ContainsKey(file))
                throw new InvalidDataException($"原版引擎包含未列入校验清单的文件：{file}。");
        }
        foreach (var entry in manifest.Files)
            await VerifyHashAsync(ResolveRelative(_packageDirectory, entry.Path), entry.Sha256, entry.Path, ct);

        if (Directory.Exists(_engineDirectory))
        {
            foreach (var relative in EnumerateSafeFiles(_engineDirectory))
            {
                ct.ThrowIfCancellationRequested();
                if (IsPortableSettings(relative)) continue;
                if (!expectedFiles.TryGetValue(relative, out var entry))
                    throw new InvalidDataException($"私有引擎包含原版清单以外的文件：{relative}。请先退出原版窗口并检查文件。");
                await VerifyHashAsync(ResolveRelative(_engineDirectory, relative), entry.Sha256, relative, ct);
            }
        }

        EnsureNotRunning();
        Directory.CreateDirectory(_engineDirectory);
        foreach (var entry in manifest.Files)
        {
            var destination = ResolveRelative(_engineDirectory, entry.Path);
            RejectReparseAncestors(destination);
            if (File.Exists(destination)) continue; // Preserve existing profile/trust files here; explicit Prepare writes its configuration separately.
            await CopyVerifiedFileAsync(ResolveRelative(_packageDirectory, entry.Path), destination, entry, ct);
        }
        Directory.CreateDirectory(Path.Combine(_engineDirectory, "settings"));
        // Portable trust databases follow settingsPath()/tls independently of the certificate path.
        RejectReparseAncestors(portableTlsDirectory);
        Directory.CreateDirectory(portableTlsDirectory);
        RejectReparseAncestors(portableTlsDirectory);
        Directory.CreateDirectory(Path.Combine(_configurationDirectory, "tls"));
        RejectReparseAncestors(Path.Combine(_configurationDirectory, "tls"));
        EnsureNotRunning();

        Directory.CreateDirectory(Path.Combine(_configurationDirectory, "state"));
        RejectReparseAncestors(Path.Combine(_configurationDirectory, "state"));
    }

    private async Task DisableAutomaticCoreStartAsync(CancellationToken ct)
    {
        EnsureNotRunning();
        RejectReparseAncestors(_windowsSettingsPath);
        if (new FileInfo(_windowsSettingsPath).Length > 1024 * 1024)
            throw new InvalidDataException("私有 Deskflow 配置异常过大，已停止打开。");
        var original = await File.ReadAllTextAsync(_windowsSettingsPath, ct);
        var lines = original.Split('\n').ToList();
        var inGui = false;
        var firstGuiHeader = -1;
        var foundKey = false;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inGui = trimmed == "[gui]";
                if (inGui && firstGuiHeader < 0) firstGuiHeader = index;
                continue;
            }
            var equals = line.IndexOf('=');
            if (!inGui || equals < 0 || line[..equals].Trim() != "startCoreWithGui") continue;
            // Patch only our boolean key. Qt owns all other GUI/profile serialization.
            lines[index] = line[..(equals + 1)] + "false" + (line.EndsWith('\r') ? "\r" : "");
            foundKey = true;
        }
        if (!foundKey)
        {
            var carriage = original.Contains("\r\n", StringComparison.Ordinal) ? "\r" : "";
            if (firstGuiHeader >= 0) lines.Insert(firstGuiHeader + 1, "startCoreWithGui=false" + carriage);
            else
            {
                lines.Add("[gui]" + carriage);
                lines.Add("startCoreWithGui=false" + carriage);
                lines.Add("");
            }
        }
        var patched = string.Join('\n', lines);
        if (patched != original)
            await AtomicWriteAsync(_windowsSettingsPath, Encoding.UTF8.GetBytes(patched), overwrite: true, ct);
    }

    private void EnsureNoOtherGui()
    {
        if (_conflictProbe(_engineExecutable))
            throw new InvalidOperationException("另一份 Deskflow 窗口正在运行。原版 GUI 使用全局单实例，请先从其托盘正常退出，再打开工具箱的私有版本。");
    }

    private async Task<PackageManifest> ReadManifestAsync(CancellationToken ct) =>
        ParseManifest(await File.ReadAllTextAsync(CheckedManifestPath(), ct));

    private string CheckedManifestPath()
    {
        var path = Path.Combine(_packageDirectory, ManifestName);
        RejectReparseAncestors(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("缺少有效的原版引擎校验清单。");
        return path;
    }

    private PackageManifest ParseManifest(string contents)
    {
        PackageManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PackageManifest>(contents, JsonOptions); }
        catch (JsonException exception) { throw new InvalidDataException("原版引擎校验清单格式无效。", exception); }
        if (manifest?.Version != PackageVersion || manifest.Files is null || manifest.Files.Length is < 2 or > 10000)
            throw new InvalidDataException("原版引擎版本或文件清单无效，请使用工具箱随附的 Deskflow 1.27.0。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || file.Path == ManifestName ||
                !seen.Add(file.Path) || file.Sha256 is null || file.Sha256.Length != 64 ||
                file.Sha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
                throw new InvalidDataException("原版引擎校验清单存在无效或重复条目。");
            ResolveRelative(_packageDirectory, file.Path);
        }
        if (!seen.Contains("deskflow.exe") || !seen.Contains("settings/Deskflow.conf"))
            throw new InvalidDataException("原版引擎缺少程序或便携隔离标记 settings/Deskflow.conf。");
        return manifest;
    }

    private async Task CopyVerifiedFileAsync(string source, string destination, ManifestFile entry, CancellationToken ct)
    {
        RejectReparseAncestors(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream();
        await input.CopyToAsync(output, ct);
        var contents = output.ToArray();
        if (Hash(contents) != entry.Sha256) throw new InvalidDataException($"原版引擎文件校验失败：{entry.Path}。");
        await AtomicWriteAsync(destination, contents, overwrite: false, ct);
    }

    private async Task AtomicWriteAsync(string destination, byte[] contents, bool overwrite, CancellationToken ct)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            EnsureNotRunning();
            RejectReparseAncestors(destination);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            EnsureNotRunning();
            RejectReparseAncestors(destination);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            RejectReparseAncestors(temporary);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task VerifyHashAsync(string path, string expected, string relative, CancellationToken ct)
    {
        RejectReparseAncestors(path);
        if (!File.Exists(path)) throw new InvalidDataException($"原版引擎缺少文件：{relative}。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (actual != expected)
            throw new InvalidDataException($"引擎文件校验失败：{relative}。已停止准备，未替换该文件。");
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        RejectReparseAncestors(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("引擎目录包含链接或重解析点，已停止准备。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return Path.GetRelativePath(root, path).Replace('\\', '/');
            }
        }
    }

    private string SettingsHash(DeskSwitchSettings settings) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = PackageVersion, settings, bridgeExecutable = _bridgeExecutable,
        settingsFile = _settingsFile, configurationDirectory = _configurationDirectory
    }));

    private void EnsureNotRunning()
    {
        if (IsRunning) throw new InvalidOperationException("本工具箱的 Deskflow 窗口或共享进程正在运行。请先正常停止共享并退出，再重新准备配置。");
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.IndexOf(':', 2) >= 0 || path.Any(char.IsControl) || path.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0)
            throw new ArgumentException("引擎和配置须使用本机绝对路径，不能包含设备路径、控制字符或通配符。");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (normalized.Length <= 3) throw new ArgumentException("引擎和配置不能使用磁盘根目录。");
        return normalized;
    }

    private static string ResolveRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 512 || relative.Contains('\\') || Path.IsPathRooted(relative) ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedName(part)))
            throw new InvalidDataException("引擎校验清单包含不安全的相对路径。");
        var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(resolved, root) || resolved == root) throw new InvalidDataException("引擎校验清单路径越界。");
        return resolved;
    }

    private static bool IsReservedName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("引擎或配置经过链接/重解析路径，已停止操作。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool IsPortableSettings(string relative) => relative.StartsWith("settings/", StringComparison.OrdinalIgnoreCase);
    private static string Hash(byte[] contents) => Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
    private static bool IsStateReadError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException;

    private static bool ProbeRunning(string executable)
    {
        foreach (var name in new[] { "deskflow", "deskflow-core" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    var actual = ReadEngineProcessPath(() => process.MainModule?.FileName);
                    if (IsPrivateEngineProcessPath(actual, executable)) return true;
                }
            }
        }
        return false;
    }

    internal static bool IsPrivateEngineProcessPath(string actual, string guiExecutable)
    {
        var fullPath = Path.GetFullPath(actual);
        var coreExecutable = Path.Combine(Path.GetDirectoryName(guiExecutable)!, "deskflow-core.exe");
        return fullPath.Equals(guiExecutable, StringComparison.OrdinalIgnoreCase) ||
            fullPath.Equals(coreExecutable, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ReadEngineProcessPath(Func<string?> readPath)
    {
        try
        {
            var actual = readPath();
            if (string.IsNullOrWhiteSpace(actual) || !Path.IsPathFullyQualified(actual))
                throw new InvalidOperationException("未能取得完整的进程路径。");
            return Path.GetFullPath(actual);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("无法确认 Deskflow 窗口或共享进程状态（权限不足或进程已变化）。请先正常退出相关 Deskflow 进程后重试。", exception);
        }
    }

    private static bool ProbeConflictingGui(string executable)
    {
        foreach (var process in Process.GetProcessesByName("deskflow"))
        {
            using (process)
            {
                var actual = ReadEngineProcessPath(() => process.MainModule?.FileName);
                if (!actual.Equals(executable, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
    internal static void ConfirmProcessSurvivedStartup(Process process)
    {
        if (process.WaitForExit(600))
            throw new InvalidOperationException($"原版 Deskflow 窗口立即退出（代码 {process.ExitCode}）。请检查是否有另一份原版窗口正在运行，以及引擎与配置是否完整。");
    }

    private static void StartProcess(ProcessStartInfo info)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException("原版 Deskflow 启动失败，请检查引擎文件。");
        ConfirmProcessSurvivedStartup(process);
    }

    private sealed record PackageManifest(string Version, ManifestFile[] Files);
    private sealed record ManifestFile(string Path, string Sha256);
    private sealed record PreparationStamp(string Version, string SettingsSha256, string ActualWinPath, Dictionary<string, string> Files);
}





