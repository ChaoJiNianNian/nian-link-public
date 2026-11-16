namespace PreviewOwnerGateExperiment;

// Experimental backend evidence is assumed trustworthy only inside the harness.
internal enum BeginProofKind { Unknown, NoHandoff, Active }
internal readonly record struct BeginProof(BeginProofKind Kind, string? Id)
{
    internal static BeginProof NoHandoff() => new(BeginProofKind.NoHandoff, null);
    internal static BeginProof Active(string id) => new(BeginProofKind.Active, id);
    internal static BeginProof Unknown() => default;
}
internal enum ReturnProof { Unknown, Confirmed }
internal interface IPreviewProofBackend
{
    Task<BeginProof> BeginAsync(CancellationToken ct);
    Task<ReturnProof> ReturnAsync(string id, CancellationToken ct);
    Task AttemptEmergencyReturnAsync(CancellationToken ct);
}
internal sealed class OwnerPreviewGate
{
    private enum State { Idle, Beginning, Active, Returning, Unknown }
    private readonly IPreviewProofBackend backend;
    private readonly object sync = new();
    private State state;
    private string? activeId;

    internal OwnerPreviewGate(IPreviewProofBackend backend) =>
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public bool CanBegin { get { lock (sync) return state == State.Idle; } }

    public async Task BeginAsync(CancellationToken ct = default)
    {
        lock (sync)
        {
            if (state != State.Idle) throw new InvalidOperationException("The previous handoff is not confirmed returned.");
            state = State.Beginning;
        }
        try
        {
            var proof = await backend.BeginAsync(ct).WaitAsync(ct).ConfigureAwait(false);
            lock (sync)
            {
                if (state != State.Beginning) throw new InvalidOperationException("Handoff outcome became uncertain.");
                if (proof.Kind == BeginProofKind.NoHandoff && proof.Id is null) state = State.Idle;
                else if (proof.Kind == BeginProofKind.Active && !string.IsNullOrWhiteSpace(proof.Id)) { activeId = proof.Id; state = State.Active; }
                else throw new InvalidOperationException("Handoff outcome is unconfirmed.");
            }
        }
        catch
        {
            lock (sync) state = State.Unknown;
            throw;
        }
    }

    public async Task ReturnAsync(CancellationToken ct = default)
    {
        string id;
        lock (sync)
        {
            if (state != State.Active || activeId is null) throw new InvalidOperationException("No safely returnable handoff is known.");
            id = activeId;
            state = State.Returning;
        }
        try
        {
            var proof = await backend.ReturnAsync(id, ct).WaitAsync(ct).ConfigureAwait(false);
            lock (sync)
            {
                if (state != State.Returning || proof != ReturnProof.Confirmed)
                    throw new InvalidOperationException("Return is unconfirmed.");
                activeId = null;
                state = State.Idle;
            }
        }
        catch
        {
            lock (sync) state = State.Unknown;
            throw;
        }
    }

    public Task AttemptEmergencyReturnAsync(CancellationToken ct = default)
    {
        // The callback can send an action which continues after timeout, so even
        // its success is not a barrier and cannot prove that this owner is idle.
        lock (sync) state = State.Unknown;
        return backend.AttemptEmergencyReturnAsync(ct);
    }
}
