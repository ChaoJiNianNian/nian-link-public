using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using DesktopToolbox.App.DeskLink;
using DesktopToolbox.App.Hosting;
using DesktopToolbox.Core;
using DesktopToolbox.Windows.DeskSwitch;

namespace NianLink.Smoke;

internal static class Program
{
    private const string AllowedParent = @"D:\Projects\nian-link\tests\.work\standalone-profile";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, nint securityAttributes);

    [STAThread] private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--profile" || args.Length == 3 && args[2] != "--peer") return 2;
        var peerMode = args.Length == 3;
        string root;
        try { root = SmokeProfilePolicy.Validate(args[1], AllowedParent); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return 2; }
        // Lock the parent before atomic creation, and then lock the child itself;
        // DataDirectoryLease rejects reparse points in every path component.
        using var parent = DataDirectoryLease.Acquire(AllowedParent, createIfMissing: true);
        if (!CreateDirectoryW(root, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The isolated GUID profile must not exist already.");
        using var profile = DataDirectoryLease.Acquire(root);
        if (!profile.Exists) throw new IOException("The newly created isolated profile is unavailable.");
        var dataPath = Path.Combine(root, "data");
        var webPath = Path.Combine(root, "webview-profile");
        using var data = DataDirectoryLease.Acquire(dataPath, createIfMissing: true);
        using var web = DataDirectoryLease.Acquire(webPath, createIfMissing: true);
        var resultCode = 1;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            MiniCollaborationWindow? mini = null;
            SingleInstance? instance = null;
            var errors = new List<string>();
            var resources = new Dictionary<string, string>(StringComparer.Ordinal);
            var offline = new OfflineMiniService();
            var peerBackend = new OfflinePeerBackend();
            PeerMiniService? peer = peerMode ? new PeerMiniService("peer", "host", peerBackend) : null;
            IMiniCollaborationService service = peer is null ? offline : peer;
            var ownPid = checked((uint)Environment.ProcessId);
            var peakOwnTcpListeners = 0;
            var settingsRequests = 0;
            void CheckListeners()
            {
                var count = TcpListenerOwners.ReadAll().Count(item => item.ProcessId == ownPid);
                peakOwnTcpListeners = Math.Max(peakOwnTcpListeners, count);
                if (count != 0) throw new InvalidOperationException("The smoke process unexpectedly owns a TCP listener.");
            }
            try
            {
                CheckListeners();
                foreach (var name in new[] { "index.html", "mini.js", "mini.css", "mini-windows.css", "assets/lucide.min.js" })
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "CollaborationUi", name.Replace('/', Path.DirectorySeparatorChar));
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length is <= 0 or > 4 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Missing, empty, linked, or oversized UI resource: " + name);
                    using var stream = info.OpenRead(); resources.Add(name, Convert.ToHexString(SHA256.HashData(stream)));
                }
                instance = new SingleInstance(root);
                if (!instance.IsOwner) throw new InvalidOperationException("Isolated single-instance identity unexpectedly already exists.");
                await using (var duplicate = new SingleInstance(root))
                    if (duplicate.IsOwner) throw new InvalidOperationException("The isolated single-instance identity was not enforced.");
                var store = new JsonFileStore(new AppPaths(dataPath));
                // IsEnabled disables user interaction; StartAsync uses the real window and the real WebView.
                mini = new MiniCollaborationWindow(service, store, error => errors.Add(error.Message), webPath)
                { Title = "年年互联 · 隔离启动测试", IsEnabled = false };
                mini.ConnectionSettingsRequested += () => settingsRequests++;
                await mini.StartAsync().WaitAsync(TimeSpan.FromSeconds(25));
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (!mini.IsPageReadyForSmoke && DateTime.UtcNow < deadline)
                { CheckListeners(); await Task.Delay(100); }
                if (!mini.IsPageReadyForSmoke) throw new TimeoutException("The actual WebView page did not complete its ready handshake.");
                if (errors.Count != 0) throw new InvalidOperationException("The mini window reported an initialization error.");
                if (peerMode && (!service.Snapshot.SharedFolderIsRemote || service.Snapshot.SelectedDevice != "host" || service.Snapshot.Connected))
                    throw new InvalidOperationException("The peer mini did not present its isolated host state.");
                var request = MiniBridgePolicy.Parse("{\"id\":\"0123456789abcdef0123456789abcdef\",\"action\":\"connection.openSettings\",\"payload\":{}}");
                var dispatch = typeof(MiniCollaborationWindow).GetMethod("InvokePageAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("Mini bridge dispatch unavailable.");
                var operation = dispatch.Invoke(mini, new object[] { request, Array.Empty<string>(), CancellationToken.None }) as Task<object?>
                    ?? throw new InvalidOperationException("Mini bridge did not return a task.");
                await operation;
                if (settingsRequests != 1) throw new InvalidOperationException("The connection settings request was not dispatched exactly once.");
                CheckListeners();
                await mini.StopAsync(); mini = null;
                if (peer is not null) await peer.DisposeAsync();
                CheckListeners();
                await instance.DisposeAsync(); instance = null;
                var report = new {
                    succeeded = true, actualMiniWindowAndPageReady = true, settingsBridgeDispatched = true, stoppedAndDisposed = true,
                    businessService = "offline fake; every sharing, preview enable, clipboard, file, and control operation rejects",
                    businessSocketsOpened = false, ownTcpListenersObserved = peakOwnTcpListeners,
                    uiResourcesSha256 = resources, refreshCount = peerMode ? peerBackend.RefreshCount : offline.RefreshCount, peerMode,
                    note = "The WebView2 runtime runs normally; app navigation/resource requests use the production allowlist. TCP samples do not prove browser subprocesses have no independent network traffic.",
                    testedAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MiniCollaborationWindow).Assembly.Location)))
                };
                await File.WriteAllTextAsync(Path.Combine(root, "startup-smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                resultCode = 0;
            }
            catch (Exception error)
            {
                try { await File.WriteAllTextAsync(Path.Combine(root, "startup-failure.txt"), error.ToString()); } catch { }
            }
            finally
            {
                if (mini is not null) try { await mini.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch { resultCode = 1; }
                if (instance is not null) try { await instance.DisposeAsync(); } catch { resultCode = 1; }
                app.Shutdown(resultCode);
            }
        };
        app.Run(); return resultCode;
    }
}
