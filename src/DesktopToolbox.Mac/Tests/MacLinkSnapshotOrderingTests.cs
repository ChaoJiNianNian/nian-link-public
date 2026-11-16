using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacLinkSnapshotOrderingTests
{
    [Fact]
    public void ReorderedUiDispatchCannotRestoreOlderConnectedSnapshot()
    {
        var actions = new List<Action>();
        var service = new FakeLink();
        using var view = new MacLinkViewModel(service, action => actions.Add(action));
        service.Emit(Snapshot(1, true));
        service.Emit(Snapshot(2, false));
        actions[1](); actions[0]();
        Assert.False(view.IsInputConnected);
    }

    [Fact]
    public void PreviewAdmissionCannotRestoreAnOlderSnapshot()
    {
        var observer = new MacPreviewAdmissionObserver();
        var older = Snapshot(1, true);
        var newer = Snapshot(2, false);
        observer.Observe(newer, false);
        observer.Observe(older, false);
        Assert.Same(newer, observer.Snapshot);
    }

    [Fact]
    public void LegacyUnversionedSnapshotsStillWork()
    {
        var observer = new MacPreviewAdmissionObserver();
        var first = Snapshot(0, false); var second = Snapshot(0, true);
        observer.Observe(first, false); observer.Observe(second, false);
        Assert.Same(second, observer.Snapshot);
        var actions = new List<Action>(); var service = new FakeLink();
        using var view = new MacLinkViewModel(service, action => actions.Add(action));
        service.Emit(first); service.Emit(second); actions[0](); actions[1]();
        Assert.True(view.IsInputConnected);
    }

    [Fact]
    public void UnversionedSnapshotCannotReplaceVersionedState()
    {
        var observer = new MacPreviewAdmissionObserver();
        var newer = Snapshot(2, false);
        observer.Observe(newer, false); observer.Observe(Snapshot(0, true), false);
        Assert.Same(newer, observer.Snapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SlowPreviewObserverCannotPublishAnOlderSnapshotAfterNewer(long oldVersion)
    {
        var service = new FakeLink(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        await using var wrapper = new MacPreviewAwareLinkService(service, new BlockingPreview(entered, release), _ => Task.FromResult<DeskLinkPairingBundle?>(null));
        var delivered = new List<long>();
        wrapper.Changed += value => { lock (delivered) delivered.Add(value.LocalSnapshotVersion); };
        var older = Task.Run(() => service.Emit(Snapshot(oldVersion, true)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await Task.Run(() => service.Emit(Snapshot(2, false))).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); await older; }
        Assert.Equal(new long[] { 2 }, delivered);
    }

    private static MacLinkSnapshot Snapshot(long version, bool connected)
    {
        var value = new MacLinkSnapshot { IsRunning = true, IsConnected = connected, ControlConnected = true,
            PreviewLocalCoreState = new DeskLinkState { ProcessState = DeskLinkProcessState.Running,
                ConnectionState = connected ? DeskLinkConnectionState.Connected : DeskLinkConnectionState.Disconnected } };
        // Before the fix this property does not exist; the behavioural assertions
        // still exercise real consumers and fail on stale Connected delivery.
        typeof(MacLinkSnapshot).GetProperty("LocalSnapshotVersion")?.SetValue(value, version);
        return value;
    }

    private sealed class FakeLink : IMacLinkService
    {
        public event Action<MacLinkSnapshot>? Changed;
        public void Emit(MacLinkSnapshot value) => Changed?.Invoke(value);
        public Task<MacLinkSnapshot> RefreshAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public Task StartAsync(MacConnectionSettings settings, CancellationToken token = default) => throw new InvalidOperationException();
        public Task StopAsync(CancellationToken token = default) => throw new InvalidOperationException();
        public Task ConfirmPeerAsync(string fingerprint, bool accept, CancellationToken token = default) => throw new InvalidOperationException();
        public Task<PairingPreview> PreviewPairingAsync(string json, CancellationToken token = default) => throw new InvalidOperationException();
        public Task AcceptPairingAsync(string id, CancellationToken token = default) => throw new InvalidOperationException();
        public Task<MacSwitchOutcome> SwitchAsync(string target, CancellationToken token = default) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class BlockingPreview(ManualResetEventSlim entered, ManualResetEventSlim release) : IMacPreviewControlLifecycle
    {
        public void ObserveSnapshot(MacLinkSnapshot snapshot)
        { if (snapshot.IsConnected) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); } }
        public void StopControl(string reason) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
