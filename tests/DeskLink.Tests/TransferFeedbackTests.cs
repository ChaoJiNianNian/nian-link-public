using DesktopToolbox.DeskLink.Collaboration;

namespace DeskLink.Tests;

public sealed class TransferFeedbackTests
{
    [Fact]
    public void LegacyMissingReceiveFolderErrorIsTranslatedWithoutChangingOtherErrors()
    {
        Assert.Equal("请先在 Windows 互联设置中选择收件文件夹，然后重试。",
            CollaborationClient.LocalizeRemoteError("Choose a Windows receive folder before sending files."));
        Assert.Equal("other failure", CollaborationClient.LocalizeRemoteError("other failure"));
        Assert.Null(CollaborationClient.LocalizeRemoteError(null));
    }
}
