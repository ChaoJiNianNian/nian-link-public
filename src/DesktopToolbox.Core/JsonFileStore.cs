using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopToolbox.Core;

public sealed class JsonFileStore(AppPaths paths)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.OrdinalIgnoreCase);
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            MaxDepth = 48
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public async Task<T> LoadAsync<T>(string name, Func<T> defaults, CancellationToken cancellationToken = default)
    {
        var file = paths.Resolve(name);
        var gate = gates.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var directoryLease = paths.AcquireDataDirectory();
            if (!directoryLease.Exists || !File.Exists(file)) return defaults();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 8 * 1024 * 1024)
                throw new InvalidDataException($"本地数据文件不符合安全范围：{name}");
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"本地数据为空：{name}");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"本地数据损坏，已保留原文件：{name}", exception);
            }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync<T>(string name, T value, CancellationToken cancellationToken = default)
    {
        var file = paths.Resolve(name);
        var data = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (data.Length > 8 * 1024 * 1024) throw new InvalidDataException("本地数据超出大小限制。");
        var gate = gates.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        DataDirectoryLease? directoryLease = null;
        try
        {
            directoryLease = paths.AcquireDataDirectory(createIfMissing: true);
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("不能覆盖链接形式的本地数据文件。");
            temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, file, true);
            temporary = null;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { directoryLease?.Dispose(); gate.Release(); }
        }
    }
}

