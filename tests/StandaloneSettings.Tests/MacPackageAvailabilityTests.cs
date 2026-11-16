using NianLink.Windows;

public class MacPackageAvailabilityTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void ButtonRequiresBothArchiveAndManifest(bool archive, bool manifest, bool expected)
    {
        var parent = Path.Combine(Path.GetTempPath(), "nianlink-synthetic-install");
        bool Exists(string path)
        {
            if (Path.GetDirectoryName(path) != Path.Combine(parent, "MacClient")) return false;
            return Path.GetFileName(path) switch
            {
                "NianDeskLink-macOS-arm64.zip" => archive,
                "manifest.json" => manifest,
                _ => false
            };
        }
        Assert.Equal(expected, MacPackageAvailability.Present(parent, Exists));
    }
}
