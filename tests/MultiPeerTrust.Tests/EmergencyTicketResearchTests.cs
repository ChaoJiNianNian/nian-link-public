using System.Security.Cryptography;
using DesktopToolbox.DeskLink;

namespace MultiPeerTrust.Tests;

// Research-only, memory-only state tests. No vault or physical keyboard.
public sealed class EmergencyTicketResearchTests
{
    private static readonly TrustedDeviceIdentity Peer = new("peer", new string('A', 64), "windows", 1);

    [Fact]
    public void RepairedCredentialCannotConsumeOldObservationAndReplayIsDenied()
    {
        var clock = new SyntheticClock();
        using var store = new EmergencyTicketStore(2, TimeSpan.FromSeconds(10), clock);
        var first = RandomNumberGenerator.GetBytes(32);
        var replacement = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.True(store.TryIssue(Peer, first, 17, out var ticket));
            Assert.False(store.TryConsume(Peer, replacement, ticket, out _));
            Assert.True(store.TryConsume(Peer, first, ticket, out var revision));
            Assert.Equal(17, revision);
            Assert.False(store.TryConsume(Peer, first, ticket, out _));
        }
        finally { CryptographicOperations.ZeroMemory(first); CryptographicOperations.ZeroMemory(replacement); }
    }

    [Fact]
    public async Task ConcurrentReplayHasExactlyOneAdmission()
    {
        var clock = new SyntheticClock();
        using var store = new EmergencyTicketStore(2, TimeSpan.FromSeconds(10), clock);
        var token = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.True(store.TryIssue(Peer, token, 7, out var ticket));
            var attempts = Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.TryConsume(Peer, token, ticket, out _))).ToArray();
            Assert.Equal(1, (await Task.WhenAll(attempts)).Count(ok => ok));
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public void ExpiredTicketAndClockRollbackFailClosed()
    {
        var clock = new SyntheticClock();
        using var store = new EmergencyTicketStore(4, TimeSpan.FromSeconds(10), clock);
        var token = RandomNumberGenerator.GetBytes(32);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.True(store.TryIssue(Peer, token, 1, out var expired));
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.False(store.TryConsume(Peer, token, expired, out _));
            Assert.True(store.TryIssue(Peer, token, 2, out var pending));
            clock.Advance(TimeSpan.FromSeconds(-1));
            Assert.False(store.TryConsume(Peer, token, pending, out _));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False(store.TryConsume(Peer, token, pending, out _));
            Assert.False(store.TryIssue(Peer, token, 3, out _));
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public void CapacityNeverEvictsLiveOrConsumedTicketsBeforeExpiry()
    {
        var clock = new SyntheticClock();
        using var store = new EmergencyTicketStore(1, TimeSpan.FromSeconds(10), clock);
        var token = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.True(store.TryIssue(Peer, token, 7, out var ticket));
            Assert.False(store.TryIssue(Peer, token, 8, out _));
            Assert.True(store.TryConsume(Peer, token, ticket, out _));
            Assert.False(store.TryIssue(Peer, token, 8, out _));
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(store.TryIssue(Peer, token, 8, out _));
            Assert.False(store.TryConsume(Peer, token, ticket, out _));
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public void DifferentIdentityOrMalformedTicketCannotConsume()
    {
        var clock = new SyntheticClock();
        using var store = new EmergencyTicketStore(2, TimeSpan.FromSeconds(10), clock);
        var token = RandomNumberGenerator.GetBytes(32);
        try
        {
            Assert.True(store.TryIssue(Peer, token, 1, out var ticket));
            var other = new TrustedDeviceIdentity("peer", new string('B', 64), "windows", 1);
            Assert.False(store.TryConsume(other, token, ticket, out _));
            Assert.False(store.TryConsume(Peer, token, ticket + "x", out _));
            Assert.True(store.TryConsume(Peer, token, ticket, out _));
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    private sealed class SyntheticClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan amount) => ticks += amount.Ticks;
    }
}
