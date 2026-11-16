namespace DesktopToolbox.DeskLink;

public sealed partial class PairedControlClient
{
    private DeskLinkState? observedPreviewState;
    public bool PreviewHandoffSupported => Volatile.Read(ref observedPreviewState)?.PreviewHandoffSupported == true;
    public string PreviewReturnShortcut => Volatile.Read(ref observedPreviewState)?.InputPreview?.ReturnShortcut is { Length: > 0 } shortcut ? shortcut : "Ctrl+Alt+F12";
    public string PreviewHandoffHint => PreviewHandoffSupported ? "只交接键鼠，显示器保持原输入源。" : "请先更新两端共享服务。";
    public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default) => PreviewRequestAsync("preview-begin",null,cancellationToken);
    public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default) => PreviewRequestAsync("preview-renew",ValidPreviewId(id),cancellationToken);
    public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default) => PreviewRequestAsync("preview-end",ValidPreviewId(id),cancellationToken);
    public async Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken);
        if (!state.PreviewHandoffSupported) throw new NotSupportedException("The paired service does not support keyboard-only preview handoff.");
        return state.InputPreview ?? throw new InvalidDataException("The paired service omitted its preview handoff state.");
    }
    private async Task<DeskflowPreviewState> PreviewRequestAsync(string action,string? id,CancellationToken token)
    {
        var response = await RequestAsync(action,null,token,id);
        var preview = response.Preview ?? throw new InvalidDataException("The paired service omitted its preview handoff acknowledgement.");
        var previous = Volatile.Read(ref observedPreviewState) ?? new();
        Volatile.Write(ref observedPreviewState,previous with { PreviewHandoffSupported = true,InputPreview = preview,DisplaySuppressed = preview.HasSession });
        return preview;
    }
    private static string ValidPreviewId(string id) => Guid.TryParseExact(id,"N",out var parsed) ? parsed.ToString("N") : throw new ArgumentException("Invalid preview session identifier.",nameof(id));
}
