using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink;

public interface IMultiDeviceAuthorizer
{
    bool IsAuthorized(string name, string token);
    Task<bool> RevokeAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// In-memory credential isolation primitive for future multi-device sessions.
/// Caller must verify the fingerprint with the user before adding a device.
/// This is not a persistent trust store or an active network endpoint.
/// </summary>
public sealed class MultiDeviceCredentials : IDisposable, IMultiDeviceAuthorizer
{
    private sealed record Entry(string Fingerprint, byte[] Token);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> peers = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    public const int MaximumPeers = 4;

    public string AddConfirmed(string name, string fingerprint)
    {
        DeskflowProfile.ValidateName(name);
        var normalized = DeskLinkFingerprint.Normalize(fingerprint);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (peers.ContainsKey(name) || peers.Values.Any(p => p.Fingerprint == normalized))
                throw new ArgumentException("Device name or fingerprint is already registered.");
            if (peers.Count >= MaximumPeers) throw new InvalidOperationException("The device limit has been reached.");
            var token = RandomNumberGenerator.GetBytes(32);
            peers.Add(name, new(normalized, token));
            return Convert.ToBase64String(token);
        }
    }

    public bool IsAuthorized(string name, string token)
    {
        byte[] supplied;
        try { supplied = Convert.FromBase64String(token ?? ""); }
        catch (FormatException) { return false; }
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return supplied.Length == 32 && name is not null &&
                    peers.TryGetValue(name, out var peer) &&
                    CryptographicOperations.FixedTimeEquals(peer.Token, supplied);
            }
        }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }

    public bool Revoke(string name)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!peers.Remove(name, out var peer)) return false;
            CryptographicOperations.ZeroMemory(peer.Token);
            return true;
        }
    }

    public Task<bool> RevokeAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Revoke(name));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var peer in peers.Values) CryptographicOperations.ZeroMemory(peer.Token);
            peers.Clear();
            disposed = true;
        }
    }
}
