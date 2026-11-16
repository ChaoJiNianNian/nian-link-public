using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyDefaultCloseOwnershipAsync()
        {
#if NIAN_MAC_RETURN_RESEARCH
            throw new InvalidOperationException("Default close ownership test requires the default build.");
#else
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var main = new MacMultiDeviceWindow(Path.Combine(Path.GetDirectoryName(resultPath)!, "close-ownership-" + Guid.NewGuid().ToString("N")), false, new FakeVault());
            var update = typeof(MacMultiDeviceWindow).GetMethod("UpdateRemoteReturn", flags)!;
            var close = typeof(MacMultiDeviceWindow).GetMethod("CloseRemoteReturn", flags)!;
            var field = typeof(MacMultiDeviceWindow).GetField("remoteReturn", flags)!;
            var click = typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", flags)!;
            var backend = new CloseOwnershipBackend();
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            update.Invoke(main, ["old", backend]);
            var old = (MacRemotePreviewReturnWindow)field.GetValue(main)!;
            if (!old.IsVisible) throw new InvalidOperationException("Initial return window is not visible.");
            EventHandler<WindowClosingEventArgs> veto = (_, args) => args.Cancel = true;
            EventHandler<WindowClosingEventArgs> hideAndVeto = (_, args) => { old.Hide(); args.Cancel = true; };
            EventHandler<WindowClosingEventArgs> fail = (_, _) => throw new InvalidOperationException("Synthetic close failure.");
            EventHandler<WindowClosingEventArgs> hideAndFail = (_, _) => { old.Hide(); throw new InvalidOperationException("Synthetic close failure after hide."); };
            var closingCalls = 0;
            var sameIdBlocked = false;
            EventHandler<WindowClosingEventArgs> reentrant = (_, args) =>
            {
                closingCalls++;
                if (closingCalls == 1)
                    try { update.Invoke(main, ["new", backend]); }
                    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
                args.Cancel = true;
            };
            EventHandler<WindowClosingEventArgs> sameIdReentrant = (_, args) =>
            {
                old.Hide();
                try { update.Invoke(main, ["old", backend]); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { sameIdBlocked = true; }
                args.Cancel = true;
            };
            if (mode == "default-close-canceled") old.Closing += veto;
            if (mode == "default-close-hidden-canceled") old.Closing += hideAndVeto;
            if (mode == "default-close-throw") old.Closing += fail;
            if (mode == "default-close-hidden-throw") old.Closing += hideAndFail;
            if (mode == "default-close-reentrant") old.Closing += reentrant;
            if (mode == "default-close-same-id-reentrant") old.Closing += sameIdReentrant;
            try
            {
                try { update.Invoke(main, ["new", backend]); }
                catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
                if (mode != "default-close-normal")
                {
                    var matching = lifetime.Windows.Count(w => w.IsVisible && w.Title == MacRemotePreviewReturnWindow.CaptureTitle);
                    var retained = ReferenceEquals(field.GetValue(main), old) && old.IsVisible && matching == 1;
                    var retainedHidden = ReferenceEquals(field.GetValue(main), old) && !old.IsVisible && matching == 0;
                    var fullyClosed = field.GetValue(main) is null && !old.IsVisible && matching == 0;
                    // Avalonia Headless may finish closing before propagating a
                    // Closing handler exception. That outcome is safe to retry.
                    if (!retained && !((mode is "default-close-hidden-canceled" or "default-close-hidden-throw" or "default-close-same-id-reentrant") && retainedHidden) && !((mode is "default-close-throw" or "default-close-hidden-throw") && fullyClosed))
                        throw new InvalidOperationException("A canceled or failed close lost a visible entry or duplicated the capture entry.");
                    if (mode == "default-close-reentrant" && closingCalls != 1)
                        throw new InvalidOperationException("A close callback re-entered the same close operation.");
                    if (mode == "default-close-same-id-reentrant" && !sameIdBlocked)
                        throw new InvalidOperationException("A close callback reopened the entry before closing settled.");
                    if (mode is "default-close-canceled" or "default-close-hidden-canceled" or "default-close-reentrant" or "default-close-same-id-reentrant")
                    {
                        old.Closing -= hideAndVeto;
                        old.Closing -= veto;
                        old.Closing -= reentrant;
                        old.Closing -= sameIdReentrant;
                        if (mode is "default-close-hidden-canceled" or "default-close-same-id-reentrant")
                        {
                            update.Invoke(main, ["old", backend]);
                            if (!old.IsVisible || !ReferenceEquals(field.GetValue(main), old))
                                throw new InvalidOperationException("The same known session did not restore its hidden entry.");
                        }
                        old.Closing += veto;
                        await ((Task)click.Invoke(old, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        if (backend.EndIds.Count != 1 || backend.EndIds[0] != "old" || !old.IsVisible || !ReferenceEquals(field.GetValue(main), old))
                            throw new InvalidOperationException("The retained entry lost its existing End attempt.");
                    }
                    old.Closing -= veto;
                    old.Closing -= hideAndVeto;
                    old.Closing -= fail;
                    old.Closing -= hideAndFail;
                    old.Closing -= reentrant;
                    old.Closing -= sameIdReentrant;
                    update.Invoke(main, ["new", backend]);
                }
                var next = field.GetValue(main) as MacRemotePreviewReturnWindow;
                if (next is null || ReferenceEquals(old, next) || old.IsVisible || !next.IsVisible || next.SessionId != "new" ||
                    lifetime.Windows.Count(w => w.IsVisible && w.Title == MacRemotePreviewReturnWindow.CaptureTitle) != 1)
                    throw new InvalidOperationException("A successful replacement must leave one visible entry for the new session.");
                close.Invoke(main, null);
                close.Invoke(main, null);
                if (field.GetValue(main) is not null || next.IsVisible)
                    throw new InvalidOperationException("Repeated close did not release a successfully closed entry.");
            }
            finally
            {
                old.Closing -= veto;
                old.Closing -= hideAndVeto;
                old.Closing -= fail;
                old.Closing -= hideAndFail;
                old.Closing -= reentrant;
                old.Closing -= sameIdReentrant;
                foreach (var window in lifetime.Windows.Where(w => w.Title == MacRemotePreviewReturnWindow.CaptureTitle).ToArray()) window.Close();
            }
#endif
        }

#if !NIAN_MAC_RETURN_RESEARCH
        private sealed class CloseOwnershipBackend : IDeskflowPreviewControl
        {
            public List<string> EndIds { get; } = [];
            public bool PreviewHandoffSupported => true;
            public string PreviewReturnShortcut => "fixture";
            public string PreviewHandoffHint => "fixture";
            public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) { EndIds.Add(id); return Task.FromResult(new DeskflowPreviewState()); }
            public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Begin.");
            public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Renew.");
            public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => throw new InvalidOperationException("Unexpected Read.");
        }
#endif
    }
}
