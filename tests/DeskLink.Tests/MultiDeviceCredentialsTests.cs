namespace DeskLink.Tests;
using DesktopToolbox.DeskLink;

public sealed class MultiDeviceCredentialsTests
{
    private static string Fingerprint(int n) => n.ToString("X64");

    [Fact]
    public void CredentialsAreIndependentAndRevocationOnlyAffectsThatDevice()
    {
        using var peers = new MultiDeviceCredentials();
        var first = peers.AddConfirmed("Win-Two", Fingerprint(1));
        var second = peers.AddConfirmed("Mac-Two", Fingerprint(2));
        Assert.NotEqual(first, second);
        Assert.True(peers.IsAuthorized("Win-Two", first));
        Assert.False(peers.IsAuthorized("Mac-Two", first));
        Assert.True(peers.Revoke("Win-Two"));
        Assert.False(peers.IsAuthorized("Win-Two", first));
        Assert.True(peers.IsAuthorized("Mac-Two", second));
    }

    [Fact]
    public void DuplicateDeviceOrIdentityAndFifthPeerAreRejected()
    {
        using var peers = new MultiDeviceCredentials();
        peers.AddConfirmed("one", Fingerprint(1));
        Assert.Throws<ArgumentException>(() => peers.AddConfirmed("ONE", Fingerprint(2)));
        Assert.Throws<ArgumentException>(() => peers.AddConfirmed("two", Fingerprint(1)));
        peers.AddConfirmed("two", Fingerprint(2));
        peers.AddConfirmed("three", Fingerprint(3));
        peers.AddConfirmed("four", Fingerprint(4));
        Assert.Throws<InvalidOperationException>(() => peers.AddConfirmed("five", Fingerprint(5)));
    }

    [Fact]
    public void InvalidOrOldTokensCannotAuthorize()
    {
        using var peers = new MultiDeviceCredentials();
        var old = peers.AddConfirmed("peer", Fingerprint(1));
        Assert.False(peers.IsAuthorized("peer", "not base64"));
        Assert.False(peers.IsAuthorized("peer", Convert.ToBase64String(new byte[31])));
        peers.Revoke("peer");
        var current = peers.AddConfirmed("peer", Fingerprint(1));
        Assert.False(peers.IsAuthorized("peer", old));
        Assert.True(peers.IsAuthorized("peer", current));
    }

    [Fact]
    public void NativeBindingsContainOnlyUniqueValidatedPublicIdentities()
    {
        var json = NativePeerBindings.ToJson([
            new("Win-Two", new string('A', 64)),
            new("Mac-Two", new string('B', 64))]);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("peers").GetArrayLength());
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => NativePeerBindings.ToJson([
            new("Win-Two", new string('A', 64)), new("win-two", new string('B', 64))]));
        Assert.Throws<ArgumentException>(() => NativePeerBindings.ToJson([
            new("Win-Two", new string('A', 64)), new("Mac-Two", new string('A', 64))]));
        Assert.Throws<ArgumentException>(() => NativePeerBindings.ToJson([
            new("Bad\nName", new string('A', 64))]));
    }
}
