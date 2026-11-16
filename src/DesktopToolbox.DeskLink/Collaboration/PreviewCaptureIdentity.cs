namespace DesktopToolbox.DeskLink.Collaboration;

/// <summary>Tracks source availability and monotonic frames for a consented preview session.</summary>
public sealed class PreviewCaptureIdentity
{
    private PreviewSourceStatus status = new(Guid.NewGuid(), 1, "disabled");
    private Guid? request;
    private long sequence;
    public PreviewSourceStatus Status => status;

    public PreviewSourceStatus SetAvailability(string value)
    {
        if (value is not ("enabled" or "disabled" or "locked" or "unavailable" or "unknown"))
            throw new ArgumentException("Invalid preview availability.", nameof(value));
        if (status.Availability != value) status = status with { Availability = value, Revision = checked(status.Revision + 1) };
        return status;
    }

    public long? NextSequence(Guid? requestId)
    {
        if (requestId is null) return null;
        if (requestId == Guid.Empty) throw new ArgumentException("Invalid preview request.", nameof(requestId));
        if (request != requestId) { request = requestId; sequence = 0; }
        return checked(++sequence);
    }
}
