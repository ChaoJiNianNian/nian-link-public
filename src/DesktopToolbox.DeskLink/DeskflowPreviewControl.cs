namespace DesktopToolbox.DeskLink;

public enum DeskflowPreviewPhase { Viewing, Preparing, Active, Returning, Recovery }

/// <summary>Input handoff only. The existing paired data connection and monitor bindings stay unchanged.</summary>
public sealed record DeskflowPreviewState
{
    public string? Id { get; init; }
    public string ViewerComputer { get; init; } = "";
    public string TargetComputer { get; init; } = "";
    public DeskflowPreviewPhase Phase { get; init; }
    public string ReturnShortcut { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public bool HasSession => Id is not null && Phase != DeskflowPreviewPhase.Viewing;
    public bool IsActive => Phase == DeskflowPreviewPhase.Active;
    public bool NeedsReturn => Phase is DeskflowPreviewPhase.Returning or DeskflowPreviewPhase.Recovery;
}

/// <summary>The adapter binds its viewer locally; a network request cannot choose another viewer.</summary>
public interface IDeskflowPreviewControl
{
    bool PreviewHandoffSupported { get; }
    string PreviewReturnShortcut { get; }
    string PreviewHandoffHint { get; }
    Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default);
    Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default);
    Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default);
    Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default);
}
