using System;
using DesktopToolbox.DeskLink;

namespace NianLink.MultiDevice;

internal static class PairingReviewGuard
{
    internal static string? ChooseDestination(MultiPeerPairingPreview expected, long generation,
        Func<MultiPeerPairingPreview?> getCurrent, Func<long> getGeneration, Func<DateTimeOffset> clock,
        Func<bool> confirm, Func<string?> chooseDestination)
    {
        if (!confirm()) return null;
        RequireCurrent(expected, getCurrent(), generation, getGeneration(), clock());
        var destination = chooseDestination();
        if (destination is null) return null;
        RequireCurrent(expected, getCurrent(), generation, getGeneration(), clock());
        return destination;
    }

    internal static void RequireCurrent(MultiPeerPairingPreview expected, MultiPeerPairingPreview? current,
        long expectedGeneration, long currentGeneration, DateTimeOffset now)
    {
        if (!ReferenceEquals(expected, current) || expectedGeneration != currentGeneration || now >= expected.ExpiresAtUtc)
            throw new InvalidOperationException("身份信息已改变或过期，请重新选择后核对。");
    }
}
