using System.Text.Json;

namespace DesktopToolbox.Core;

public interface IToolModule : IAsyncDisposable
{
    IReadOnlyCollection<string> Methods { get; }
    Task<object?> InvokeAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default);
}

public interface IUserPrompts
{
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);
    Task<string?> PickFileAsync(string title, string filter);
    /// <summary>Ask the user where to save a new file; the chosen name must not already exist.</summary>
    Task<string?> PickSaveFileAsync(string title, string filter, string suggestedName) =>
        throw new NotSupportedException("当前界面不支持保存文件对话框。");
    Task RevealAsync(string path, bool selectFile = false);
}

public sealed record ModuleContext(
    AppPaths Paths,
    JsonFileStore Store,
    IUserPrompts Prompts,
    Action<string, object?> Emit);

public sealed record AppPaths(string DataDirectory)
{
    public static AppPaths Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopToolbox"));
    public string Resolve(string name)
    {
        DataDirectoryLease.ValidateName(name);
        return Path.Combine(DataDirectoryLease.Normalize(DataDirectory), name);
    }
    public DataDirectoryLease AcquireDataDirectory(bool createIfMissing = false) => DataDirectoryLease.Acquire(DataDirectory, createIfMissing);
}
