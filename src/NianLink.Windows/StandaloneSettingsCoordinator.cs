namespace NianLink.Windows;

/// <summary>Synchronizes saved names before a settings-page start reaches Deskflow.</summary>
internal sealed class StandaloneSettingsCoordinator<T>(Func<T, Task> persist, Action<T> acceptSaved,
    Func<Task> reloadCollaboration, Func<Task> start)
{
    public async Task SaveAsync(T value)
    {
        await persist(value);
        // The file has been saved even if a later reload fails. Keep the UI and
        // target-return window aligned with the persisted values so retry is safe.
        acceptSaved(value);
        await reloadCollaboration();
    }

    public async Task StartAsync()
    {
        // A previous reload may have failed after save; never start with stale names.
        await reloadCollaboration();
        await start();
    }

    public async Task SaveIfChangedAsync(T pending, T current)
    {
        if (!EqualityComparer<T>.Default.Equals(pending, current)) await SaveAsync(pending);
    }

    public async Task StartAsync(T pending, T current)
    {
        await SaveIfChangedAsync(pending, current);
        await StartAsync();
    }
}
