using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class MultiPeerControlTests
{
    [Fact]
    public void PrototypeRejectsNonLoopbackBinding()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        Assert.Throws<ArgumentException>(() => new MultiPeerControlServer(folder.Path, IPAddress.Any, 0, "Win-PC", credentials,
            _ => ReadyState("Win-PC"), (target, expected, revision, _) => Task.FromResult(new DeskLinkSwitchResult())));
    }

    [Fact]
    public async Task HostIdentityLeasePreventsConcurrentCertificateCreation()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var lockFile = Path.Combine(folder.Path, ".identity.lock");
        using var held = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using var server = CreateServer(folder.Path, credentials, new List<string>());
        await Assert.ThrowsAsync<IOException>(() => server.StartAsync());
        Assert.Equal(0, server.Port);
    }
    [Fact]
    public async Task ConfirmedPeersCanOnlyRequestTheirOwnOrHostTargetAndRevocationIsIndividual()
    {
        using var folder = new TestFolder();
        var switched = new List<string>();
        using var credentials = new MultiDeviceCredentials();
        var first = credentials.AddConfirmed("Win-Two", new string('1', 64));
        var second = credentials.AddConfirmed("Mac-Two", new string('2', 64));
        await using var server = CreateServer(folder.Path, credentials, switched);
        await server.StartAsync();

        Assert.True(await Switch(server, "Win-Two", first, "Win-Two"));
        Assert.True(await Switch(server, "Win-Two", first, "Win-PC"));
        Assert.False(await Switch(server, "Win-Two", first, "Mac-Two"));
        Assert.False(await Switch(server, "Mac-Two", first, "Mac-Two"));
        Assert.True(await Switch(server, "Mac-Two", second, "Mac-Two"));
        await server.RevokeAsync("Win-Two");
        Assert.False(await Switch(server, "Win-Two", first, "Win-Two"));
        Assert.True(await Switch(server, "Mac-Two", second, "Mac-Two"));
        Assert.Equal(new[] { "Win-Two", "Win-PC", "Mac-Two", "Mac-Two" }, switched);
    }

    [Fact]
    public async Task CertificateMismatchAndUnknownActionsCannotInvokeKeyboard()
    {
        using var folder = new TestFolder();
        var switched = new List<string>();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        await using var server = CreateServer(folder.Path, credentials, switched);
        await server.StartAsync();
        await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(
            () => Switch(server, "Win-Two", token, "Win-Two", expectedFingerprint: new string('0', 64)));
        Assert.False(await Switch(server, "Win-Two", token, "Win-Two", action: "execute"));
        Assert.Empty(switched);
    }

    [Fact]
    public async Task ClientRequiresApprovalPinsCertificateAndCanReturnToHost()
    {
        using var folder = new TestFolder();
        var switched = new List<string>();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        await using var server = CreateServer(folder.Path, credentials, switched);
        await server.StartAsync();
        var invitation = new MultiPeerInvitation
        {
            Host = "127.0.0.1", Port = server.Port, HostName = "Win-PC", PeerName = "Win-Two",
            CertificateSha256 = server.CertificateSha256!, Token = token
        };
        Assert.DoesNotContain(token, invitation.ToString());
        Assert.Throws<InvalidOperationException>(() => new MultiPeerControlClient(invitation, false));
        var readBack = MultiPeerInvitation.Parse(invitation.ToJson());
        await using var client = new MultiPeerControlClient(readBack, true);
        Assert.True(await client.SwitchAsync("Win-Two"));
        Assert.True(await client.SwitchAsync("Win-PC"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SwitchAsync("Other-PC"));
        await server.RevokeAsync("Win-Two");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SwitchAsync("Win-Two"));
        Assert.Equal(new[] { "Win-Two", "Win-PC" }, switched);
    }

    [Fact]
    public async Task RevocationWaitsForAnInProgressSwitchAndRejectsLaterRequests()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new MultiPeerControlServer(folder.Path, IPAddress.Loopback, 0, "Win-PC", credentials, _ => ReadyState("Win-PC", "Win-Two"),
            async (target, expected, revision, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new() { Ok = true, InputTransferred = true, ActiveComputer = target }; });
        await server.StartAsync();
        var switching = Switch(server, "Win-Two", token, "Win-Two");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var revoke = server.RevokeAsync("Win-Two");
        Assert.False(revoke.IsCompleted);
        release.TrySetResult();
        Assert.True(await switching.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await revoke.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await Switch(server, "Win-Two", token, "Win-Two"));
    }

    [Fact]
    public async Task FourPeersCanConnectConcurrentlyAndAResultWithoutConfirmedFocusIsFailure()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var names = new[] { "Win-One", "Win-Two", "Mac-One", "Mac-Two" };
        var tokens = names.Select((name, index) => credentials.AddConfirmed(name, (index + 1).ToString("X64"))).ToArray();
        await using var server = new MultiPeerControlServer(folder.Path, IPAddress.Loopback, 0, "Win-PC", credentials, _ => ReadyState("Win-PC", names),
            (target, expected, revision, _) => Task.FromResult(new DeskLinkSwitchResult
            {
                Ok = true, InputTransferred = target != "Mac-Two", ActiveComputer = target
            }));
        await server.StartAsync();
        var results = await Task.WhenAll(names.Select((name, i) => Switch(server, name, tokens[i], name)));
        Assert.Equal(new[] { true, true, true, false }, results);
    }

    [Fact]
    public async Task AnotherDeviceCannotInterruptTheCurrentController()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        var calls = 0;
        await using var server = new MultiPeerControlServer(folder.Path, IPAddress.Loopback, 0, "Win-PC", credentials,
            _ => ReadyState("Mac-Two", "Win-Two", "Mac-Two"),
            (target, expected, revision, _) => { calls++; return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, ActiveComputer = target }); });
        await server.StartAsync();
        Assert.False(await Switch(server, "Win-Two", token, "Win-PC"));
        Assert.False(await Switch(server, "Win-Two", token, "Win-Two"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SwitchPassesObservedOwnerAndRevisionToAtomicKeyboardCallback()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        string? owner = null;
        long revision = -1;
        await using var server = new MultiPeerControlServer(folder.Path, IPAddress.Loopback, 0, "Win-PC", credentials,
            _ => Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true,
                ActiveComputer = "Win-PC", ConnectedComputers = ["Win-Two"], Revision = 17 }),
            (target, expected, version, _) =>
            {
                owner = expected; revision = version;
                return Task.FromResult(new DeskLinkSwitchResult { Ok = false, Error = "focus_changed" });
            });
        await server.StartAsync();
        Assert.False(await Switch(server, "Win-Two", token, "Win-Two"));
        Assert.Equal("Win-PC", owner);
        Assert.Equal(17, revision);
    }

    [Fact]
    public async Task NetworkSwitchUsesConditionalNativeRequestAndRefusesStaleFocus()
    {
        using var folder = new TestFolder();
        using var credentials = new MultiDeviceCredentials();
        var token = credentials.AddConfirmed("Win-Two", new string('1', 64));
        var (nativeStream, coreStream) = ControlProtocolTests.Duplex();
        await using var native = new NativeControlClient(nativeStream);
        var adapter = new MultiPeerNativeSwitch(native);
        await using var server = new MultiPeerControlServer(folder.Path, IPAddress.Loopback, 0, "Win-PC", credentials,
            _ => Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true,
                ActiveComputer = "Win-PC", ConnectedComputers = ["Win-Two"], Revision = 17 }), adapter.SwitchAsync);
        await server.StartAsync();
        var switching = Switch(server, "Win-Two", token, "Win-Two");
        using var request = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(coreStream))!);
        Assert.Equal("switch-if-current", request.RootElement.GetProperty("action").GetString());
        Assert.Equal(17, request.RootElement.GetProperty("expectedRevision").GetInt64());
        var id = request.RootElement.GetProperty("requestId").GetString();
        await JsonLineProtocol.WriteAsync(coreStream, new { version = 1, requestId = id, ok = false,
            activeComputer = "Mac-Two", connectedComputers = new[] { "Win-Two", "Mac-Two" }, revision = 18, error = "focus_changed" });
        Assert.False(await switching.WaitAsync(TimeSpan.FromSeconds(5)));
        await coreStream.DisposeAsync();
    }

    private static MultiPeerControlServer CreateServer(string path, MultiDeviceCredentials credentials, List<string> switched)
    {
        var active = "Win-PC";
        return new(path, IPAddress.Loopback, 0, "Win-PC", credentials, _ => ReadyState(active, "Win-Two", "Mac-Two"),
            (target, expected, revision, _) => { active = target; switched.Add(target); return Task.FromResult(new DeskLinkSwitchResult { Ok = true, Target = target, ActiveComputer = target, InputTransferred = true }); });
    }

    internal static Task<DeskLinkState> ReadyState(string active, params string[] peers) =>
        Task.FromResult(new DeskLinkState { ProcessState = DeskLinkProcessState.Running, NativeControlAvailable = true,
            ActiveComputer = active, ConnectedComputers = peers });

    private static async Task<bool> Switch(MultiPeerControlServer server, string peer, string token, string target,
        string? expectedFingerprint = null, string action = "switch")
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
            cert is not null && DeskLinkFingerprint.Matches(expectedFingerprint ?? server.CertificateSha256!,
                Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))));
        await tls.AuthenticateAsClientAsync("nian-desklink");
        var id = Guid.NewGuid().ToString("N");
        await JsonLineProtocol.WriteAsync(tls, new { version = 1, requestId = id, peer, token, action, target });
        using var response = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(tls))!);
        Assert.Equal(id, response.RootElement.GetProperty("requestId").GetString());
        return response.RootElement.GetProperty("ok").GetBoolean();
    }
}
