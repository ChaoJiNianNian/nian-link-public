using System.Xml;
using System.Xml.Linq;
using DesktopToolbox.Mac.Services;

namespace DesktopToolbox.Mac.Tests;

public sealed class MacWirelessKeepRunnerTests
{
    [Fact]
    public void EnableInstallsPersistentLaunchdPolicyInsteadOfOneShotIfconfig()
    {
        var start = MacWirelessCommandRunner.CreateStartInfo(MacWirelessCommand.EnableKeep);
        Assert.Equal("/usr/bin/osascript", start.FileName);
        Assert.Equal("-e", start.ArgumentList[0]);
        Assert.Contains("bootstrap", start.ArgumentList[1]);
        Assert.Contains(MacWirelessKeepDefinition.PlistPath, start.ArgumentList[1]);
        Assert.Contains("with administrator privileges", start.ArgumentList[1]);
        Assert.DoesNotContain("sudo", start.ArgumentList[1]);
    }

    [Fact]
    public void ReadPolicyNeverRequestsAdministratorAuthorization()
    {
        var start = MacWirelessCommandRunner.CreateStartInfo(MacWirelessCommand.ReadKeepState);
        Assert.Equal("/bin/sh", start.FileName);
        Assert.Equal("-c", start.ArgumentList[0]);
        Assert.DoesNotContain("administrator privileges", string.Join(" ", start.ArgumentList));
        Assert.DoesNotContain("launchctl enable", start.ArgumentList[1]);
        Assert.DoesNotContain("launchctl bootstrap", start.ArgumentList[1]);
        Assert.DoesNotContain("launchctl disable", start.ArgumentList[1]);
    }

    [Fact]
    public void SystemJobExecutesOnlyTheFixedAppleBinaryAndPersistsAcrossBoot()
    {
        using var reader = XmlReader.Create(new StringReader(MacWirelessKeepDefinition.Plist), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var children = XDocument.Load(reader).Root!.Element("dict")!.Elements().ToArray();
        var values = Enumerable.Range(0, children.Length / 2).ToDictionary(i => children[2 * i].Value, i => children[2 * i + 1]);
        Assert.Equal(MacWirelessKeepDefinition.Label, values["Label"].Value);
        Assert.Equal(new[] { "/sbin/ifconfig", "awdl0", "down" }, values["ProgramArguments"].Elements("string").Select(n => n.Value));
        Assert.Equal("true", values["RunAtLoad"].Name.LocalName);
        Assert.Equal("3", values["StartInterval"].Value);
        Assert.Equal("1", values["ThrottleInterval"].Value);
        Assert.False(values.ContainsKey("KeepAlive"));
        Assert.Equal(5, values.Count);
    }

    [Theory]
    [InlineData("NIAN_WIRELESS_KEEP_V1:1:1", true, false)]
    [InlineData("NIAN_WIRELESS_KEEP_V1:0:0", false, false)]
    [InlineData("NIAN_WIRELESS_KEEP_V1:1:0", true, true)]
    [InlineData("NIAN_WIRELESS_KEEP_V1:0:1", true, true)]
    public void PolicyChoiceAndExecutionDisagreementAreNotMistakenForOff(string output, bool expected, bool hasError)
    {
        var result = MacWirelessKeepDefinition.ParseReadResult(new(0, output));
        Assert.Equal(expected, result.KeepEnabled);
        Assert.Equal(hasError, result.Error.Length != 0);
    }

    [Theory]
    [InlineData(73, "configuration conflict")]
    [InlineData(1, "permission denied")]
    [InlineData(5, "unavailable launchd domain")]
    public void UntrustedOrUnknownReadFailureNeverBecomesDisabled(int exitCode, string error)
    {
        var result = MacWirelessKeepDefinition.ParseReadResult(new(exitCode, "", error));
        Assert.Null(result.KeepEnabled);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain(error, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NIAN_WIRELESS_KEEP_V1:1:1\nNIAN_WIRELESS_KEEP_V1:0:0")]
    [InlineData("NIAN_WIRELESS_KEEP_V1:2:0")]
    [InlineData("some other successful command")]
    public void UnexpectedReadProtocolRemainsUnknown(string output)
    {
        var result = MacWirelessKeepDefinition.ParseReadResult(new(0, output));
        Assert.Null(result.KeepEnabled);
        Assert.NotEmpty(result.Error);
    }

    [Fact]
    public void InterruptedReadPreservesCancellationAndTimeout()
    {
        var cancelled = new MacWirelessCommandResult(-1, Cancelled: true);
        var timedOut = new MacWirelessCommandResult(-1, TimedOut: true);
        Assert.Equal(cancelled, MacWirelessKeepDefinition.ParseReadResult(cancelled));
        Assert.Equal(timedOut, MacWirelessKeepDefinition.ParseReadResult(timedOut));
    }

    [Fact]
    public void PrivilegedTransactionsRejectSymlinksAndProtectRootFiles()
    {
        foreach (var script in new[] { MacWirelessKeepDefinition.ReadScript, MacWirelessKeepDefinition.EnableScript, MacWirelessKeepDefinition.DisableScript })
        {
            Assert.Contains("safe_dir /Library\nsafe_dir /Library/LaunchDaemons", script);
            Assert.Contains("[ ! -L \"$plist\" ] && [ -f \"$plist\" ]", script);
            Assert.Contains("= '0:0:644'", script);
            Assert.Contains("/usr/bin/cmp -s \"$plist\" -", script);
            Assert.Contains("113) loaded=0", script);
            Assert.Contains("*) fail 'Cannot verify wireless system job.'", script);
        }
        Assert.Contains("/usr/bin/mktemp '/Library/LaunchDaemons/", MacWirelessKeepDefinition.EnableScript);
        Assert.Contains("/bin/mv -f \"$temporary\" \"$plist\"", MacWirelessKeepDefinition.EnableScript);
        Assert.DoesNotContain("/tmp/", MacWirelessKeepDefinition.EnableScript);
        Assert.DoesNotContain("rm -r", MacWirelessKeepDefinition.DisableScript);
    }

    [Fact]
    public void DisableStopsAndVerifiesExactJobBeforeDeletingAndRestoringAwdl()
    {
        var script = MacWirelessKeepDefinition.DisableScript;
        var disable = script.IndexOf("/bin/launchctl disable \"$service\"", StringComparison.Ordinal);
        var bootout = script.IndexOf("/bin/launchctl bootout \"$service\"", StringComparison.Ordinal);
        var verify = script.IndexOf("[ \"$loaded\" -eq 0 ] || fail 'Wireless system job is still loaded.'", StringComparison.Ordinal);
        var remove = script.IndexOf("/bin/rm -f \"$plist\"", StringComparison.Ordinal);
        var restore = script.IndexOf("/sbin/ifconfig awdl0 up", StringComparison.Ordinal);
        Assert.True(disable < bootout && bootout < verify && verify < remove && remove < restore);
        Assert.DoesNotContain("bootout system\n", script);
    }

    [Fact]
    public void MutationsAcquireKernelTransactionLockBeforePolicySnapshot()
    {
        foreach (var script in new[] { MacWirelessKeepDefinition.EnableScript, MacWirelessKeepDefinition.DisableScript })
        {
            var acquire = script.IndexOf("/usr/bin/lockf -s -t 5 9", StringComparison.Ordinal);
            var snapshot = script.LastIndexOf("read_policy\n", StringComparison.Ordinal);
            Assert.True(acquire >= 0 && acquire < snapshot);
            Assert.Contains("exec 9<>\"$lock\"", script);
            Assert.Contains("= '0:0:600'", script);
            Assert.DoesNotContain("rm -f \"$lock\"", script);
        }
        Assert.DoesNotContain("exec 9<>", MacWirelessKeepDefinition.ReadScript);
    }

    [Fact]
    public void PersistentlyDisabledJobIsAnErrorWhileKeepingTheSavedChoice()
    {
        var result = MacWirelessKeepDefinition.ParseReadResult(new(0, "NIAN_WIRELESS_KEEP_V1:disabled"));
        Assert.True(result.KeepEnabled);
        Assert.Contains("停用", result.Error);
    }
    [Fact]
    public void AppleScriptLiteralEscapesOnlySyntaxAndPreservesTheFixedShellText()
    {
        Assert.Equal("\"a\\\"b\\\\c\\nd\\re\"", MacWirelessKeepDefinition.AppleScriptLiteral("a\"b\\c\nd\re"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MacWirelessCommandRunner.CreateStartInfo((MacWirelessCommand)99));
    }
}