using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopToolbox.Core;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

/// <summary>Names are local presentation settings and never modify credentials.</summary>
public sealed class DeviceAliasStore
{
    private const string FileName = "device-aliases.json";
    private readonly AppPaths paths;
    private readonly JsonFileStore files;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeviceAliases names;

    private DeviceAliasStore(AppPaths paths, JsonFileStore files, DeviceAliases names)
    { this.paths = paths; this.files = files; this.names = names; }

    public DeviceAliases Names => Volatile.Read(ref names);

    public static async Task<DeviceAliasStore> OpenAsync(string profile, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(profile)) throw new ArgumentException("资料目录必须使用完整路径。");
        var paths = new AppPaths(Path.Combine(profile, "display-names"));
        var files = new JsonFileStore(paths);
        var saved = await files.LoadAsync(FileName, () => new Dictionary<string, string>(), token);
        return new(paths, files, DeviceAliases.From(saved));
    }

    public async Task RenameAsync(string key, string name, IReadOnlyDictionary<string, string> known, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            using var directory = paths.AcquireDataDirectory(createIfMissing: true);
            var lockPath = paths.Resolve("names.lock");
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("名称配置锁不能是链接。");
            await using var fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var saved = await files.LoadAsync(FileName, () => new Dictionary<string, string>(), token);
            var next = DeviceAliases.From(saved).Rename(key, name, known);
            await files.SaveAsync(FileName, next.Snapshot(), token);
            Volatile.Write(ref names, next);
        }
        finally { gate.Release(); }
    }
}
