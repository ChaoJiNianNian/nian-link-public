using System.Reflection;
using Avalonia.Controls;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyReturnCloseAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var calls = 0;
            var fail = mode == "return-close-failure";
            Func<string, Task> action = _ => { calls++; return fail ? Task.FromException(new InvalidOperationException("synthetic failure")) : Task.CompletedTask; };
            var type = typeof(MacRemotePreviewReturnWindow);
            var click = type.GetMethod("ReturnAsync", flags)!;
            MacMultiDeviceWindow? main = null;
            MethodInfo? update = null;
            FieldInfo? current = null;
            MacRemotePreviewReturnWindow window;
            if (mode is "return-close-multi" or "return-close-hidden")
            {
                main = new MacMultiDeviceWindow(Path.Combine(Path.GetDirectoryName(resultPath)!, "return-close-" + Guid.NewGuid().ToString("N")), false, new FakeVault());
                update = typeof(MacMultiDeviceWindow).GetMethod("UpdateRemoteReturn", flags)!;
                current = typeof(MacMultiDeviceWindow).GetField("remoteReturn", flags)!;
                var backend = new ReturnCloseBackend(action);
                update.Invoke(main, ["old", backend]);
                window = (MacRemotePreviewReturnWindow)current.GetValue(main)!;
            }
            else
            {
                // Same construction shape as the old single-device view, with
                // a synthetic callback; this is not the full legacy window.
                window = new MacRemotePreviewReturnWindow("old", action);
                window.Show();
            }
            if (!window.IsVisible) throw new InvalidOperationException("Initial return window is not visible.");
            var button = (Button)type.GetField("button", flags)!.GetValue(window)!;
            EventHandler<WindowClosingEventArgs> veto = (_, e) => { if (mode == "return-close-hidden") window.Hide(); e.Cancel = true; };
            if (mode is "return-close-multi" or "return-close-hidden" or "return-close-legacy") window.Closing += veto;
            try
            {
                await ((Task)click.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                if (calls != 1) throw new InvalidOperationException("The existing End was not attempted once.");
                if (mode is "return-close-multi" or "return-close-hidden" or "return-close-legacy")
                {
                    if (main is not null && !ReferenceEquals(current!.GetValue(main), window)) throw new InvalidOperationException("Canceled close lost the return window.");
                    if (!button.IsEnabled || (button.Content as string ?? "").Contains("正在归还", StringComparison.Ordinal) ||
                        !(button.Content as string ?? "").Contains("未确认", StringComparison.Ordinal))
                        throw new InvalidOperationException("Canceled close left a misleading in-progress label.");
                    if (mode == "return-close-hidden")
                    {
                        if (window.IsVisible) throw new InvalidOperationException("The synthetic close did not hide the window.");
                        update!.Invoke(main, ["old", new ReturnCloseBackend(action)]);
                    }
                    if (!window.IsVisible) throw new InvalidOperationException("The retained return attempt is not visible.");
                    window.Closing -= veto;
                    await ((Task)click.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                    if (calls != 2 || window.IsVisible || main is not null && current!.GetValue(main) is not null)
                        throw new InvalidOperationException("The retained return attempt did not close after retry.");
                }
                else if (mode == "return-close-normal" && window.IsVisible)
                    throw new InvalidOperationException("A successful ordinary return failed to close.");
                else if (mode == "return-close-failure" && (!window.IsVisible || (button.Content as string) != "归还未确认 · 检查键鼠"))
                    throw new InvalidOperationException("A failed ordinary return lost the retry state.");
            }
            finally { window.Closing -= veto; if (window.IsVisible) window.Close(); }
        }

        private sealed class ReturnCloseBackend(Func<string, Task> action) : IDeskflowPreviewControl
        {
            public bool PreviewHandoffSupported => true;
            public string PreviewReturnShortcut => "fixture";
            public string PreviewHandoffHint => "fixture";
            public async Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) { await action(id); return new DeskflowPreviewState(); }
            public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Begin.");
            public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Renew.");
            public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Read.");
        }
    }
}
