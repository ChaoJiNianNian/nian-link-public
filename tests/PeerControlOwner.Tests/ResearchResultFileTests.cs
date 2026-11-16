using System.Diagnostics;

namespace PeerControlOwnerTests;

public sealed class ResearchResultFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "nian-result-test-" + Guid.NewGuid().ToString("N"));
    private readonly string work;

    public ResearchResultFileTests()
    {
        work = Path.Combine(directory, "tests", ".work");
        Directory.CreateDirectory(work);
    }

    [Fact]
    public void NewResultInsideWorkIsCreatedAndWritten()
    {
        var path = Path.Combine(work, "result.txt");
        using (var result = ResearchResultFile.CreateWithin(work, path)) result.Write("synthetic");
        Assert.Equal("synthetic", File.ReadAllText(path));
    }

    [Fact]
    public void ExistingResultIsNeverOverwritten()
    {
        var path = Path.Combine(work, "result.txt");
        File.WriteAllText(path, "do not change");
        Assert.ThrowsAny<IOException>(() => ResearchResultFile.CreateWithin(work, path));
        Assert.Equal("do not change", File.ReadAllText(path));
    }

    [Fact]
    public void RootSiblingOutsideAndRelativeResultAreRejected()
    {
        var sibling = work + "-sibling";
        Directory.CreateDirectory(sibling);
        Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(work, Path.Combine(sibling, "result.txt")));
        Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(work, Path.Combine(work, "..", "outside.txt")));
        Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(work, "relative.txt"));
        Assert.False(File.Exists(Path.Combine(sibling, "result.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "tests", "outside.txt")));
    }

    [Fact]
    public void MissingParentAndExistingDirectoryAreRejected()
    {
        Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(work, Path.Combine(work, "missing", "result.txt")));
        Assert.False(Directory.Exists(Path.Combine(work, "missing")));
        Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(work, work));
    }

    [Fact]
    public void AlternateDataStreamPathIsRejected()
    {
        Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(work, Path.Combine(work, "result.txt:stream")));
        Assert.False(File.Exists(Path.Combine(work, "result.txt")));
    }

    [Fact]
    public void LinkedParentCannotRedirectResultOutsideWork()
    {
        var outside = Path.Combine(directory, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(work, "linked");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception error) when (OperatingSystem.IsWindows() && (error is UnauthorizedAccessException || error is IOException))
            {
                var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, outside }) info.ArgumentList.Add(arg);
                using var proc = Process.Start(info)!; proc.WaitForExit();
                Assert.Equal(0, proc.ExitCode);
            }
            Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(work, Path.Combine(link, "result.txt")));
            Assert.ThrowsAny<Exception>(() => ResearchResultFile.CreateWithin(link, Path.Combine(link, "result.txt")));
            Assert.False(File.Exists(Path.Combine(outside, "result.txt")));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
