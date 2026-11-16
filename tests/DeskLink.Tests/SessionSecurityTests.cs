using DesktopToolbox.DeskLink;
using System.Text;
namespace DeskLink.Tests;

public sealed class SessionSecurityTests
{
    [Fact]
    public async Task ExplicitConfirmationWritesOnlyPresentedFingerprintAndPreservesExistingTrust()
    {
        using var folder = new TestFolder();
        var (client, server) = ControlProtocolTests.Duplex();
        var process = new SyntheticProcess();
        var fingerprint = new string('D',64);
        var first = "v2:sha256:" + new string('e',64);
        var dependencies = new DeskflowSessionDependencies
        {
            HasConflictingCore = _ => false,
            StartProcessAsync = (_,_) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_,_) => Task.FromResult(client)
        };
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path) with {UseNativeControl=false}, dependencies);
        var info = await session.PrepareAsync();
        await File.WriteAllTextAsync(System.IO.Path.Combine(info.TrustDirectory,"trusted-clients"),first+"\n");
        await session.StartAsync();
        await JsonLineProtocol.ReadAsync(server);
        await server.WriteAsync(Encoding.UTF8.GetBytes("versionMismatch=1.27.0+synthetic\n"));
        Assert.Equal("hello=1.27.0+synthetic", await JsonLineProtocol.ReadAsync(server));
        await server.WriteAsync(Encoding.UTF8.GetBytes("hello=1.27.0+synthetic\npeerFingerprint="+fingerprint+"\nconnectionState=Listening\n"));
        await server.FlushAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while ((await session.GetStateAsync()).PendingPeerFingerprint is null) await Task.Delay(5, deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ConfirmPeerAsync(new string('F',64)));
        await session.ConfirmPeerAsync(fingerprint);
        var trust = await File.ReadAllTextAsync(System.IO.Path.Combine(info.TrustDirectory,"trusted-clients"));
        Assert.Contains(first,trust);
        Assert.Contains("v2:sha256:"+fingerprint.ToLowerInvariant(),trust);
        Assert.Null((await session.GetStateAsync()).PendingPeerFingerprint);
        process.Exit();
        await server.DisposeAsync();
    }
    [Fact]
    public async Task StaticProfilePrepareCannotOverwriteAnActiveLease()
    {
        using var folder = new TestFolder();
        var options = ProfileTests.Options(folder.Path);
        var profile = await DeskflowProfile.PrepareAsync(options);
        var before = await File.ReadAllTextAsync(profile.SettingsPath);
        using(var lease = new FileStream(System.IO.Path.Combine(folder.Path,".session.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            await Assert.ThrowsAsync<IOException>(()=>DeskflowProfile.PrepareAsync(options with {ClipboardEnabled=true}));
        Assert.Equal(before,await File.ReadAllTextAsync(profile.SettingsPath));
    }
    [Fact]
    public async Task OtherRunningCoreBlocksStartingWithoutStoppingIt()
    {
        using var folder = new TestFolder();
        var started = false;
        await using var session = new DeskflowSession(ProfileTests.Options(folder.Path),new()
        {
            HasConflictingCore = _=>true,
            StartProcessAsync = (_,_)=>{started=true;throw new InvalidOperationException();}
        });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.StartAsync());
        Assert.False(started);
    }
    private sealed class SyntheticProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id=>5678;public bool HasExited=>completion.Task.IsCompleted;public Task<int> Completion=>completion.Task;
        public void Exit()=>completion.TrySetResult(0);
        public Task StopAsync(CancellationToken ct=default){Exit();return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
