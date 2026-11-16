using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using NianLink.MultiDevice;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Absolute result path required.");
        using var result = ResearchResultFile.Create(Assembly.GetExecutingAssembly(), args[0]);
        if (result is null) return 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var outcome = 1;
        _ = app.Dispatcher.InvokeAsync(async () =>
        {
            var profile = Path.Combine(Path.GetDirectoryName(args[0])!, "peer-owner-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var window = new MainWindow(profile, Path.Combine(profile, "synthetic-engine.exe"));
                var backend = new Backend();
                await using var session = new DeskflowPreviewSession(backend, (_, _) => Task.CompletedTask);
                session.ObserveFrame(new PreviewFrame { State = "live", Width = 1, Height = 1, JpegBytes = [1], CapturedAtUtc = DateTimeOffset.UtcNow });
                await session.EnableAsync();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainWindow).GetField("peerDetachedSession", flags)!.SetValue(window, session);
                var stop = (Task)typeof(MainWindow).GetMethod("StopPeerPreviewAsync", flags)!.Invoke(window, null)!;
                await backend.EndEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (stop.IsCompleted) throw new InvalidOperationException("Window stop did not wait for preview return.");
                backend.EndReply.TrySetResult(new DeskflowPreviewState());
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
                if (!session.CanStart) throw new InvalidOperationException("Confirmed return did not complete.");
                var returnType = typeof(MainWindow).Assembly.GetType("NianLink.MultiDevice.PreviewReturnWindow", throwOnError: true)!;
                Func<string, Task> unconfirmed = _ => Task.FromException(new InvalidOperationException("尚未确认"));
                var returnWindow = Activator.CreateInstance(returnType, "one", unconfirmed)!;
                await ((Task)returnType.GetMethod("ReturnAsync", flags)!.Invoke(returnWindow, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                var button = (Button)returnType.GetField("button", flags)!.GetValue(returnWindow)!;
                if (!button.Content.ToString()!.Contains("未确认", StringComparison.Ordinal) || !button.IsEnabled)
                    throw new InvalidOperationException("Return window does not preserve an actionable unconfirmed status.");
                var owner = typeof(MainWindow).GetField("peerControlOwner", flags)!.GetValue(window)!;
                owner.GetType().GetField("uncertain", flags)!.SetValue(owner, true);
                typeof(MainWindow).GetMethod("PresentPeerStoppedStatus", flags)!.Invoke(window, null);
                var peerStatus = (TextBlock)window.FindName("PeerStatus");
                if (!peerStatus.Text.Contains("未连接", StringComparison.Ordinal) || !peerStatus.Text.Contains("未确认", StringComparison.Ordinal))
                    throw new InvalidOperationException("Stopped peer status lost its unconfirmed warning.");
                result.Write("main window waits for detached preview return; return button and stopped status report unconfirmed");
                outcome = 0;
            }
            catch (Exception error) { result.Write(error.ToString()); }
            finally { app.Shutdown(); }
        });
        app.Run();
        return outcome;
    }

    private sealed class Backend : IDeskflowPreviewControl
    {
        public TaskCompletionSource EndEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DeskflowPreviewState> EndReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "fixture";
        public string PreviewHandoffHint => "fixture";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState { Phase = DeskflowPreviewPhase.Active, Id = "one" });
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) { EndEntered.TrySetResult(); return EndReply.Task; }
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState { Phase = DeskflowPreviewPhase.Active, Id = id });
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => Task.FromResult(new DeskflowPreviewState());
    }
}
