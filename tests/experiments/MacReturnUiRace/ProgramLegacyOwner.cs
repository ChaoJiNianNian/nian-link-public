using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;
using DesktopToolbox.Mac.ViewModels;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static void VerifyLegacyReturnOwnership()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var profile = Path.Combine(Path.GetDirectoryName(resultPath)!, "legacy-owner-unused-profile");
            if (Directory.Exists(profile) || File.Exists(profile)) throw new InvalidOperationException("The unused synthetic profile path must not already exist.");
            var service = new MacLinkService(profile);
            var owner = new MainWindow(service, new LegacyWireless()); // Never shown or initialized.
            var model = (MacLinkViewModel)typeof(MainWindow).GetField("model", flags)!.GetValue(owner)!;
            var snapshot = typeof(MacLinkViewModel).GetField("snapshot", flags)!;
            var update = typeof(MainWindow).GetMethod("UpdateRemoteReturnWindow", flags)!;
            var field = typeof(MainWindow).GetField("remoteReturnWindow", flags)!;
            var oldId = "11111111111111111111111111111111";
            var newId = "22222222222222222222222222222222";
            MacLinkSnapshot State(string id) => new()
            {
                HasApprovedPairing = true, PreviewHandoffSupported = true, IsRunning = true,
                IsConnected = true, ControlConnected = true, PreviewControlState = new DeskLinkState(),
                ActiveComputer = "My-Mac", Settings = new MacConnectionSettings { WindowsName = "Win-PC", MacName = "My-Mac" },
                InputPreview = new DeskflowPreviewState { Id = id, Phase = DeskflowPreviewPhase.Active, ViewerComputer = "Win-PC", TargetComputer = "My-Mac" }
            };
            snapshot.SetValue(model, State(oldId));
            update.Invoke(owner, null);
            var old = (MacRemotePreviewReturnWindow?)field.GetValue(owner) ?? throw new InvalidOperationException("Fixture did not create a return window.");
            if (!old.IsVisible) throw new InvalidOperationException("Initial legacy return window is not visible.");
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            var closeCalls = 0;
            EventHandler<WindowClosingEventArgs> handler = (_, args) =>
            {
                closeCalls++;
                if (mode == "legacy-owner-hidden-veto") old.Hide();
                if (mode == "legacy-owner-reentrant" && closeCalls == 1)
                {
                    try { update.Invoke(owner, null); }
                    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
                }
                args.Cancel = true;
            };
            if (mode is "legacy-owner-veto" or "legacy-owner-hidden-veto" or "legacy-owner-reentrant") old.Closing += handler;
            try
            {
                if (mode == "legacy-owner-closed")
                {
                    old.Close();
                    if (field.GetValue(owner) is not null || old.IsVisible)
                        throw new InvalidOperationException("A truly closed legacy window remains owned.");
                    update.Invoke(owner, null);
                    if (field.GetValue(owner) is not MacRemotePreviewReturnWindow next || ReferenceEquals(next, old) || !next.IsVisible)
                        throw new InvalidOperationException("The same session did not obtain a new visible window after Closed.");
                }
                else if (mode is "legacy-owner-normal" or "legacy-owner-empty")
                {
                    snapshot.SetValue(model, mode == "legacy-owner-empty" ? new MacLinkSnapshot() : State(newId));
                    update.Invoke(owner, null);
                    var next = field.GetValue(owner) as MacRemotePreviewReturnWindow;
                    if (old.IsVisible || (mode == "legacy-owner-empty" ? next is not null : next is null || !next.IsVisible || next.SessionId != newId))
                        throw new InvalidOperationException("The ordinary legacy close or replacement did not finish.");
                }
                else
                {
                    snapshot.SetValue(model, State(newId));
                    try { update.Invoke(owner, null); }
                    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
                    var current = field.GetValue(owner);
                    var visible = lifetime.Windows.Count(w => w.IsVisible && w.Title == MacRemotePreviewReturnWindow.CaptureTitle);
                    if (!ReferenceEquals(current, old) || visible > 1 || mode == "legacy-owner-reentrant" && closeCalls != 1)
                        throw new InvalidOperationException($"Canceled legacy close lost ownership or created duplicates: retained={ReferenceEquals(current, old)}, visible={visible}, closeCalls={closeCalls}.");
                }
            }
            finally
            {
                old.Closing -= handler;
                foreach (var window in lifetime.Windows.Where(w => w.Title == MacRemotePreviewReturnWindow.CaptureTitle).ToArray()) window.Close();
                model.Dispose();
                if (Directory.Exists(profile) || File.Exists(profile)) throw new InvalidOperationException("The fixture unexpectedly created a profile.");
            }
        }

        private sealed class LegacyWireless : IMacWirelessModeService
        {
            public Task<MacWirelessSnapshot> RefreshAsync(CancellationToken token = default) => Task.FromResult(new MacWirelessSnapshot(MacAwdlState.Unavailable, "fixture", ""));
            public Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled, CancellationToken token = default) => RefreshAsync(token);
        }
    }
}
