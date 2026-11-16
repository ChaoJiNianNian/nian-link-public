namespace PreviewFenceModel;

// Test harness only: request labels select handles held by this trusted fixture.
// A network client must never have this registration or lookup facility.
internal sealed class TestFence(int capacity, bool previousProcessProvenStopped)
{
    private readonly Fence inner = new(capacity, previousProcessProvenStopped);
    private readonly Dictionary<(string, string), object> handles = [];
    public Guid Incarnation => inner.Incarnation;
    private object? Find(string principal, string grant) => handles.GetValueOrDefault((principal, grant));
    public bool RegisterTrustedGrant(string principal, string grant)
    {
        var handle = inner.RegisterTrustedGrant(principal, grant);
        if (handle is null) return false;
        handles.Add((principal, grant), handle); return true;
    }
    public bool ReplaceTrustedGrant(string principal, string oldGrant, string newGrant)
    {
        var handle = inner.ReplaceTrustedGrant(Find(principal, oldGrant), newGrant);
        if (handle is null) return false;
        handles.Add((principal, newGrant), handle); return true;
    }
    public Result RevokeTrustedGrant(string principal, string grant) => inner.RevokeTrustedGrant(Find(principal, grant));
    public Task<Result> CleanupRevoked(Guid operation, Func<Task<bool>> action) => inner.CleanupRevoked(operation, action);
    public Task<Result> Begin(Guid incarnation, string principal, Guid operation, string grant, string digest, Func<Task<bool>> action) =>
        inner.Begin(incarnation, Find(principal, grant), operation, digest, action);
    public Task<Result> Seal(Guid incarnation, string principal, Guid operation, string grant, string digest, Func<Task<bool>> action) =>
        inner.Seal(incarnation, Find(principal, grant), operation, digest, action);
    public Task<Result> Renew(Guid incarnation, string principal, Guid operation, string grant, string digest) =>
        inner.Renew(incarnation, Find(principal, grant), operation, digest);
}
