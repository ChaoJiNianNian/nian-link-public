using DesktopToolbox.Mac.Models;
using Nian.Input.Security;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacPreviewInvitationPresentationTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-25T00:00:00Z");
    private static DeviceInvitation Invitation(DevicePermission permission=DevicePermission.PreviewControl)=>
        new("192.0.2.101",24951,new string('A',64),Convert.ToBase64String(new byte[32]),Now.AddMinutes(2),2,permission);
    [Fact]
    public void PreviewCodeShowsTheWholeFingerprintWithoutMutatingTheInvitation()
    {
        var expected=Invitation();var view=MacPreviewInvitationPresentation.Read("  "+expected.ToCode()+"\n",Now);
        Assert.Equal(expected,view.Invitation);Assert.Equal(new string('A',64),view.Invitation!.Fingerprint);
    }
    [Theory]
    [InlineData(DevicePermission.Control)]
    [InlineData(DevicePermission.Control|DevicePermission.PreviewControl)]
    public void OtherPermissionsCannotReachTheConfirmButton(DevicePermission permission)=>
        Assert.Null(MacPreviewInvitationPresentation.Read(Invitation(permission).ToCode(),Now).Invitation);
    [Fact]
    public void ExpiryAtTheBoundaryInvalidatesConfirmation()=>
        Assert.Null(MacPreviewInvitationPresentation.Read(Invitation().ToCode(),Now.AddMinutes(2)).Invitation);
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an invitation")]
    public void EmptyOrMalformedInputStaysLocalAndDoesNotBecomeAnApproval(string? code)=>
        Assert.Null(MacPreviewInvitationPresentation.Read(code,Now).Invitation);
}
