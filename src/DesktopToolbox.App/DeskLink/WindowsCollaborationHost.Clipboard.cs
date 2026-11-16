using DesktopToolbox.DeskLink.Collaboration;

namespace DesktopToolbox.App.DeskLink;

public sealed partial class WindowsCollaborationHost
{
    private async Task ExchangeClipboardAsync(CollaborationServer owned, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, clipboardLifetime.Token);
        token = linked.Token;
        var active = settings.ClipboardEnabled && owned.RemoteClipboardEnabled && owned.IsPeerConnected;
        var observedSequence = clipboard.Sequence;
        try
        {
            if (cursor.ShouldPublish(observedSequence, active))
            {
                var value = await clipboard.ReadAsync(settings.OriginId, token);
                if (settings.ClipboardEnabled && owned.RemoteClipboardEnabled && clipboard.Sequence == observedSequence)
                {
                    if (value is not null) owned.SetLocalClipboard(value);
                    else owned.ClearClipboard();
                }
                clipboardError = null;
                return;
            }
            if (!active || !settings.ClipboardEnabled || clipboard.Sequence != observedSequence || owned.GetRemoteClipboard() is not { } remote || !cursor.ShouldApply(remote.Id, remote.Origin)) return;
            if (remote.Text is null || remote.PngBytes is not null) { cursor.Ignore(remote.Id); return; }
            var sequence = await clipboard.ApplyAsync(remote, token, observedSequence);
            if (sequence.HasValue) { cursor.Applied(remote.Id, sequence.Value); clipboardError = null; }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or IOException or ArgumentException or InvalidOperationException)
        {
            if (error is System.Runtime.InteropServices.ExternalException) cursor.RetryPublish(observedSequence);
            else if (settings.ClipboardEnabled && owned.RemoteClipboardEnabled)
                try { owned.ClearClipboard(); } catch (InvalidOperationException) { }
            clipboardError = "剪贴板暂未同步：" + error.Message;
        }
    }
}
