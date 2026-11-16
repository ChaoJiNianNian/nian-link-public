using DesktopToolbox.Windows.DeskSwitch.Collaboration;

namespace DesktopToolbox.DeskLink.Tests;

public sealed class ClipboardTextPolicyTests
{
    [Fact]
    public void QqRichImageWithFileDropIsIgnored()
    {
        Assert.Equal(ClipboardContentKind.None, ClipboardTextPolicy.Classify(false, false, false, true, true, true));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ProtectedClipboardNeverPublishesEvenIfItLooksLikeAnImage(bool excluded, bool historyBlocked, bool cloudBlocked)
    {
        Assert.Equal(ClipboardContentKind.None, ClipboardTextPolicy.Classify(excluded, historyBlocked, cloudBlocked, true, true, true));
    }

    [Fact]
    public void OrdinaryFileCopyDoesNotPublishEvenIfBitmapIsAlsoOffered()
    {
        Assert.Equal(ClipboardContentKind.None, ClipboardTextPolicy.Classify(false, false, false, true, true, true));
    }

    [Fact]
    public void QqFileDropWithoutBitmapDoesNotFallBackToText()
    {
        Assert.Equal(ClipboardContentKind.None, ClipboardTextPolicy.Classify(false, false, false, true, false, true));
    }

    [Fact]
    public void OnlyRegularTextIsSupported()
    {
        Assert.Equal(ClipboardContentKind.Text, ClipboardTextPolicy.Classify(false, false, false, false, false, true));
        Assert.Equal(ClipboardContentKind.None, ClipboardTextPolicy.Classify(false, false, false, false, true, true));
        Assert.Equal(ClipboardContentKind.None, ClipboardTextPolicy.Classify(false, false, false, false, false, false));
    }
}
