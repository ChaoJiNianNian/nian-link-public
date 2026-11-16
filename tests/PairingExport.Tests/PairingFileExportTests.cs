using DesktopToolbox.Core;
using DesktopToolbox.Windows.DeskSwitch;

public sealed class PairingFileExportTests
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.work/pairing-export-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task CancelDoesNotGenerateOrWriteAFile()
    {
        var prompts = new Prompts(); var calls = 0;
        var exporter = new PairingFileExport(prompts, _ => { calls++; throw new InvalidOperationException(); });
        var result = await exporter.ExportAsync(CancellationToken.None);
        Assert.True(result.Canceled); Assert.Null(result.Path); Assert.Equal(0, calls); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task SavesSyntheticPairingAtChosenPathWithoutAutomaticallyOpeningExplorer()
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "private-synthetic.json"); var target = Path.Combine(root, "发送给Mac.json");
        await File.WriteAllTextAsync(source, "synthetic pairing only");
        var prompts = new Prompts { Chosen = target }; var exporter = new PairingFileExport(prompts, _ => Task.FromResult(source));
        var result = await exporter.ExportAsync(CancellationToken.None);
        Assert.False(result.Canceled); Assert.Equal(target, result.Path); Assert.Equal("synthetic pairing only", await File.ReadAllTextAsync(target));
        Assert.Equal(0, prompts.Reveals); Assert.Contains(".json", prompts.SuggestedName);
    }

    [Fact]
    public async Task ExistingTargetIsNeverOverwrittenAndGeneratorIsNotCalled()
    {
        Directory.CreateDirectory(root); var target = Path.Combine(root, "existing.json"); await File.WriteAllTextAsync(target, "keep me");
        var calls = 0; var exporter = new PairingFileExport(new Prompts { Chosen = target }, _ => { calls++; throw new InvalidOperationException(); });
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(CancellationToken.None));
        Assert.Equal("keep me", await File.ReadAllTextAsync(target)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GeneratorFailureLeavesNoExternalFile()
    {
        Directory.CreateDirectory(root); var target = Path.Combine(root, "destination.json");
        var exporter = new PairingFileExport(new Prompts { Chosen = target }, _ => Task.FromException<string>(new InvalidOperationException("not running")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(CancellationToken.None)); Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task RevealFailureReportsFailureWithoutChangingSavedFile()
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "private.json"); var target = Path.Combine(root, "sent.json");
        await File.WriteAllTextAsync(source, "synthetic pairing only");
        var prompts = new Prompts { Chosen = target, FailReveal = true }; var exporter = new PairingFileExport(prompts, _ => Task.FromResult(source));
        await exporter.ExportAsync(CancellationToken.None); var result = await exporter.RevealAsync();
        Assert.False(result.Opened); Assert.Equal("synthetic pairing only", await File.ReadAllTextAsync(target)); Assert.Equal(1, prompts.Reveals);
    }

    [Fact]
    public async Task WrongExtensionRejectedWithoutGenerating()
    {
        Directory.CreateDirectory(root); var calls = 0; var exporter = new PairingFileExport(new Prompts { Chosen = Path.Combine(root, "bad.zip") }, _ => { calls++; throw new InvalidOperationException(); });
        await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(CancellationToken.None)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FileCreatedByAnotherProcessDuringGenerationIsNeverOverwritten()
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "private.json"); var target = Path.Combine(root, "race.json");
        await File.WriteAllTextAsync(source, "synthetic pairing only");
        var exporter = new PairingFileExport(new Prompts { Chosen = target }, async _ =>
        {
            await File.WriteAllTextAsync(target, "other process"); return source;
        });
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(CancellationToken.None));
        Assert.Equal("other process", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(root, ".nian-pairing-*.tmp"));
    }

    [Fact]
    public async Task CancellationDuringGenerationCreatesNoExternalFile()
    {
        Directory.CreateDirectory(root); var source = Path.Combine(root, "private.json"); var target = Path.Combine(root, "canceled.json");
        await File.WriteAllTextAsync(source, "synthetic pairing only"); using var canceled = new CancellationTokenSource();
        var exporter = new PairingFileExport(new Prompts { Chosen = target }, _ => { canceled.Cancel(); return Task.FromResult(source); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(canceled.Token));
        Assert.False(File.Exists(target)); Assert.Empty(Directory.GetFiles(root, ".nian-pairing-*.tmp"));
    }

    private sealed class Prompts : IUserPrompts
    {
        public string? Chosen, SuggestedName; public bool FailReveal; public int Reveals;
        public Task<string?> PickSaveFileAsync(string title, string filter, string suggestedName) { SuggestedName = suggestedName; return Task.FromResult(Chosen); }
        public Task RevealAsync(string path, bool selectFile = false) { Reveals++; return FailReveal ? Task.FromException(new IOException("synthetic")) : Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter) => Task.FromResult<string?>(null);
    }
}
