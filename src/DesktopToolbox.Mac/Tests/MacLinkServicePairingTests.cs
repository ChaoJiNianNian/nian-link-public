using System.Net;
using System.Net.Sockets;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacLinkServicePairingTests
{
    [Fact]
    public async Task ApprovedPairingReadDoesNotWaitForAnUnrelatedStalledControlRefresh()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var setup = await Setup.CreateAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        var approved = await setup.Service.GetApprovedPairingAsync();
        Assert.NotNull(approved);

        using var refreshCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refresh = setup.Service.RefreshAsync(refreshCancellation.Token);
        using var accepted = await listener.AcceptTcpClientAsync(refreshCancellation.Token);
        try
        {
            // TCP is connected but the peer never completes TLS: Refresh owns its
            // lifecycle gate. An already committed data pairing must remain readable.
            Assert.False(refresh.IsCompleted);
            using var readCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var readable = await setup.Service.GetApprovedPairingAsync(readCancellation.Token);
            Assert.Same(approved, readable);
            Assert.False(refresh.IsCompleted);
        }
        finally
        {
            refreshCancellation.Cancel();
            try { await refresh; }
            catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyLocallyApprovedSavedPairingIsReturned(bool approved)
    {
        await using var setup = await Setup.CreateAsync(24801, approved);
        var actual = await setup.Service.GetApprovedPairingAsync();
        if (approved) Assert.Equal(setup.Bundle, actual);
        else Assert.Null(actual);
    }

    [Fact]
    public async Task ImportedPairingRemainsInvisibleUntilItsApprovalHasCommitted()
    {
        await using var setup = await Setup.CreateAsync(24801);
        var previous = await setup.Service.GetApprovedPairingAsync();
        var replacement = setup.Bundle with { WindowsName = "Other-Windows", Port = 24811 };
        var pending = await setup.Service.PreviewPairingAsync(replacement.ToJson());
        Assert.Same(previous, await setup.Service.GetApprovedPairingAsync());

        await setup.Service.AcceptPairingAsync(pending.Id);
        Assert.Equal(replacement, await setup.Service.GetApprovedPairingAsync());
        Assert.NotSame(previous, await setup.Service.GetApprovedPairingAsync());
    }

    [Fact]
    public async Task FailedPairingCommitDoesNotReplaceTheReadableApprovedPairing()
    {
        await using var setup = await Setup.CreateAsync(24801);
        var previous = await setup.Service.GetApprovedPairingAsync();
        var pending = await setup.Service.PreviewPairingAsync((setup.Bundle with { WindowsName = "Other-Windows" }).ToJson());
        Directory.CreateDirectory(Path.Combine(setup.Directory, "connection.json"));

        var failure = await Record.ExceptionAsync(() => setup.Service.AcceptPairingAsync(pending.Id));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Same(previous, await setup.Service.GetApprovedPairingAsync());
    }

    [Fact]
    public async Task CancelledReadsAndReadsAfterDisposalDoNotReturnSavedPairing()
    {
        await using var setup = await Setup.CreateAsync(24801);
        Assert.NotNull(await setup.Service.GetApprovedPairingAsync());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.GetApprovedPairingAsync(cancellation.Token));

        await setup.Service.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => setup.Service.GetApprovedPairingAsync());
    }

    [Fact]
    public async Task DisposalRevokesPairingReadsBeforeWaitingForAStalledRefreshToFinish()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var setup = await Setup.CreateAsync(((IPEndPoint)listener.LocalEndpoint).Port);
        Assert.NotNull(await setup.Service.GetApprovedPairingAsync());
        using var refreshCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refresh = setup.Service.RefreshAsync(refreshCancellation.Token);
        using var accepted = await listener.AcceptTcpClientAsync(refreshCancellation.Token);
        var disposing = setup.Service.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposing.IsCompleted);
            using var readCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => setup.Service.GetApprovedPairingAsync(readCancellation.Token));
        }
        finally
        {
            refreshCancellation.Cancel();
            try { await refresh; }
            catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested) { }
            await disposing;
        }
    }

    private sealed class Setup : IAsyncDisposable
    {
        // These are synthetic fixtures, never copies of installed pairing or keys.
        // Keep them outside compiler output and do not delete profile directories.
        public string Directory { get; }
        public DeskLinkPairingBundle Bundle { get; }
        public MacLinkService Service { get; }
        private Setup(string directory, DeskLinkPairingBundle bundle)
        { Directory = directory; Bundle = bundle; Service = new MacLinkService(directory); }

        public static async Task<Setup> CreateAsync(int port, bool approved = true)
        {
            var bundle = new DeskLinkPairingBundle
            {
                Host = "127.0.0.1", Port = port, CertificateSha256 = new string('A', 64),
                Token = Convert.ToBase64String(new byte[32]), WindowsName = "Win-PC", MacName = "My-Mac"
            };
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../../..", "test-profiles", "preview-control", "mac-pairing-snapshot-" + Guid.NewGuid().ToString("N")));
            var store = new MacPrivateStore(directory);
            await store.WriteAsync("paired-control.json", new { UserApproved = approved, BundleJson = bundle.ToJson() }, CancellationToken.None);
            return new Setup(directory, bundle);
        }

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }
}
