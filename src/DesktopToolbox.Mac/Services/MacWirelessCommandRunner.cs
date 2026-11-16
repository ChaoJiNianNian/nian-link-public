using System.Diagnostics;

namespace DesktopToolbox.Mac.Services;

internal enum MacWirelessCommand { ReadState, ReadKeepState, EnableKeep, DisableKeep }
internal sealed record MacWirelessCommandResult(int ExitCode, string Output = "", string Error = "", bool TimedOut = false, bool Cancelled = false, bool Unsupported = false, bool AuthorizationPending = false, bool? KeepEnabled = null);
internal interface IMacWirelessCommandRunner
{
    Task<MacWirelessCommandResult> RunAsync(MacWirelessCommand command, CancellationToken cancellationToken);
}

/// <summary>A fixed command whitelist; administrator authorization is requested only for explicit policy changes.</summary>
internal sealed class MacWirelessCommandRunner : IMacWirelessCommandRunner
{
    private Process? unfinishedAuthorization;

    internal static ProcessStartInfo CreateStartInfo(MacWirelessCommand command)
    {
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (command == MacWirelessCommand.ReadState)
        {
            start.FileName = "/sbin/ifconfig";
            start.ArgumentList.Add("awdl0");
        }
        else if (command == MacWirelessCommand.ReadKeepState)
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(MacWirelessKeepDefinition.ReadScript);
        }
        else
        {
            var script = command switch
            {
                MacWirelessCommand.EnableKeep => MacWirelessKeepDefinition.EnableScript,
                MacWirelessCommand.DisableKeep => MacWirelessKeepDefinition.DisableScript,
                _ => throw new ArgumentOutOfRangeException(nameof(command))
            };
            start.FileName = "/usr/bin/osascript";
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add($"do shell script {MacWirelessKeepDefinition.AppleScriptLiteral(script)} with administrator privileges");
        }
        return start;
    }

    public async Task<MacWirelessCommandResult> RunAsync(MacWirelessCommand command, CancellationToken cancellationToken)
    {
        var start = CreateStartInfo(command);
        if (cancellationToken.IsCancellationRequested) return new(-1, Cancelled: true);
        if (!OperatingSystem.IsMacOS()) return new(-1, Unsupported: true);
        if (unfinishedAuthorization is { } previous)
        {
            if (previous.HasExited) { previous.Dispose(); unfinishedAuthorization = null; }
            else if (!IsRead(command)) return new(-1, AuthorizationPending: true);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(IsRead(command) ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(60));
        var child = new Process { StartInfo = start };
        var started = false;
        var retained = false;
        Task<string>? output = null, errors = null;
        MacWirelessCommandResult result;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            started = child.Start();
            if (!started) result = new(-1, Error: "无法启动系统命令。");
            else
            {
                output = child.StandardOutput.ReadToEndAsync(deadline.Token);
                errors = child.StandardError.ReadToEndAsync(deadline.Token);
                await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                result = new(child.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        { result = new(-1, TimedOut: !cancellationToken.IsCancellationRequested, Cancelled: cancellationToken.IsCancellationRequested); }
        catch (Exception) { result = new(-1, Error: "无法执行系统命令。"); }
        finally
        {
            if (started && !child.HasExited && !await StopOwnedAsync(child).ConfigureAwait(false) && !IsRead(command))
            { unfinishedAuthorization = child; retained = true; }
            deadline.Cancel();
            if (output is not null && errors is not null)
                try { await Task.WhenAll(output, errors).ConfigureAwait(false); } catch (Exception) { /* Preserve the command outcome, not stream cancellation. */ }
            if (!retained) child.Dispose();
        }
        result = result with { AuthorizationPending = retained };
        return command == MacWirelessCommand.ReadKeepState ? MacWirelessKeepDefinition.ParseReadResult(result) : result;
    }

    private static bool IsRead(MacWirelessCommand command) => command is MacWirelessCommand.ReadState or MacWirelessCommand.ReadKeepState;

    private static async Task<bool> StopOwnedAsync(Process child)
    {
        // A privileged atomic ifconfig descendant may reject tree termination. The
        // caller still owns osascript and must attempt to close its native prompt.
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (Exception) { try { if (!child.HasExited) child.Kill(); } catch (Exception) { } }
        try { await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception) { return child.HasExited; }
        return true;
    }
}
