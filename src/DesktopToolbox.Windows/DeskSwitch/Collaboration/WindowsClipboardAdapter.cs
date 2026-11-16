using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using DesktopToolbox.DeskLink.Collaboration;
using Clipboard = System.Windows.Clipboard;
using TextDataFormat = System.Windows.TextDataFormat;

namespace DesktopToolbox.Windows.DeskSwitch.Collaboration;

public sealed class WindowsClipboardAdapter(Dispatcher dispatcher)
{
    private static readonly uint Excluded = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFlag = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CloudFlag = RegisterClipboardFormat("CanUploadToCloudClipboard");
    public uint Sequence => GetClipboardSequenceNumber();

    public async Task<ClipboardPayload?> ReadAsync(string origin, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var content = await dispatcher.InvokeAsync(() =>
                {
                    if (IsClipboardFormatAvailable(Excluded) || IsDisallowedFlag(HistoryFlag) || IsDisallowedFlag(CloudFlag)) return (string?)null;
                    var hasFiles = Clipboard.ContainsFileDropList();
                    var hasImage = !hasFiles && Clipboard.ContainsImage();
                    var kind = ClipboardTextPolicy.Classify(false, false, false, hasFiles, hasImage,
                        !hasFiles && !hasImage && Clipboard.ContainsText(TextDataFormat.UnicodeText));
                    var text = kind == ClipboardContentKind.Text ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
                    if (text is { Length: > 0 } && Encoding.UTF8.GetByteCount(text) > CollaborationClipboardExchange.MaxTextUtf8Bytes)
                        throw new ArgumentException("文字超过 256 KB，无法同步；请分段复制。");
                    return text is { Length: > 0 } ? text : null;
                }, DispatcherPriority.Background, token);
                return content is { } text ? new(Guid.NewGuid().ToString("N"), origin, DateTimeOffset.UtcNow, Text: text) : null;
            }
            catch (COMException) when (attempt < 2) { await Task.Delay(50, token); }
        }
        return null;
    }

    public async Task<uint?> ApplyAsync(ClipboardPayload payload, CancellationToken token, uint? expectedSequence = null)
    {
        if (payload.PngBytes is not null || payload.Text is null) return null;
        return await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (expectedSequence.HasValue && Sequence != expectedSequence.Value) return (uint?)null;
            if (payload.Text is { Length: > 0 } text && Encoding.UTF8.GetByteCount(text) <= CollaborationClipboardExchange.MaxTextUtf8Bytes) Clipboard.SetText(text, TextDataFormat.UnicodeText);
            else throw new InvalidDataException("收到的文字超过限制。");
            return (uint?)Sequence;
        }, DispatcherPriority.Background, token);
    }

    private static bool IsDisallowedFlag(uint format)
    {
        if (!IsClipboardFormatAvailable(format)) return false;
        if (!OpenClipboard(0)) throw new COMException("剪贴板正在被其他软件使用。");
        try
        {
            var handle = GetClipboardData(format);
            if (handle == 0 || GlobalSize(handle).ToUInt64() < 4) return true;
            var pointer = GlobalLock(handle);
            if (pointer == 0) return true;
            try { return Marshal.ReadInt32(pointer) == 0; }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
}
