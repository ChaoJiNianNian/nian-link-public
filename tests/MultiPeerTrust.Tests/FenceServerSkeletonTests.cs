using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using NianLink.Windows;

namespace MultiPeerTrust.Tests;

public sealed class FenceServerSkeletonTests
{
    private static string ResearchRoot => Path.GetFullPath(
        typeof(MultiPeerControlServer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "NianFenceResearchRoot").Value
        ?? throw new InvalidOperationException("Missing isolated research root."));
    private static string ResearchSiblingRoot => Path.Combine(
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(ResearchRoot))
            ?? throw new InvalidOperationException("Missing research parent directory."),
        "fence-real-server-sibling");
    [Fact]
    public async Task EmergencyBoundToObservedRevisionRejectsChangedRevisionBeforeAttempt()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        Assert.True(probe.GetProperty("ok").GetBoolean());
        Assert.False(probe.GetRawText().Contains(f.Token, StringComparison.Ordinal));
        var incarnation = probe.GetProperty("incarnation").GetString();
        var revision = probe.GetProperty("keyboardRevision").GetInt64();
        // Only the in-memory fixture is changed: the focus moves away and back
        // to the same peer, so the old observation must not be reused.
        f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("host");
        f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("peer");
        var rejected = await f.SendAsync(2, "emergency-bound", Guid.NewGuid(), incarnation, expectedRevision: revision);
        Assert.False(rejected.GetProperty("ok").GetBoolean());
        Assert.Equal("stale-or-unavailable", rejected.GetProperty("fenceResult").GetString());
        Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
    }

    [Fact]
    public async Task EmergencyBoundAllowsMatchingObservationButRemainsUnconfirmed()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        Assert.True(probe.GetProperty("ok").GetBoolean());
        var incarnation = probe.GetProperty("incarnation").GetString();
        var revision = probe.GetProperty("keyboardRevision").GetInt64();
        var result = await f.SendAsync(2, "emergency-bound", Guid.NewGuid(), incarnation, expectedRevision: revision);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("attempted-unconfirmed", result.GetProperty("fenceResult").GetString());
        Assert.Equal(1, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        Assert.False(result.GetRawText().Contains(f.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmergencyBoundRejectsMissingStaleOrForeignIncarnationAndCredentials()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        await using var other = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        var foreign = await other.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        var incarnation = probe.GetProperty("incarnation").GetString();
        var revision = probe.GetProperty("keyboardRevision").GetInt64();
        Assert.NotEqual(incarnation, foreign.GetProperty("incarnation").GetString());
        foreach (var wrongIncarnation in new[] { (string?)null, Guid.NewGuid().ToString("N"), foreign.GetProperty("incarnation").GetString() })
        {
            var rejected = await f.SendAsync(2, "emergency-bound", Guid.NewGuid(), wrongIncarnation, expectedRevision: revision);
            Assert.False(rejected.GetProperty("ok").GetBoolean());
            Assert.Equal("stale-or-unavailable", rejected.GetProperty("fenceResult").GetString());
        }
        var missingRevision = await f.SendAsync(2, "emergency-bound", Guid.NewGuid(), incarnation);
        Assert.False(missingRevision.GetProperty("ok").GetBoolean());
        var deniedProbe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null, suppliedToken: Convert.ToBase64String(new byte[32]));
        Assert.False(deniedProbe.GetProperty("ok").GetBoolean());
        var deniedAttempt = await f.SendAsync(2, "emergency-bound", Guid.NewGuid(), incarnation, peer: "other", expectedRevision: revision);
        Assert.False(deniedAttempt.GetProperty("ok").GetBoolean());
        Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
    }

    [Fact]
    public async Task EmergencyBoundRechecksRevisionAfterAuthenticationBeforeAdmissionToManager()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        var revision = probe.GetProperty("keyboardRevision").GetInt64();
        var controls = f.Server.EmergencyResearchControls;
        controls.HoldAfterPermit = true;
        var request = f.SendAsync(2, "emergency-bound", Guid.NewGuid(), probe.GetProperty("incarnation").GetString(), expectedRevision: revision);
        try
        {
            await controls.Permitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("host");
            f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("peer");
            controls.ReleasePermit.TrySetResult();
            var rejected = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("stale-or-unavailable", rejected.GetProperty("fenceResult").GetString());
            Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
        finally { controls.ReleasePermit.TrySetResult(); }
    }

    [Fact]
    public async Task EmergencyBoundRechecksCredentialAfterRegistration()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        var controls = f.Server.EmergencyResearchControls;
        controls.HoldAfterRegistration = true;
        var request = f.SendAsync(2, "emergency-bound", Guid.NewGuid(), probe.GetProperty("incarnation").GetString(),
            expectedRevision: probe.GetProperty("keyboardRevision").GetInt64());
        try
        {
            await controls.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await f.Vault.RevokeAsync("peer"));
            controls.ReleaseRegistration.TrySetResult();
            var rejected = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(rejected.GetProperty("ok").GetBoolean());
            Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
        finally { controls.ReleaseRegistration.TrySetResult(); }
    }

    [Fact]
    public async Task EmergencyBoundAdmittedBeforeRevocationStillCannotClaimReturnConfirmed()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        var controls = f.Server.EmergencyResearchControls;
        controls.HoldAfterPermit = true;
        var request = f.SendAsync(2, "emergency-bound", Guid.NewGuid(), probe.GetProperty("incarnation").GetString(),
            expectedRevision: probe.GetProperty("keyboardRevision").GetInt64());
        try
        {
            await controls.Permitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await f.Vault.RevokeAsync("peer"));
            controls.ReleasePermit.TrySetResult();
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Equal("attempted-unconfirmed", result.GetProperty("fenceResult").GetString());
            Assert.Equal(1, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
        finally { controls.ReleasePermit.TrySetResult(); }
    }

    [Fact]
    public async Task LegacyResearchEmergencyStillBypassesBoundRevision()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        Assert.True(probe.GetProperty("ok").GetBoolean());
        f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("host");
        f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("peer");
        var legacy = await f.SendAsync(2, "emergency", Guid.NewGuid(), null);
        Assert.False(legacy.GetProperty("ok").GetBoolean());
        Assert.Equal("attempted-unconfirmed", legacy.GetProperty("fenceResult").GetString());
        Assert.Equal(1, f.Server.EmergencyResearchKeyboard.SwitchCalls);
    }

    [Fact]
    public async Task EmergencyBoundObservationIsNotBoundToPairingGeneration()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var probe = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null);
        var incarnation = probe.GetProperty("incarnation").GetString();
        var revision = probe.GetProperty("keyboardRevision").GetInt64();
        var replacement = await f.RePairAsync();
        var oldCredential = await f.SendAsync(2, "emergency-bound", Guid.NewGuid(), incarnation, expectedRevision: revision);
        Assert.False(oldCredential.GetProperty("ok").GetBoolean());
        Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        // Deliberately documents a limitation: no observation ticket ties the
        // returned revision to the old pairing identity or its generation.
        var newCredential = await f.SendAsync(2, "emergency-bound", Guid.NewGuid(), incarnation,
            suppliedToken: replacement, expectedRevision: revision);
        Assert.False(newCredential.GetProperty("ok").GetBoolean());
        Assert.Equal("attempted-unconfirmed", newCredential.GetProperty("fenceResult").GetString());
        Assert.Equal(1, f.Server.EmergencyResearchKeyboard.SwitchCalls);
    }

    [Fact]
    public async Task EmergencyProbeRejectsAnUnrelatedPreviewParticipantWithoutSwitching()
    {
        var (f, otherToken) = await Fixture.CreateEmergencyWithOtherAsync();
        await using (f)
        {
            f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("peer");
            var manager = (DesktopToolbox.DeskLink.Coordination.MultiPeerPreviewManager)typeof(MultiPeerControlServer)
                .GetField("emergencyResearchManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Server)!;
            var active = await manager.BeginAsync("peer", "peer");
            Assert.True(active.IsActive);
            Assert.Equal("host", active.TargetComputer);
            var switches = f.Server.EmergencyResearchKeyboard.SwitchCalls;
            var denied = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null, suppliedToken: otherToken, peer: "other");
            Assert.False(denied.GetProperty("ok").GetBoolean());
            Assert.False(denied.TryGetProperty("keyboardRevision", out _));
            Assert.False(denied.GetRawText().Contains(otherToken, StringComparison.Ordinal));
            Assert.Equal(switches, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
    }

    [Fact]
    public async Task EmergencyProbeRejectsAnotherPeerWhileEmergencyIsUnconfirmed()
    {
        var (f, otherToken) = await Fixture.CreateEmergencyWithOtherAsync();
        await using (f)
        {
            f.Server.EmergencyResearchKeyboard.SetSyntheticFocus("host");
            var first = await f.SendAsync(2, "emergency", Guid.NewGuid(), null);
            Assert.Equal("already-host-unconfirmed", first.GetProperty("fenceResult").GetString());
            var denied = await f.SendAsync(2, "emergency-probe", Guid.NewGuid(), null, suppliedToken: otherToken, peer: "other");
            Assert.False(denied.GetProperty("ok").GetBoolean());
            Assert.False(denied.TryGetProperty("keyboardRevision", out _));
            Assert.False(denied.GetRawText().Contains(otherToken, StringComparison.Ordinal));
            Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
    }
    [Fact]
    public async Task EmergencyResearchRejectsPeerNameEqualToHostIgnoringCase()
    {
        var root = Path.Combine(ResearchRoot, Guid.NewGuid().ToString("N"));
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "vault"), new WindowsCredentialProtector());
        _ = await vault.AddConfirmedAsync("HOST", new string('B', 64));
        Assert.Throws<ArgumentException>(() => MultiPeerControlServer.CreateForEmergencyResearch("host", vault));
    }
    [Fact]
    public async Task EmergencyRejectsRevocationBeforeRegistrationAndNeverFallsBack()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        Assert.True(await f.Vault.RevokeAsync("peer"));
        var rejected = await f.SendAsync(2, "emergency", Guid.NewGuid(), null);
        Assert.False(rejected.GetProperty("ok").GetBoolean());
        Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        var oldSwitch = await f.SendAsync(1, "switch", Guid.NewGuid(), null);
        Assert.False(oldSwitch.GetProperty("ok").GetBoolean());
        Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
    }

    [Fact]
    public async Task EmergencyUsesFixedHostAndDoesNotExposeCredential()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        Assert.False((await f.SendAsync(1, "switch", Guid.NewGuid(), null)).GetProperty("ok").GetBoolean());
        Assert.False((await f.SendAsync(2, "switch", Guid.NewGuid(), null)).GetProperty("ok").GetBoolean());
        var denied = await f.SendAsync(2, "emergency", Guid.NewGuid(), null, target: "peer");
        Assert.False(denied.GetProperty("ok").GetBoolean());
        Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        var result = await f.SendAsync(2, "emergency", Guid.NewGuid(), null);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("attempted-unconfirmed", result.GetProperty("fenceResult").GetString());
        Assert.Equal(1, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        Assert.False(result.GetRawText().Contains(f.Token, StringComparison.Ordinal));
        Assert.False(result.GetRawText().Contains(new string('A', 64), StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmergencyRejectsNonCanonicalCredentialEncodingWithoutAttemptingKeyboard()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var token = f.Token;
        Assert.Equal(44, token.Length);
        Assert.Equal('=', token[43]);
        var index = alphabet.IndexOf(token[42]);
        Assert.True(index >= 0 && (index & 3) == 0);
        var alternative = token[..42] + alphabet[index + 1] + "=";
        Assert.NotEqual(token, alternative);
        Assert.Equal(Convert.FromBase64String(token), Convert.FromBase64String(alternative));

        var denied = await f.SendAsync(2, "emergency", Guid.NewGuid(), null, suppliedToken: alternative);
        Assert.False(denied.GetProperty("ok").GetBoolean());
        Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);

        var accepted = await f.SendAsync(2, "emergency", Guid.NewGuid(), null);
        Assert.False(accepted.GetProperty("ok").GetBoolean());
        Assert.Equal("attempted-unconfirmed", accepted.GetProperty("fenceResult").GetString());
        Assert.Equal(1, f.Server.EmergencyResearchKeyboard.SwitchCalls);
    }

    [Fact]
    public async Task EmergencyRejectsWhenRevocationWinsAfterRegistrationBeforePermit()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var controls = f.Server.EmergencyResearchControls;
        controls.HoldAfterRegistration = true;
        var request = f.SendAsync(2, "emergency", Guid.NewGuid(), null);
        try
        {
            await controls.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await f.Vault.RevokeAsync("peer"));
            controls.ReleaseRegistration.TrySetResult();
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
        finally { controls.ReleaseRegistration.TrySetResult(); }
    }

    [Fact]
    public async Task EmergencyAdmittedBeforeRevocationNeverClaimsConfirmed()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var controls = f.Server.EmergencyResearchControls;
        controls.HoldAfterPermit = true;
        var request = f.SendAsync(2, "emergency", Guid.NewGuid(), null);
        try
        {
            await controls.Permitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await f.Vault.RevokeAsync("peer"));
            controls.ReleasePermit.TrySetResult();
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Equal("attempted-unconfirmed", result.GetProperty("fenceResult").GetString());
            Assert.Equal(1, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
        finally { controls.ReleasePermit.TrySetResult(); }
    }

    [Fact]
    public async Task EmergencyOldGrantCannotUseSameNamedReplacement()
    {
        await using var f = await Fixture.CreateEmergencyAsync();
        var controls = f.Server.EmergencyResearchControls;
        controls.HoldAfterRegistration = true;
        var request = f.SendAsync(2, "emergency", Guid.NewGuid(), null);
        try
        {
            await controls.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await f.RePairAsync();
            controls.ReleaseRegistration.TrySetResult();
            var result = await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.GetProperty("ok").GetBoolean());
            Assert.Equal(0, f.Server.EmergencyResearchKeyboard.SwitchCalls);
        }
        finally { controls.ReleaseRegistration.TrySetResult(); }
    }

    [Fact]
    public async Task AuthenticatedV2ProbeAndSealRejectLaterBegin()
    {
        await using var fixture = await Fixture.CreateAsync();
        var probe = await fixture.SendAsync(2, "probe", Guid.NewGuid(), null);
        Assert.True(probe.GetProperty("ok").GetBoolean());
        var incarnation = probe.GetProperty("incarnation").GetString();
        var operation = Guid.NewGuid();
        var seal = await fixture.SendAsync(2, "seal", operation, incarnation);
        Assert.Equal("sealed", seal.GetProperty("fenceResult").GetString());
        var late = await fixture.SendAsync(2, "begin", operation, incarnation);
        Assert.Equal("rejected", late.GetProperty("fenceResult").GetString());
    }

    [Fact]
    public async Task BeginPayloadSentBeforeSealIsRejectedWhenItsFrameCompletesAfterSeal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var probe = await fixture.SendAsync(2, "probe", Guid.NewGuid(), null);
        var incarnation = probe.GetProperty("incarnation").GetString();
        var operation = Guid.NewGuid();
        var payloadSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldBegin = fixture.SendBeginWithHeldNewlineAsync(operation, incarnation!, payloadSent, release);
        try
        {
            await payloadSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var seal = await fixture.SendAsync(2, "seal", operation, incarnation);
            Assert.Equal("sealed", seal.GetProperty("fenceResult").GetString());
            release.TrySetResult();
            var begin = await heldBegin.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("rejected", begin.GetProperty("fenceResult").GetString());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task V1ControlsRejectedWhileResearchModeIsActive()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var action in new[] { "switch", "preview-begin", "preview-renew", "preview-end", "preview-read" })
        {
            var response = await fixture.SendAsync(1, action, Guid.NewGuid(), null);
            Assert.False(response.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task AuthenticationTargetAndIncarnationAreNotChosenByTheRequester()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.False((await fixture.SendAsync(2, "probe", Guid.NewGuid(), null, Convert.ToBase64String(new byte[32]))).GetProperty("ok").GetBoolean());
        Assert.False((await fixture.SendAsync(2, "probe", Guid.NewGuid(), null, peer: "other")).GetProperty("ok").GetBoolean());
        Assert.False((await fixture.SendAsync(2, "probe", Guid.NewGuid(), null, target: "other")).GetProperty("ok").GetBoolean());
        var probe = await fixture.SendAsync(2, "probe", Guid.NewGuid(), null);
        var operation = Guid.NewGuid();
        var old = await fixture.SendAsync(2, "seal", operation, Guid.NewGuid().ToString("N"));
        Assert.Equal("rejected", old.GetProperty("fenceResult").GetString());
        var current = probe.GetProperty("incarnation").GetString();
        Assert.Equal("unavailable", (await fixture.SendAsync(2, "begin", operation, current)).GetProperty("fenceResult").GetString());
        Assert.Equal("sealed", (await fixture.SendAsync(2, "seal", operation, current)).GetProperty("fenceResult").GetString());
        Assert.Equal("sealed", (await fixture.SendAsync(2, "seal", operation, current)).GetProperty("fenceResult").GetString());
    }

    [Fact]
    public async Task RevokedCredentialCannotUseOldOrNewGrantTombstone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var probe = await fixture.SendAsync(2, "probe", Guid.NewGuid(), null);
        var incarnation = probe.GetProperty("incarnation").GetString();
        var operation = Guid.NewGuid();
        Assert.Equal("sealed", (await fixture.SendAsync(2, "seal", operation, incarnation)).GetProperty("fenceResult").GetString());
        var old = fixture.Token;
        var replacement = await fixture.RePairAsync();
        Assert.False((await fixture.SendAsync(2, "seal", operation, incarnation, old)).GetProperty("ok").GetBoolean());
        var retry = await fixture.SendAsync(2, "seal", operation, incarnation, replacement);
        Assert.Equal("rejected", retry.GetProperty("fenceResult").GetString());
    }

    [Fact]
    public async Task ASecondApprovedPeerCannotReuseAnExistingOperation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = await fixture.AddOtherAsync();
        var probe = await fixture.SendAsync(2, "probe", Guid.NewGuid(), null);
        var incarnation = probe.GetProperty("incarnation").GetString();
        var operation = Guid.NewGuid();
        Assert.Equal("sealed", (await fixture.SendAsync(2, "seal", operation, incarnation)).GetProperty("fenceResult").GetString());
        var attempt = await fixture.SendAsync(2, "seal", operation, incarnation, other, "other");
        Assert.Equal("rejected", attempt.GetProperty("fenceResult").GetString());
    }

    [Fact]
    public async Task DuplicateLegacyTokensCannotAuthenticateResearchServer()
    {
        await using var fixture = await Fixture.CreateDuplicateAsync();
        var response = await fixture.SendAsync(2, "probe", Guid.NewGuid(), null);
        Assert.False(response.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ResponsesDoNotExposeCredentialIdentityOrInternalMarker()
    {
        await using var fixture = await Fixture.CreateAsync();
        var operation = Guid.NewGuid();
        var probe = await fixture.SendAsync(2, "probe", operation, null);
        var incarnation = probe.GetProperty("incarnation").GetString();
        var seal = await fixture.SendAsync(2, "seal", operation, incarnation);
        var raw = seal.GetRawText();
        var key = Convert.FromBase64String(fixture.Token);
        var marker = ResearchGrantMarker.Derive(key, new TrustedDeviceIdentity("peer", new string('A', 64), "unknown", 1));
        try
        {
            // Boolean assertions avoid writing even synthetic sensitive values to test logs.
            Assert.False(raw.Contains(fixture.Token, StringComparison.Ordinal));
            Assert.False(raw.Contains(new string('A', 64), StringComparison.Ordinal));
            Assert.False(raw.Contains(Convert.ToHexString(marker), StringComparison.OrdinalIgnoreCase));
            Assert.False(raw.Contains(Convert.ToBase64String(marker), StringComparison.Ordinal));
            Assert.False(raw.Contains("credential", StringComparison.OrdinalIgnoreCase));
            Assert.False(raw.Contains("digest", StringComparison.OrdinalIgnoreCase));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(marker); }
    }

    [Fact]
    public async Task ResearchRequestDiagnosticStringIsRedacted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = typeof(MultiPeerControlServer).GetNestedType("Request", System.Reflection.BindingFlags.NonPublic)!;
        var constructor = type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(item => item.GetParameters().Length == 10);
        var request = constructor.Invoke(new object?[] { 2, Guid.NewGuid().ToString("N"), "peer", fixture.Token,
            "probe", "host", null, Guid.NewGuid().ToString("N"), null, 1L });
        Assert.False(request.ToString()!.Contains(fixture.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task VaultRevocationWaitsForAlreadyAdmittedInMemoryTransition()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = Convert.FromBase64String(fixture.Token);
        // Blocking inside this callback is a test-only way to force ordering;
        // actual server transitions must remain short and perform no I/O.
        var commit = Task.Run(() => fixture.Vault.TryCommitAuthorizedAsync("peer", token, () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var revoke = fixture.Vault.RevokeAsync("peer");
            Assert.False(revoke.IsCompleted);
            release.Set();
            Assert.True(await commit);
            Assert.True(await revoke);
            Assert.False(await fixture.Vault.TryCommitAuthorizedAsync("peer", token, () => throw new InvalidOperationException()));
        }
        finally { release.Set(); CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public async Task ConcurrentTlsSealAndRevocationRespectActualVaultOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var probe = await fixture.SendAsync(2, "probe", Guid.NewGuid(), null);
        var incarnation = probe.GetProperty("incarnation").GetString();
        var operation = Guid.NewGuid();
        var token = Convert.FromBase64String(fixture.Token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Test-only blocking callback creates contention at the actual vault gate.
        var holding = Task.Run(() => fixture.Vault.TryCommitUniqueResearchAuthorizedAsync("peer", token, _ =>
        { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var seal = fixture.SendAsync(2, "seal", operation, incarnation, requestSent: sent);
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var revoke = fixture.Vault.RevokeAsync("peer");
            Assert.False(revoke.IsCompleted);
            release.Set();
            Assert.Equal(ResearchGrantAuthorization.Committed, await holding);
            Assert.True(await revoke);
            var oldSeal = await seal;
            Assert.False((await fixture.SendAsync(2, "probe", Guid.NewGuid(), null)).GetProperty("ok").GetBoolean());
            var newToken = await fixture.Vault.AddConfirmedAsync("peer", new string('A', 64));
            var newSeal = await fixture.SendAsync(2, "seal", operation, incarnation, newToken);
            // Whichever contender won the vault lock determines whether an old
            // tombstone exists; it may never be overwritten by the new grant.
            Assert.Equal(oldSeal.GetProperty("ok").GetBoolean() ? "rejected" : "sealed",
                newSeal.GetProperty("fenceResult").GetString());
        }
        finally { release.Set(); CryptographicOperations.ZeroMemory(token); }
    }

    [Fact]
    public async Task DefaultServerDoesNotEnableTheResearchProtocol()
    {
        await using var fixture = await Fixture.CreateAsync(research: false);
        Assert.Null(await fixture.TrySendAsync(2, "probe", Guid.NewGuid(), null));
    }

    [Fact]
    public async Task IncorrectCertificatePinCannotAuthenticateTheResearchServer()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, fixture.Port, deadline.Token);
        await using var tls = new SslStream(socket.GetStream(), false, (_, _, _, _) => false);
        await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(() =>
            tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "test-local" }, deadline.Token));
    }

    [Fact]
    public async Task ResearchServerRejectsVaultOutsideItsIsolatedRoot()
    {
        var outside = Path.Combine(ResearchSiblingRoot, Guid.NewGuid().ToString("N"));
        await using var vault = await MultiDeviceCredentialVault.OpenAsync(outside, new WindowsCredentialProtector());
        Assert.Throws<ArgumentException>(() => MultiPeerControlServer.CreateForFenceResearch("host", vault));
    }

    [Fact]
    public async Task ResearchListenerIsBoundOnlyToLoopback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var listener = typeof(MultiPeerControlServer).GetField("listener", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(fixture.Server) as TcpListener;
        Assert.NotNull(listener);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)listener!.LocalEndpoint).Address);
        Assert.False(File.Exists(Path.Combine(fixture.Vault.FenceResearchDirectory, "multi-control.pem")));
        Assert.False(File.Exists(Path.Combine(fixture.Vault.FenceResearchDirectory, ".identity.lock")));
    }

    [Fact]
    public void CapacityDoesNotDiscardAnyEarlierTombstone()
    {
        using var fence = new FenceResearchState(1);
        var token = new byte[32];
        var identity = new TrustedDeviceIdentity("peer", new string('A', 64), "unknown", 1);
        var old = Guid.NewGuid().ToString("N");
        Assert.Equal("sealed", fence.Apply("seal", identity, token, "host", old, fence.Incarnation));
        Assert.Equal("unconfirmed", fence.Apply("seal", identity, token, "host", Guid.NewGuid().ToString("N"), fence.Incarnation));
        Assert.Equal("rejected", fence.Apply("begin", identity, token, "host", old, fence.Incarnation));
    }

    [Fact]
    public void DisposedResearchFenceClearsMarkersAndRejectsFurtherTransitions()
    {
        var fence = new FenceResearchState(1);
        var token = new byte[32];
        var identity = new TrustedDeviceIdentity("peer", new string('A', 64), "unknown", 1);
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal("sealed", fence.Apply("seal", identity, token, "host", id, fence.Incarnation));
        var field = typeof(FenceResearchState).GetField("sealedOperations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var collection = (System.Collections.IDictionary)field.GetValue(fence)!;
        var binding = collection.Values.Cast<object>().Single();
        var marker = (byte[])binding.GetType().GetProperty("Marker")!.GetValue(binding)!;
        fence.Dispose();
        Assert.True(marker.All(value => value == 0));
        Assert.Throws<ObjectDisposedException>(() => fence.Apply("seal", identity, token, "host", id, fence.Incarnation));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly MultiDeviceCredentialVault vault;
        private readonly MultiPeerControlServer server;
        private readonly string token;
        public string Token => token;
        public int Port => server.Port;
        public MultiPeerControlServer Server => server;
        public MultiDeviceCredentialVault Vault => vault;

        private Fixture(MultiDeviceCredentialVault vault, MultiPeerControlServer server, string token)
        { this.vault = vault; this.server = server; this.token = token; }

        public static async Task<Fixture> CreateAsync(bool research = true)
        {
            var root = Path.Combine(ResearchRoot, Guid.NewGuid().ToString("N"));
            var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "vault"), new WindowsCredentialProtector());
            var token = await vault.AddConfirmedAsync("peer", new string('A', 64));
            var server = research
                ? MultiPeerControlServer.CreateForFenceResearch("host", vault)
                : new MultiPeerControlServer(Path.Combine(root, "control"), IPAddress.Loopback, 0, "host", vault,
                    _ => throw new InvalidOperationException("Fixture must not use keyboard."),
                    (_, _, _, _) => throw new InvalidOperationException("Fixture must not use keyboard."));
            var fixture = new Fixture(vault, server, token);
            await server.StartAsync();
            return fixture;
        }

        public static async Task<Fixture> CreateEmergencyAsync()
        {
            var root = Path.Combine(ResearchRoot, Guid.NewGuid().ToString("N"));
            var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "vault"), new WindowsCredentialProtector());
            var token = await vault.AddConfirmedAsync("peer", new string('A', 64));
            var server = MultiPeerControlServer.CreateForEmergencyResearch("host", vault);
            var fixture = new Fixture(vault, server, token);
            await server.StartAsync();
            return fixture;
        }

        public static async Task<(Fixture, string)> CreateEmergencyWithOtherAsync()
        {
            var root = Path.Combine(ResearchRoot, Guid.NewGuid().ToString("N"));
            var vault = await MultiDeviceCredentialVault.OpenAsync(Path.Combine(root, "vault"), new WindowsCredentialProtector());
            var token = await vault.AddConfirmedAsync("peer", new string('A', 64));
            var otherToken = await vault.AddConfirmedAsync("other", new string('B', 64));
            var server = MultiPeerControlServer.CreateForEmergencyResearch("host", vault);
            var fixture = new Fixture(vault, server, token);
            await server.StartAsync();
            return (fixture, otherToken);
        }

        public static async Task<Fixture> CreateDuplicateAsync()
        {
            var root = Path.Combine(ResearchRoot, Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "vault");
            Directory.CreateDirectory(directory);
            var protector = new WindowsCredentialProtector();
            var key = RandomNumberGenerator.GetBytes(32);
            var encoded = Convert.ToBase64String(key);
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, devices = new[] {
                new { name = "peer", fingerprint = new string('A', 64), token = encoded, platform = "windows", slot = 1 },
                new { name = "other", fingerprint = new string('B', 64), token = encoded, platform = "windows", slot = 2 }
            } });
            try { await File.WriteAllBytesAsync(Path.Combine(directory, "multi-device-trust.bin"), protector.Protect(plain)); }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
            var vault = await MultiDeviceCredentialVault.OpenAsync(directory, protector);
            var server = MultiPeerControlServer.CreateForFenceResearch("host", vault);
            var fixture = new Fixture(vault, server, encoded);
            await server.StartAsync();
            return fixture;
        }

        public async Task<string> RePairAsync()
        { Assert.True(await vault.RevokeAsync("peer")); return await vault.AddConfirmedAsync("peer", new string('A', 64)); }
        public Task<string> AddOtherAsync() => vault.AddConfirmedAsync("other", new string('B', 64));

        public async Task<JsonElement> SendAsync(int version, string action, Guid operation, string? incarnation,
            string? suppliedToken = null, string peer = "peer", string target = "host", TaskCompletionSource? requestSent = null, long? expectedRevision = null)
        {
            var response = await TrySendAsync(version, action, operation, incarnation, suppliedToken, peer, target, requestSent, expectedRevision);
            Assert.True(response.HasValue);
            return response.GetValueOrDefault();
        }

        public async Task<JsonElement?> TrySendAsync(int version, string action, Guid operation, string? incarnation,
            string? suppliedToken = null, string peer = "peer", string target = "host", TaskCompletionSource? requestSent = null, long? expectedRevision = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
            await using var tls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && DeskLinkFingerprint.Matches(server.CertificateSha256!,
                    Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "test-local" }, deadline.Token);
            var requestId = Guid.NewGuid().ToString("N");
            await JsonLineProtocol.WriteAsync(tls, new { version, requestId, peer,
                token = suppliedToken ?? token, action, target, operationId = operation.ToString("N"), incarnation, expectedRevision }, deadline.Token);
            requestSent?.TrySetResult();
            var response = await JsonLineProtocol.ReadAsync(tls, deadline.Token);
            if (response is null) return null;
            using var document = JsonDocument.Parse(response);
            Assert.Equal(requestId, document.RootElement.GetProperty("requestId").GetString());
            Assert.Equal(version, document.RootElement.GetProperty("version").GetInt32());
            return document.RootElement.Clone();
        }

        public async Task<JsonElement> SendBeginWithHeldNewlineAsync(Guid operation, string incarnation,
            TaskCompletionSource payloadSent, TaskCompletionSource release)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, server.Port, deadline.Token);
            await using var tls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && DeskLinkFingerprint.Matches(server.CertificateSha256!,
                    Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "test-local" }, deadline.Token);
            var frame = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, requestId = Guid.NewGuid().ToString("N"),
                peer = "peer", token, action = "begin", target = "host", operationId = operation.ToString("N"), incarnation });
            await tls.WriteAsync(frame, deadline.Token);
            await tls.FlushAsync(deadline.Token);
            payloadSent.TrySetResult(); // JSON payload sent; delimiter remains unsent.
            await release.Task.WaitAsync(deadline.Token);
            await tls.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token);
            await tls.FlushAsync(deadline.Token);
            var reply = await JsonLineProtocol.ReadAsync(tls, deadline.Token);
            Assert.NotNull(reply);
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.Clone();
        }
        public async ValueTask DisposeAsync() { await server.DisposeAsync(); await vault.DisposeAsync(); }
    }
}
