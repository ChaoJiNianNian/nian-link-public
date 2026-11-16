using System.Diagnostics;

public sealed class SyntheticWorkDirectoryTests
{
    [Fact]
    public void CreatesUniqueDirectoriesWithinChosenRoot()
    {
        var work = Path.Combine(AppContext.BaseDirectory, "synthetic-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var one = SyntheticWorkDirectory.CreateUnder(work, "fixture-");
            using var two = SyntheticWorkDirectory.CreateUnder(work, "fixture-");
            Assert.NotEqual(one.Path, two.Path);
            Assert.Equal(work, Path.GetDirectoryName(one.Path));
            Assert.True(Directory.Exists(one.Path));
        }
        finally { Directory.Delete(work, true); }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../")]
    [InlineData("/absolute")]
    public void RefusesDirectoryEscapePrefix(string prefix)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "synthetic-prefix-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => SyntheticWorkDirectory.CreateUnder(root, prefix));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void RefusesLinkedRootBeforeWritingToItsTarget()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "synthetic-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            CreateLink(link, target);
            Assert.Throws<IOException>(() => SyntheticWorkDirectory.CreateUnder(link, "fixture-"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(target);
            Directory.Delete(root);
        }
    }
    [Fact]
    public void CleanupRefusesLinkedSubdirectoryAndKeepsTarget()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "synthetic-cleanup-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "protected");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "marker.txt");
        File.WriteAllText(marker, "keep");
        var fixture = SyntheticWorkDirectory.CreateUnder(root, "fixture-");
        var link = Path.Combine(fixture.Path, "linked-target");
        try
        {
            CreateLink(link, target);
            Assert.Throws<IOException>(() => fixture.Dispose());
            Assert.Equal("keep", File.ReadAllText(marker));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            fixture.Dispose();
            File.Delete(marker);
            Directory.Delete(target);
            Directory.Delete(root);
        }
    }

    private static void CreateLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
