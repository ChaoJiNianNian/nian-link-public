using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.Core;
using DesktopToolbox.Windows.DeskSwitch;

public sealed class MacInstallerExportTests
{
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.work/mac-export-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task MissingPackageClearlyDescribesTheStandaloneTestState()
    {
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => new MacInstallerExport(root, new Prompts()).ExportAsync(CancellationToken.None));
        Assert.Contains("独立测试版", error.Message);
        Assert.DoesNotContain("重新安装完整工具箱", error.Message);
    }

    [Fact]
    public async Task UnlabelledManifestCannotBeExportedAsAnInternalTestPackage()
    {
        var dir = Path.Combine(root, "payload"); Directory.CreateDirectory(dir);
        var payload = new byte[] { 1, 3, 5, 7 }; var archive = Path.Combine(dir, "NianDeskLink-macOS-arm64.zip");
        await File.WriteAllBytesAsync(archive, payload);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
        { file = Path.GetFileName(archive), version = "0.1.2", bytes = payload.Length, sha256 = Convert.ToHexString(SHA256.HashData(payload)) }));
        var prompts = new Prompts { Chosen = Path.Combine(root, "output.zip") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MacInstallerExport(dir, prompts).ExportAsync(CancellationToken.None));
        Assert.Equal(0, prompts.SavePrompts); Assert.False(File.Exists(prompts.Chosen));
    }

    [Fact]
    public async Task LabeledSyntheticTestPackageCanBeSavedWithoutAutomaticallyOpeningExplorer()
    {
        var dir = Path.Combine(root, "payload"); Directory.CreateDirectory(dir);
        var payload = new byte[] { 1, 3, 5, 7 }; var archive = Path.Combine(dir, "NianDeskLink-macOS-arm64.zip");
        await File.WriteAllBytesAsync(archive, payload);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
        { file = Path.GetFileName(archive), version = "0.1.2", bytes = payload.Length, sha256 = Convert.ToHexString(SHA256.HashData(payload)),
          channel = "internal-test", includesDeskflow = true, notarized = false }));
        var prompts = new Prompts { Chosen = Path.Combine(root, "output.zip") };
        var result = JsonSerializer.SerializeToElement(await new MacInstallerExport(dir, prompts).ExportAsync(CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(result.GetProperty("canceled").GetBoolean());
        Assert.Equal(payload, await File.ReadAllBytesAsync(prompts.Chosen));
        Assert.Contains("测试", result.GetProperty("message").GetString()); Assert.Equal(0, prompts.Reveals);
    }

    [Fact]
    public async Task CorruptSyntheticPackageIsRejectedBeforePrompting()
    {
        var dir = Path.Combine(root, "payload"); Directory.CreateDirectory(dir);
        var payload = new byte[] { 1, 3, 5, 7 }; var archive = Path.Combine(dir, "NianDeskLink-macOS-arm64.zip");
        await File.WriteAllBytesAsync(archive, payload);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
        { file = Path.GetFileName(archive), version = "0.1.2", bytes = payload.Length, sha256 = new string('0', 64),
          channel = "internal-test", includesDeskflow = true, notarized = false }));
        var prompts = new Prompts { Chosen = Path.Combine(root, "output.zip") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MacInstallerExport(dir, prompts).ExportAsync(CancellationToken.None));
        Assert.Equal(0, prompts.SavePrompts); Assert.False(File.Exists(prompts.Chosen));
    }

    [Fact]
    public async Task ExistingFileIsNeverOverwritten()
    {
        var dir = Path.Combine(root, "payload"); Directory.CreateDirectory(dir);
        var payload = new byte[] { 1, 3, 5, 7 }; var archive = Path.Combine(dir, "NianDeskLink-macOS-arm64.zip");
        await File.WriteAllBytesAsync(archive, payload);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
        { file = Path.GetFileName(archive), version = "0.1.2", bytes = payload.Length, sha256 = Convert.ToHexString(SHA256.HashData(payload)),
          channel = "internal-test", includesDeskflow = true, notarized = false }));
        var target = Path.Combine(root, "existing.zip"); await File.WriteAllTextAsync(target, "preserve");
        await Assert.ThrowsAsync<IOException>(() => new MacInstallerExport(dir, new Prompts { Chosen = target }).ExportAsync(CancellationToken.None));
        Assert.Equal("preserve", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task MultiDeviceExportRejectsLegacyPackageBeforePrompting()
    {
        var dir = Path.Combine(root, "payload"); Directory.CreateDirectory(dir);
        var payload = new byte[] { 1, 3, 5 }; var archive = Path.Combine(dir, "NianDeskLink-macOS-arm64.zip");
        await File.WriteAllBytesAsync(archive, payload);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
        { file = Path.GetFileName(archive), version = "0.2.0", bytes = payload.Length, sha256 = Convert.ToHexString(SHA256.HashData(payload)),
          channel = "internal-test", includesDeskflow = true, notarized = false }));
        var prompts = new Prompts { Chosen = Path.Combine(root, "output.zip") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MacInstallerExport(dir, prompts, "multi-device-test").ExportAsync(CancellationToken.None));
        Assert.Equal(0, prompts.SavePrompts);
    }

    [Fact]
    public async Task MultiDeviceExportAcceptsOnlyLabeledPackage()
    {
        var dir = Path.Combine(root, "payload"); Directory.CreateDirectory(dir);
        var payload = new byte[] { 1, 3, 5 }; var archive = Path.Combine(dir, "NianDeskLink-macOS-arm64.zip");
        await File.WriteAllBytesAsync(archive, payload);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new
        { file = Path.GetFileName(archive), version = "0.2.0", bytes = payload.Length, sha256 = Convert.ToHexString(SHA256.HashData(payload)),
          channel = "internal-test", includesDeskflow = true, notarized = false, mode = "multi-device-test" }));
        var prompts = new Prompts { Chosen = Path.Combine(root, "output.zip") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new MacInstallerExport(dir, prompts).ExportAsync(CancellationToken.None));
        await new MacInstallerExport(dir, prompts, "multi-device-test").ExportAsync(CancellationToken.None);
        Assert.Equal(payload, await File.ReadAllBytesAsync(prompts.Chosen));
    }

    private sealed class Prompts : IUserPrompts
    {
        public string? Chosen; public int SavePrompts, Reveals;
        public Task<string?> PickSaveFileAsync(string title, string filter, string suggestedName) { SavePrompts++; return Task.FromResult(Chosen); }
        public Task RevealAsync(string path, bool selectFile = false) { Reveals++; return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filter) => Task.FromResult<string?>(null);
    }
}
