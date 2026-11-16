using NianLink.MacDiscoveryBoundary;

namespace MacDiscoveryBoundary.Tests;

public sealed class MacSystemEvidenceTests
{
    [Fact]
    public void RealEntryRejectsNonMacBeforeCallingNativeCode()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.Throws<PlatformNotSupportedException>(() => MacSystemEvidence.Read());
    }

    [Fact]
    public void SuccessfulReadReleasesOwnedArrayAndReturnsOnlyNames()
    {
        var fake = new NativeFake { Names = ["en0", "en5"] };
        Assert.Equal(new[] { "en0", "en5" }, MacSystemEvidence.ReadForTest(fake));
        Assert.Equal(1, fake.Releases);
    }

    [Theory]
    [InlineData("copy")][InlineData("array-type")][InlineData("negative-count")][InlineData("oversize-count")]
    [InlineData("item")][InlineData("wrong-type")][InlineData("name")][InlineData("invalid-name")]
    [InlineData("duplicate")][InlineData("release")]
    public void MalformedOrFailingNativeCallReturnsNoEvidenceAndReleasesWhenOwned(string failure)
    {
        var fake = new NativeFake { Failure = failure, Names = failure == "duplicate" ? ["en0", "en0"] : ["en0"] };
        Assert.ThrowsAny<Exception>(() => MacSystemEvidence.ReadForTest(fake));
        Assert.Equal(failure == "copy" ? 0 : 1, fake.Releases);
    }

    private sealed class NativeFake : IMacSystemConfiguration
    {
        public string Failure = "";
        public string[] Names = [];
        public int Releases;
        public nint CopyInterfaces() => Failure == "copy" ? 0 : 1;
        public bool IsArray(nint value) => Failure != "array-type";
        public long Count(nint value) => Failure == "negative-count" ? -1 : Failure == "oversize-count" ? 1025 : Names.Length;
        public nint Item(nint value, int index) => Failure == "item" ? 0 : index + 2;
        public bool IsInterface(nint item) => Failure != "wrong-type";
        public string Name(nint item) => Failure == "name" ? throw new InvalidDataException() : Failure == "invalid-name" ? "en0\0oops" : Names[(int)item - 2];
        public void Release(nint value) { Releases++; if (Failure == "release") throw new IOException(); }
    }
}
