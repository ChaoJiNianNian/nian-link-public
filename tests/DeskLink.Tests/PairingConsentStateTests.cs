using DesktopToolbox.DeskLink;

namespace DeskLink.Tests;

public sealed class PairingConsentStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static PairingSessionIdentity Identity() => new(
        Guid.Parse("97efca77-7c7d-4689-b33b-49c1d9640604"),
        Guid.Parse("8a7c3dc1-b45d-40ed-9b95-69c1c68cb761"),
        Guid.Parse("ef585cb3-e251-459d-919c-ae7deeb702fa"),
        "host", "peer", new string('A', 64), new string('B', 64), Start.AddMinutes(1));

    [Fact]
    public void PreparationRequiresBothLocalAndRemoteConfirmations()
    {
        var identity = Identity();
        var session = new PairingConsentState(identity, () => Start);
        Assert.Equal(PairingConsentPhase.AwaitingConfirmation, session.Phase);
        Assert.Throws<InvalidOperationException>(() => session.BeginPrepare(identity));
        session.ConfirmLocal(identity);
        Assert.Throws<InvalidOperationException>(() => session.BeginPrepare(identity));
        session.ConfirmRemote(identity);
        Assert.Equal(PairingConsentPhase.ReadyToPrepare, session.Phase);
        session.BeginPrepare(identity);
        Assert.Equal(PairingConsentPhase.Preparing, session.Phase);
    }

    [Fact]
    public void DifferentSessionNonceNamesAndFingerprintsCannotConfirmOrAdvance()
    {
        var identity = Identity();
        var session = new PairingConsentState(identity, () => Start);
        var mismatches = new[]
        {
            identity with { SessionId = Guid.NewGuid() },
            identity with { OfferNonce = Guid.NewGuid() },
            identity with { TransactionId = Guid.NewGuid() },
            identity with { HostName = "other-host" },
            identity with { PeerName = "other-peer" },
            identity with { HostFingerprint = new string('C', 64) },
            identity with { PeerFingerprint = new string('D', 64) },
            identity with { ExpiresAtUtc = identity.ExpiresAtUtc.AddSeconds(1) }
        };
        foreach (var mismatch in mismatches)
        {
            Assert.Throws<InvalidOperationException>(() => session.ConfirmLocal(mismatch));
            Assert.Throws<InvalidOperationException>(() => session.ConfirmRemote(mismatch));
        }
        Assert.Equal(PairingConsentPhase.AwaitingConfirmation, session.Phase);
        session.ConfirmLocal(identity); session.ConfirmRemote(identity); session.BeginPrepare(identity);
        Assert.Throws<InvalidOperationException>(() => session.AcceptReady(identity with { OfferNonce = Guid.NewGuid() }));
        Assert.Equal(PairingConsentPhase.Preparing, session.Phase);
    }

    [Fact]
    public void SuccessRequiresOrderedAcknowledgementsAndRejectsReplays()
    {
        var identity = Identity(); var session = new PairingConsentState(identity, () => Start);
        session.ConfirmRemote(identity);
        Assert.Throws<InvalidOperationException>(() => session.ConfirmRemote(identity));
        Assert.Throws<InvalidOperationException>(() => session.AcceptReady(identity));
        session.ConfirmLocal(identity);
        session.BeginPrepare(identity);
        Assert.Throws<InvalidOperationException>(() => session.BeginCommit(identity));
        session.AcceptReady(identity);
        Assert.Throws<InvalidOperationException>(() => session.AcceptReady(identity));
        session.BeginCommit(identity);
        Assert.Throws<InvalidOperationException>(() => session.AcceptApplied(identity));
        session.CommitSucceeded(identity);
        session.AcceptApplied(identity);
        Assert.Equal(PairingConsentPhase.Completed, session.Phase);
        Assert.Throws<InvalidOperationException>(() => session.AcceptApplied(identity));
        Assert.Throws<InvalidOperationException>(() => session.BeginPrepare(identity));
    }

    [Fact]
    public void CancellationAndExpiryBeforeCommitAbortWithoutAcceptingLateMessages()
    {
        var now = Start; var identity = Identity(); var session = new PairingConsentState(identity, () => now);
        session.ConfirmLocal(identity); session.ConfirmRemote(identity); session.BeginPrepare(identity);
        session.Stop();
        Assert.Equal(PairingConsentPhase.Aborted, session.Phase);
        Assert.Throws<InvalidOperationException>(() => session.AcceptReady(identity));
        var expired = new PairingConsentState(identity, () => now);
        expired.ConfirmLocal(identity); now = identity.ExpiresAtUtc;
        Assert.Equal(PairingConsentPhase.Aborted, expired.Phase);
        Assert.Throws<InvalidOperationException>(() => expired.ConfirmRemote(identity));
    }

    [Fact]
    public void InterruptedCommitOrLostFinalReceiptIsUnknownRatherThanAbortedOrComplete()
    {
        var identity = Identity();
        static PairingConsentState Prepared(PairingSessionIdentity identity)
        {
            var state = new PairingConsentState(identity, () => Start);
            state.ConfirmLocal(identity); state.ConfirmRemote(identity); state.BeginPrepare(identity); state.AcceptReady(identity);
            return state;
        }
        var duringCommit = Prepared(identity); duringCommit.BeginCommit(identity); duringCommit.Stop();
        Assert.Equal(PairingConsentPhase.ResultUnknown, duringCommit.Phase);
        Assert.Throws<InvalidOperationException>(() => duringCommit.CommitSucceeded(identity));
        var beforeReceipt = Prepared(identity); beforeReceipt.BeginCommit(identity); beforeReceipt.CommitSucceeded(identity); beforeReceipt.Stop();
        Assert.Equal(PairingConsentPhase.ResultUnknown, beforeReceipt.Phase);
        Assert.Throws<InvalidOperationException>(() => beforeReceipt.AcceptApplied(identity));
    }

    [Fact]
    public void ExpiryAfterCommitStartedReportsUnknown()
    {
        var now = Start; var identity = Identity(); var session = new PairingConsentState(identity, () => now);
        session.ConfirmLocal(identity); session.ConfirmRemote(identity); session.BeginPrepare(identity);
        session.AcceptReady(identity); session.BeginCommit(identity); session.CommitSucceeded(identity);
        now = identity.ExpiresAtUtc;
        Assert.Equal(PairingConsentPhase.ResultUnknown, session.Phase);
        Assert.Throws<InvalidOperationException>(() => session.AcceptApplied(identity));
    }

    [Fact]
    public async Task ConcurrentStopAndConfirmationCannotResumeAnAbortedSession()
    {
        var identity = Identity();
        for (var i = 0; i < 40; i++)
        {
            var session = new PairingConsentState(identity, () => Start);
            session.ConfirmLocal(identity);
            using var start = new ManualResetEventSlim();
            var confirm = Task.Run(() => { start.Wait(); try { session.ConfirmRemote(identity); } catch (InvalidOperationException) { } });
            var stop = Task.Run(() => { start.Wait(); session.Stop(); });
            start.Set();
            await Task.WhenAll(confirm, stop);
            Assert.Equal(PairingConsentPhase.Aborted, session.Phase);
            Assert.Throws<InvalidOperationException>(() => session.BeginPrepare(identity));
        }
    }

    [Fact]
    public async Task ConcurrentStopAndCommitAlwaysReportUnknown()
    {
        var identity = Identity();
        for (var i = 0; i < 40; i++)
        {
            var session = new PairingConsentState(identity, () => Start);
            session.ConfirmLocal(identity); session.ConfirmRemote(identity); session.BeginPrepare(identity);
            session.AcceptReady(identity); session.BeginCommit(identity);
            using var start = new ManualResetEventSlim();
            var commit = Task.Run(() => { start.Wait(); try { session.CommitSucceeded(identity); } catch (InvalidOperationException) { } });
            var stop = Task.Run(() => { start.Wait(); session.Stop(); });
            start.Set();
            await Task.WhenAll(commit, stop);
            Assert.Equal(PairingConsentPhase.ResultUnknown, session.Phase);
            Assert.Throws<InvalidOperationException>(() => session.AcceptApplied(identity));
        }
    }

    [Fact]
    public void ClockRollbackAndInvalidInitialBindingFailClosed()
    {
        var now = Start; var identity = Identity(); var session = new PairingConsentState(identity, () => now);
        session.ConfirmLocal(identity); now = now.AddSeconds(-1);
        Assert.Equal(PairingConsentPhase.Aborted, session.Phase);
        Assert.Throws<InvalidOperationException>(() => session.ConfirmRemote(identity));
        Assert.Throws<ArgumentException>(() => new PairingConsentState(identity with { OfferNonce = Guid.Empty }, () => Start));
        Assert.Throws<ArgumentException>(() => new PairingConsentState(identity with { TransactionId = Guid.Empty }, () => Start));
        Assert.Throws<ArgumentException>(() => new PairingConsentState(identity with { PeerFingerprint = "not-a-fingerprint" }, () => Start));
        Assert.Throws<ArgumentException>(() => new PairingConsentState(identity with { ExpiresAtUtc = Start }, () => Start));
    }
}
