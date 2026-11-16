using System.ComponentModel;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.DeskLink;
using NianLink.MultiDevice;

internal static partial class Program
{
    private static async Task VerifyCloseOwnershipAsync(MainWindow main, string mode, string resultPath)
    {
        if (mode is not ("close-ownership-status-failure" or "close-ownership-cancel" or "close-ownership-hidden" or
            "close-ownership-reentrant" or "close-ownership-reentrant-same" or "close-ownership-button" or "close-ownership-normal" or "close-ownership-show-error"))
            throw new ArgumentException("Unknown close ownership scenario.", nameof(mode));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var type = typeof(MainWindow);
        var apply = type.GetMethod("ApplyTargetReturn", flags)!;
        var field = type.GetField("targetReturn", flags)!;
        var endIds = new List<string>();
        var endFails = mode != "close-ownership-button";
        Func<string, Task<DeskflowPreviewState>> end = id =>
        {
            endIds.Add(id);
            return endFails ? Task.FromException<DeskflowPreviewState>(new IOException("synthetic end failure")) : Task.FromResult(new DeskflowPreviewState());
        };
        DeskflowPreviewState State(string id) => new() { Phase = DeskflowPreviewPhase.Active, Id = id, TargetComputer = "peer" };
        void Apply(string id) => apply.Invoke(main, [State(id), "peer", end]);
        void TryApply(string id)
        {
            try { Apply(id); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
        }
        Window? Entry() => field.GetValue(main) as Window;
        int VisibleEntries() => Application.Current.Windows.OfType<Window>().Count(w => w.GetType().Name == "PreviewReturnWindow" && w.IsVisible);

        if (mode == "close-ownership-show-error")
        {
            var windowType = type.Assembly.GetType("NianLink.MultiDevice.PreviewReturnWindow", true)!;
            Func<string, Task> action = id => { endIds.Add(id); return Task.FromException(new IOException("synthetic end failure")); };
            var failed = (Window)Activator.CreateInstance(windowType, "old", action)!;
            field.SetValue(main, failed);
            failed.Closed += (_, _) => { if (ReferenceEquals(Entry(), failed)) field.SetValue(main, null); };
            EventHandler inject = (_, _) => throw new InvalidOperationException("Synthetic SourceInitialized failure.");
            failed.SourceInitialized += inject;
            Exception? original = null;
            try { failed.Show(); } catch (Exception error) { original = error; }
            failed.SourceInitialized -= inject;
            if (original is null || !original.Message.Contains("Synthetic SourceInitialized"))
                throw new InvalidOperationException("The synthetic initialization exception did not occur.");
            if (!ReferenceEquals(Entry(), failed) || VisibleEntries() > 1)
                throw new InvalidOperationException("Failed Show lost window ownership.");
            Apply("old");
            if (!ReferenceEquals(Entry(), failed) || !failed.IsVisible || VisibleEntries() != 1)
                throw new InvalidOperationException("Same-session update could not restore the failed Show.");
            await ((Task)failed.GetType().GetMethod("ReturnAsync", flags)!.Invoke(failed, null)!).WaitAsync(TimeSpan.FromSeconds(3));
            if (endIds.Count != 1 || endIds[0] != "old")
                throw new InvalidOperationException("Original End is unavailable after failed Show.");
            failed.Close();
            if (Entry() is not null) throw new InvalidOperationException("Retry entry did not close.");
            return;
        }
        Apply("old");
        var old = Entry() ?? throw new InvalidOperationException("Missing original return window.");
        if (!old.IsVisible) throw new InvalidOperationException("Original return window is not visible.");
        var oldClosed = false;
        old.Closed += (_, _) => oldClosed = true;
        if (mode == "close-ownership-normal")
        {
            Apply("new");
            if (!oldClosed || Entry() is not Window { IsVisible: true } next || ReferenceEquals(old, next) || VisibleEntries() != 1)
                throw new InvalidOperationException("Normal replacement did not close the old window and show exactly one new entry.");
            next.Close();
            return;
        }
        CancelEventHandler? handler = null;
        var reentered = false;
        handler = (_, e) =>
        {
            if (mode is "close-ownership-hidden" or "close-ownership-reentrant-same") old.Hide();
            if (mode is "close-ownership-reentrant" or "close-ownership-reentrant-same")
            {
                if (reentered) throw new InvalidOperationException("Unexpected recursive close.");
                reentered = true;
                TryApply(mode == "close-ownership-reentrant-same" ? "old" : "new");
                if (mode == "close-ownership-reentrant-same" && old.IsVisible)
                    throw new InvalidOperationException("Window was shown while its close callback was running.");
            }
            e.Cancel = true;
        };
        old.Closing += handler;
        try
        {
            if (mode == "close-ownership-status-failure")
            {
                var root = Path.Combine(Path.GetDirectoryName(resultPath)!, "status-vault-" + Guid.NewGuid().ToString("N"));
                await using var vault = await MultiDeviceCredentialVault.OpenAsync(root, new SyntheticProtector());
                var runtime = new MultiPeerRuntime(vault, Path.Combine(root, "runtime"), "host", IPAddress.Loopback, 0, 0,
                    _ => throw new InvalidOperationException("The synthetic keyboard must not start."));
                await runtime.DisposeAsync();
                var hostField = type.GetField("host", flags)!;
                hostField.SetValue(main, runtime);
                try
                {
                    await ((Task)type.GetMethod("RefreshStatusAsync", flags)!.Invoke(main, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                    if (!ReferenceEquals(Entry(), old) || !old.IsVisible || VisibleEntries() != 1)
                        throw new InvalidOperationException("Status failure lost the return entry.");
                    var feedback = (TextBlock)main.FindName("Feedback");
                    if (!feedback.Text.Contains("归还窗口") || !feedback.Text.Contains("检查键鼠"))
                        throw new InvalidOperationException("Status failure did not explain the unclosed return entry.");
                }
                finally { hostField.SetValue(main, null); }
            }
            else if (mode == "close-ownership-button")
            {
                await ((Task)old.GetType().GetMethod("ReturnAsync", flags)!.Invoke(old, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                var button = (Button)old.GetType().GetField("button", flags)!.GetValue(old)!;
                if (!old.IsVisible || !ReferenceEquals(Entry(), old) || (button.Content as string ?? "").Contains("正在归还"))
                    throw new InvalidOperationException("Canceled close left a misleading return-in-progress label.");
                if (endIds.Count != 1 || endIds[0] != "old") throw new InvalidOperationException("The original return was not attempted once.");
            }
            else
            {
                TryApply("new");
                if (!ReferenceEquals(Entry(), old) || VisibleEntries() > 1)
                    throw new InvalidOperationException("Canceled close lost ownership or created a duplicate return window.");
                if (mode is "close-ownership-hidden" or "close-ownership-reentrant-same")
                {
                    if (old.IsVisible) throw new InvalidOperationException("The test close did not hide the old window.");
                    Apply("old");
                    if (!old.IsVisible || !ReferenceEquals(Entry(), old)) throw new InvalidOperationException("Same-session observation did not restore the hidden entry.");
                }
                await ((Task)old.GetType().GetMethod("ReturnAsync", flags)!.Invoke(old, null)!).WaitAsync(TimeSpan.FromSeconds(3));
                if (endIds.Count != 1 || endIds[0] != "old" || !ReferenceEquals(Entry(), old))
                    throw new InvalidOperationException("The retained window lost its original return attempt.");
            }
            old.Closing -= handler;
            Apply("new");
            var next = Entry();
            if (next is null || ReferenceEquals(old, next) || old.IsVisible || !next.IsVisible || VisibleEntries() != 1)
                throw new InvalidOperationException("Successful retry did not leave exactly one replacement window.");
        }
        finally
        {
            old.Closing -= handler;
            foreach (Window candidate in Application.Current.Windows.OfType<Window>().Where(w => w.GetType().Name == "PreviewReturnWindow").ToArray()) candidate.Close();
        }
    }

    private sealed class SyntheticProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }
}
