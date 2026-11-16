using System.Security.Cryptography;

namespace PreviewFenceModel;

// Isolated model only: the handle represents an already-authenticated in-process context.
// It has no transport, product integration, persistence or automatic reconnect.
public enum Result { Active, Sealed, Unconfirmed, Rejected }
public sealed class Fence
{
    private sealed class Entry(string principal, string grant, string digest)
    {
        public string Principal { get; } = principal;
        public string Grant { get; } = grant;
        public string Digest { get; } = digest;
        public bool Matches(string principal, string grant, string digest) => Principal == principal && Grant == grant && Digest == digest;
        public bool BeginPending { get; set; }
        public bool BeginUnconfirmed { get; set; }
        public bool Fenced { get; set; }
        public bool ReturnPending { get; set; }
        public bool ReturnUnconfirmed { get; set; }
        public bool Returned { get; set; }
    }
    private readonly int capacity;
    private readonly bool previousProcessProvenStopped;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly Dictionary<object, (string Principal, string Grant)> authorized = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, object> activeGrant = new(StringComparer.Ordinal);
    private readonly HashSet<(string Principal, string Grant)> retired = [];
    private Guid? owner;
    public Guid Incarnation { get; } = NewIncarnation();
    private static Guid NewIncarnation()
    {
        Guid value;
        do { value = new Guid(RandomNumberGenerator.GetBytes(16)); } while (value == Guid.Empty);
        return value;
    }
    // This is a caller assertion for the isolated model, not evidence obtained by it.
    // A real implementation must independently establish native-process quiescence.
    public Fence(int capacity, bool previousProcessProvenStopped)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        this.previousProcessProvenStopped = previousProcessProvenStopped;
    }

    // Only the trusted in-process test harness may call these methods; request fields
    // never create grants. This is not a credential vault or an authentication protocol.
    internal object? RegisterTrustedGrant(string principal, string grant)
    {
        if (string.IsNullOrWhiteSpace(principal) || string.IsNullOrWhiteSpace(grant)) return null;
        lock (gate)
        {
            var key = (principal, grant);
            if (retired.Contains(key) || activeGrant.ContainsKey(principal)) return null;
            var handle = new object(); authorized.Add(handle, key); activeGrant.Add(principal, handle);
            return handle;
        }
    }

    internal object? ReplaceTrustedGrant(object? oldHandle, string newGrant)
    {
        lock (gate)
        {
            if (oldHandle is null || !authorized.TryGetValue(oldHandle, out var old) ||
                string.IsNullOrWhiteSpace(newGrant) || newGrant == old.Grant || retired.Contains((old.Principal, newGrant))) return null;
            RetireInside(oldHandle, old);
            var handle = new object(); authorized.Add(handle, (old.Principal, newGrant)); activeGrant.Add(old.Principal, handle);
            return handle;
        }
    }

    internal Result RevokeTrustedGrant(object? handle)
    {
        lock (gate)
        {
            if (handle is null || !authorized.TryGetValue(handle, out var key)) return Result.Rejected;
            return RetireInside(handle, key);
        }
    }

    private Result RetireInside(object handle, (string Principal, string Grant) key)
    {
        authorized.Remove(handle); activeGrant.Remove(key.Principal); retired.Add(key);
        if (owner is Guid id && entries.TryGetValue(id, out var entry) && entry.Principal == key.Principal && entry.Grant == key.Grant)
        {
            entry.Fenced = true;
            return Result.Unconfirmed;
        }
        return Result.Sealed;
    }

    internal async Task<Result> CleanupRevoked(Guid operation, Func<Task<bool>> action)
    {
        Entry entry;
        lock (gate)
        {
            if (!previousProcessProvenStopped) return Result.Unconfirmed;
            if (!entries.TryGetValue(operation, out entry!) || !retired.Contains((entry.Principal, entry.Grant)) || !entry.Fenced)
                return Result.Rejected;
            if (entry.Returned) return Result.Sealed;
            if (entry.BeginPending || entry.BeginUnconfirmed || entry.ReturnPending || entry.ReturnUnconfirmed) return Result.Unconfirmed;
            entry.ReturnPending = true;
        }
        return await FinishReturn(operation, entry, action);
    }
    // The model compares supplied labels; real authentication, grant generations, canonical
    // request hashing and process-quiescence proof remain outside this experiment.
    private bool Valid(Guid incarnation, Guid operation, string digest) =>
        incarnation == Incarnation && operation != Guid.Empty && !string.IsNullOrWhiteSpace(digest);

    public async Task<Result> Begin(Guid incarnation, object? handle, Guid operation, string digest, Func<Task<bool>> action)
    {
        if (!Valid(incarnation, operation, digest)) return Result.Rejected;
        Entry entry;
        lock (gate)
        {
            if (handle is null || !authorized.TryGetValue(handle, out var identity)) return Result.Rejected;
            var (principal, grant) = identity;
            if (!previousProcessProvenStopped) return Result.Unconfirmed;
            if (entries.TryGetValue(operation, out var found))
            {
                if (!found.Matches(principal, grant, digest) || found.Fenced) return Result.Rejected;
                return found.BeginPending || found.BeginUnconfirmed ? Result.Unconfirmed : Result.Active;
            }
            if (owner is not null || entries.Count >= capacity) return Result.Rejected;
            entry = new Entry(principal, grant, digest) { BeginPending = true };
            entries.Add(operation, entry); // Before invoking an action that might take effect.
            owner = operation;
        }
        // True must mean the callback's side effect is fully settled, not merely sent or accepted.
        var settled = false;
        try { settled = await action(); }
        catch (Exception) { /* A failed response cannot prove the action had no effect. */ }
        lock (gate)
        {
            entry.BeginPending = false;
            entry.BeginUnconfirmed = !settled;
            return settled && !entry.Fenced ? Result.Active : Result.Unconfirmed;
        }
    }

    public async Task<Result> Seal(Guid incarnation, object? handle, Guid operation, string digest, Func<Task<bool>> action)
    {
        if (!Valid(incarnation, operation, digest)) return Result.Rejected;
        Entry entry;
        lock (gate)
        {
            if (handle is null || !authorized.TryGetValue(handle, out var identity)) return Result.Rejected;
            var (principal, grant) = identity;
            if (!previousProcessProvenStopped) return Result.Unconfirmed;
            if (!entries.TryGetValue(operation, out entry!))
            {
                if (entries.Count >= capacity) return Result.Unconfirmed;
                entries.Add(operation, new Entry(principal, grant, digest) { Fenced = true, Returned = true });
                return Result.Sealed;
            }
            if (!entry.Matches(principal, grant, digest)) return Result.Rejected;
            entry.Fenced = true;
            if (entry.Returned) return Result.Sealed;
            // A late or still-running Begin can move focus after a successful return.
            if (entry.BeginPending || entry.BeginUnconfirmed || entry.ReturnPending || entry.ReturnUnconfirmed) return Result.Unconfirmed;
            entry.ReturnPending = true;
        }
        return await FinishReturn(operation, entry, action);
    }

    private async Task<Result> FinishReturn(Guid operation, Entry entry, Func<Task<bool>> action)
    {
        var returned = false;
        try { returned = await action(); }
        catch (Exception) { /* The return itself may still execute later. */ }
        lock (gate)
        {
            entry.ReturnPending = false;
            if (returned) { entry.Returned = true; if (owner == operation) owner = null; }
            else entry.ReturnUnconfirmed = true;
            return entry.Returned ? Result.Sealed : Result.Unconfirmed;
        }
    }

    public Task<Result> Renew(Guid incarnation, object? handle, Guid operation, string digest)
    {
        if (!Valid(incarnation, operation, digest)) return Task.FromResult(Result.Rejected);
        lock (gate)
        {
            if (handle is null || !authorized.TryGetValue(handle, out var identity)) return Task.FromResult(Result.Rejected);
            var (principal, grant) = identity;
            if (!previousProcessProvenStopped) return Task.FromResult(Result.Unconfirmed);
            return Task.FromResult(entries.TryGetValue(operation, out var entry) && entry.Matches(principal, grant, digest) &&
                !entry.Fenced && !entry.BeginPending && !entry.BeginUnconfirmed ? Result.Active : Result.Rejected);
        }
    }
}
