using System.Net;
using System.Reflection;
using Avalonia.Controls;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyActualViewModelRecoveryAsync()
        {
            // The caller explicitly runs the fixture from its project root.
            // Never trust resultPath or a previously placed marker for credentials.
            var project = Path.GetFullPath(Environment.CurrentDirectory);
            var source = Path.Combine(project, "src", "DesktopToolbox.Mac", "Views", "MainWindow.cs");
            var fixture = Path.Combine(project, "tests", "experiments", "MacReturnUiRace", "MacReturnUiRace.csproj");
            if (!File.Exists(source) || !File.Exists(fixture)) throw new InvalidOperationException("Run the fixture from its project root.");
            RejectLinks(source); RejectLinks(fixture);
            var tests = Path.Combine(project, "tests");
            RejectLinks(tests);
            var work = Path.Combine(tests, ".work");
            if (!Directory.Exists(work)) Directory.CreateDirectory(work);
            RejectLinks(work);
            var root = Path.Combine(work, "mac-return-recovery-fixture");
            if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("Fixture directory must be newly created.");
            Directory.CreateDirectory(root);
            RejectLinks(root);
            try { await VerifyRecoveryInsideAsync(root); }
            finally
            {
                // Remove only the three files created by this isolated fixture;
                // do not recurse into unexpected contents or follow reparse points.
                foreach (var relative in new[] { "server/control.pem", "server/pair-token", "client/paired-control.json" })
                {
                    var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(file)) { RejectLinks(file); File.Delete(file); }
                }
                foreach (var path in new[] { Path.Combine(root, "server"), Path.Combine(root, "client"), root })
                {
                    if (Directory.Exists(path)) { RejectLinks(path); Directory.Delete(path); }
                }
            }
        }

        private static void RejectLinks(string path)
        {
            for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Fixture path must not contain a reparse point.");
            }
        }

        private static async Task VerifyRecoveryInsideAsync(string root)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var preview = new ActualModelRecoveryBackend();
            await using var server = new PairedControlServer(new()
            {
                ProfileDirectory = Path.Combine(root, "server"), BindAddress = IPAddress.Loopback,
                Port = 0, AllowedTargets = ["Win-PC", "My-Mac"]
            }, _ => Task.FromResult(preview.State()),
                (_, _) => throw new InvalidOperationException("Unexpected ordinary display switch."), preview);
            await server.StartAsync();
            var bundle = server.CreatePairingBundle("127.0.0.1", "Win-PC", "My-Mac");
            var directory = Path.Combine(root, "client");
            await new MacPrivateStore(directory).WriteAsync("paired-control.json", new { UserApproved = true, BundleJson = bundle.ToJson() }, default);
            await using var service = new MacLinkService(directory);
            typeof(MacLinkService).GetField("clientState", flags)!.SetValue(service,
                new DeskLinkState { ProcessState = DeskLinkProcessState.Running, ConnectionState = DeskLinkConnectionState.Connected, ProcessId = 200 });
            using var model = new MacLinkViewModel(service);
            await model.InitializeAsync();
            var id = model.RemotePreviewReturnId ?? throw new InvalidOperationException("Synthetic Recovery fixture did not expose the return session.");
            var window = new MacRemotePreviewReturnWindow(id, model.ReturnRemotePreviewAsync);
            window.Show();
            try
            {
                var button = (Button)typeof(MacRemotePreviewReturnWindow).GetField("button", flags)!.GetValue(window)!;
                var click = typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", flags)!;
                await ((Task)click.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                if (preview.Ends != 1 || preview.Current.Phase != DeskflowPreviewPhase.Recovery)
                    throw new InvalidOperationException("Synthetic backend did not return Recovery once.");
                if (!window.IsVisible || !button.IsEnabled || !(button.Content as string ?? "").Contains("未确认", StringComparison.Ordinal))
                    throw new InvalidOperationException("Actual MacLinkViewModel Recovery was labelled as definite failure or lost its entry.");
                if ((ToolTip.GetTip(button) as string ?? "").Contains("可能仍在处理", StringComparison.Ordinal) is false)
                    throw new InvalidOperationException("The unconfirmed return did not explain possible in-flight work.");
            }
            finally { if (window.IsVisible) window.Close(); }
        }

        private sealed class ActualModelRecoveryBackend : IDeskflowPreviewControl
        {
            internal int Ends;
            internal DeskflowPreviewState Current = new()
            {
                Id = "11111111111111111111111111111111", ViewerComputer = "Win-PC", TargetComputer = "My-Mac",
                Phase = DeskflowPreviewPhase.Active, ReturnShortcut = "Ctrl+Alt+F12",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(10)
            };
            public bool PreviewHandoffSupported => true;
            public string PreviewReturnShortcut => "Ctrl+Alt+F12";
            public string PreviewHandoffHint => "synthetic";
            internal DeskLinkState State() => new()
            {
                ProcessState = DeskLinkProcessState.Running, ConnectionState = DeskLinkConnectionState.Connected,
                NativeControlAvailable = true, ProcessId = 100, ActiveComputer = "My-Mac", Revision = 1,
                ConnectedComputers = ["My-Mac"], PreviewHandoffSupported = true, InputPreview = Current,
                DisplaySuppressed = true, DisplaySuppressedThroughRevision = 1
            };
            public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default)
            {
                if (id != Current.Id) throw new InvalidOperationException("Unexpected synthetic ID.");
                Ends++;
                Current = Current with { Phase = DeskflowPreviewPhase.Recovery, Message = "尚未确认键鼠归还，请重试" };
                return Task.FromResult(Current);
            }
            public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => Task.FromResult(Current);
            public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Begin.");
            public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Renew.");
        }
    }
}
