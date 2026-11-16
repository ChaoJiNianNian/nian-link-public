using System.Diagnostics;
using System.IO;
using DesktopToolbox.Windows.DeskSwitch;

namespace MultiDevice.Windows.Tests;

public sealed class SafeDisplayBridgeProcessTests
{
    [Fact]
    public async Task CancellationConfirmsTheOwnedChildHasExitedBeforeReturning()
    {
        var root = Path.Combine(Path.GetTempPath(), "nian-display-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "wait.ps1"); await File.WriteAllTextAsync(script, "Start-Sleep -Seconds 30");
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var bridge = new SafeDisplayBridgeProcess(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script], TimeSpan.FromSeconds(20));
            using var cancel = new CancellationTokenSource();
            var work = bridge.ListAsync(cancel.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (bridge.OwnedChildId is null) await Task.Delay(20, timeout.Token);
            using var child = Process.GetProcessById(bridge.OwnedChildId!.Value);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ListAsync(CancellationToken.None));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.True(child.HasExited);
            Assert.False(bridge.HasUnfinishedChild);
        }
        finally { File.Delete(script); Directory.Delete(root); }
    }
}
