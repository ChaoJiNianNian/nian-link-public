namespace DesktopToolbox.Mac.Models;

/// <summary>Allows a separate development profile without touching the normal user session.</summary>
public sealed record MacStartupOptions(string? DataDirectory, bool StartupCheck)
{
    public MacStartupOptions ResolveDiscoveryTestProfile(string applicationDataDirectory, string homeDirectory)
    {
        if (!Path.IsPathFullyQualified(homeDirectory) || DataDirectory is null || !Path.IsPathFullyQualified(DataDirectory))
            throw new ArgumentException("发现测试必须显式指定隔离资料目录。");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDirectory));
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("发现测试资料目录无效。");
        var projects = Path.GetFullPath(Path.Combine(homeDirectory, "Projects"));
        var parentName = Path.GetFileName(parent);
        const string prefix = "nian-link-test-";
        if (!string.Equals(Path.GetDirectoryName(parent), projects, StringComparison.Ordinal) ||
            !parentName.StartsWith(prefix, StringComparison.Ordinal) || !LowerHex(parentName[prefix.Length..], 32))
            throw new ArgumentException("发现测试资料必须放在独立测试目录中。");
        var name = Path.GetFileName(path);
        if (StartupCheck ? !name.StartsWith("startup-check-", StringComparison.Ordinal) || !LowerHex(name[14..], 32)
                         : !string.Equals(name, "profile", StringComparison.Ordinal))
            throw new ArgumentException("发现测试资料目录名称无效。");
        var candidate = new MacStartupOptions(path, true).ResolveDiscoveryCandidateProfile(applicationDataDirectory);
        if (!Directory.Exists(parent)) throw new IOException("发现测试目录不存在。");
        if (StartupCheck) candidate.ValidateStartupCheckIsolation();
        else if (!Directory.Exists(path)) throw new IOException("发现测试资料目录尚未准备。");
        return this with { DataDirectory = candidate.DataDirectory };
    }

    private static bool LowerHex(string value, int length) => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public MacStartupOptions ResolveDiscoveryCandidateProfile(string applicationDataDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDataDirectory)) throw new ArgumentException("无法确认发现候选资料根目录。");
        if (StartupCheck && DataDirectory is null) throw new ArgumentException("启动检查必须显式指定独立资料目录。");
        var root = Path.Combine(applicationDataDirectory, "NianLink");
        var own = Path.GetFullPath(Path.Combine(root, "DiscoveryCandidate"));
        var requested = DataDirectory ?? own;
        if (!StartupCheck && (!Path.IsPathFullyQualified(requested) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested)), own, StringComparison.Ordinal)))
            throw new ArgumentException("发现候选程序必须使用专用资料目录。");
        var forbidden = new[] { Path.Combine(applicationDataDirectory, "NianDesktopToolbox", "DeskLink"),
            Path.Combine(root, "DeskLink"), Path.Combine(root, "MultiDeviceCandidate"),
            Path.Combine(root, "PreviewControlCandidate"), Path.Combine(root, "NativeInputCandidate") };
        var candidate = requested;
        foreach (var directory in forbidden)
        {
            RejectDiscoveryProfileLinks(directory);
            candidate = NativeCandidateProfile.Require(candidate, directory);
        }
        if (StartupCheck)
        {
            RejectDiscoveryProfileLinks(own);
            candidate = NativeCandidateProfile.Require(candidate, own);
        }
        return this with { DataDirectory = candidate };
    }

    private static void RejectDiscoveryProfileLinks(string directory)
    {
        for (string? current = Path.GetFullPath(directory); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("发现候选资料目录不能通过文件链接比较。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public MacStartupOptions ResolveMultiDeviceCandidateProfile(string applicationDataDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDataDirectory)) throw new ArgumentException("无法确认多设备候选资料根目录。");
        if (StartupCheck && DataDirectory is null) throw new ArgumentException("启动检查必须显式指定独立资料目录。");
        var productRoot = Path.Combine(applicationDataDirectory, "NianLink");
        var requested = DataDirectory ?? Path.Combine(productRoot, "MultiDeviceCandidate");
        var candidate = NativeCandidateProfile.Require(requested, Path.Combine(applicationDataDirectory, "NianDesktopToolbox", "DeskLink"));
        candidate = NativeCandidateProfile.Require(candidate, Path.Combine(productRoot, "DeskLink"));
        candidate = NativeCandidateProfile.Require(candidate, Path.Combine(productRoot, "PreviewControlCandidate"));
        candidate = NativeCandidateProfile.Require(candidate, Path.Combine(productRoot, "NativeInputCandidate"));
        return this with { DataDirectory = candidate };
    }

    public MacStartupOptions ResolveNativeCandidateProfile(string applicationDataDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDataDirectory))
            throw new ArgumentException("无法确认候选程序的独立资料根目录。");
        if (StartupCheck && DataDirectory is null)
            throw new ArgumentException("启动检查必须显式指定独立资料目录。");
        var productRoot = Path.Combine(applicationDataDirectory, "NianLink");
        var requested = DataDirectory ?? Path.Combine(productRoot, "NativeInputCandidate");
        var candidate = NativeCandidateProfile.Require(requested, Path.Combine(applicationDataDirectory, "NianDesktopToolbox", "DeskLink"));
        candidate = NativeCandidateProfile.Require(candidate, Path.Combine(productRoot, "DeskLink"));
        return this with { DataDirectory = candidate };
    }

    public MacStartupOptions ResolvePreviewCandidateProfile(string applicationDataDirectory)
    {
        if(!Path.IsPathFullyQualified(applicationDataDirectory))throw new ArgumentException("无法确认预览候选程序的独立资料根目录。");
        if(StartupCheck&&DataDirectory is null)throw new ArgumentException("启动检查必须显式指定独立资料目录。");
        var root=Path.Combine(applicationDataDirectory,"NianLink");
        var candidate=NativeCandidateProfile.Require(DataDirectory??Path.Combine(root,"PreviewControlCandidate"),Path.Combine(applicationDataDirectory,"NianDesktopToolbox","DeskLink"));
        candidate=NativeCandidateProfile.Require(candidate,Path.Combine(root,"DeskLink"));
        return this with{DataDirectory=candidate};
    }

    public void ValidateStartupCheckIsolation()
    {
        if (!StartupCheck) return;
        var directory = DataDirectory ?? throw new ArgumentException("启动检查必须使用独立资料目录。");
        for (var current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("启动检查目录不能经过文件链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        if (File.Exists(directory) || Directory.Exists(directory))
            throw new IOException("启动检查必须使用尚不存在的新目录，不能读取已有配置。");
    }

    public static MacStartupOptions Parse(string[] arguments)
    {
        string? directory = null;
        var check = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--data-dir":
                    if (directory is not null || index + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[index + 1]))
                        throw new ArgumentException("隔离资料目录必须指定一次，并使用绝对路径。");
                    directory = Path.GetFullPath(arguments[++index]);
                    break;
                case "--startup-check":
                    if (check) throw new ArgumentException("启动检查参数不能重复。");
                    check = true;
                    break;
                default:
                    if (!arguments[index].StartsWith("-psn_", StringComparison.Ordinal))
                        throw new ArgumentException("未知的启动参数。");
                    break;
            }
        }
        if (check && directory is null) throw new ArgumentException("启动检查必须使用独立资料目录。");
        return new(directory, check);
    }
}
