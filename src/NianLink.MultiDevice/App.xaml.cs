using System.IO;
using System.Windows;

namespace NianLink.MultiDevice;

public partial class App : Application
{
    internal static bool IsDiscoveryPortableBuild =>
#if DISCOVERY_PORTABLE
        true;
#else
        false;
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
#if DISCOVERY_PORTABLE
        var betaEntry = args.Length == 1 && args[0] == "--beta-portable";
        if (betaEntry && BetaPathPolicy.TryResolveLaunch(args, out var betaArgs)) args = betaArgs;
        if (!CandidatePathPolicy.IsDiscoveryLaunch(args) && !(betaEntry && BetaPathPolicy.IsLaunch(args)))
        {
            MessageBox.Show("测试版需要正确的专用资料目录、配套核心和启动参数；请使用对应测试包中的启动入口。", "年年互联 · 发现便携测试");
            Shutdown(); return;
        }
#endif
        // Explicit paths ensure a developer cannot accidentally run this candidate
        // against the installed application's profile or bundled keyboard core.
        var discoveryPortable = args.Length == 3 && args[2] == "--discovery-portable";
#if DISCOVERY_PORTABLE
        var betaPortable = args.Length == 3 && args[2] == "--beta-portable";
#else
        var betaPortable = false;
#endif
        if ((!discoveryPortable && !betaPortable && args.Length != 2) || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
        {
            MessageBox.Show("测试版需要指定独立资料目录和测试核心路径。", "年年互联 · 多设备测试");
            Shutdown(); return;
        }
        var root = Path.GetFullPath(args[0]);
        var engine = Path.GetFullPath(args[1]);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if ((discoveryPortable && !CandidatePathPolicy.IsDiscoveryProfile(root)) ||
            !CandidatePathPolicy.IsIsolated(root, Path.Combine(local, "NianLinkMultiDeviceCandidate")) && (discoveryPortable || betaPortable) ||
            !CandidatePathPolicy.IsIsolated(root, Path.Combine(local, "NianLinkStandalone")) ||
            !CandidatePathPolicy.IsIsolated(root, Path.Combine(local, "DesktopToolbox")))
        {
            MessageBox.Show("测试版不能使用正式版资料目录。", "年年互联 · 多设备测试");
            Shutdown(); return;
        }
#if DISCOVERY_PORTABLE
        if (!(betaPortable ? BetaPathPolicy.ClaimProfile(root) : CandidatePathPolicy.ClaimDiscoveryProfile(root)))
        {
            MessageBox.Show("发现测试资料目录已存在但不属于此包，或无法安全创建。", "年年互联 · 发现便携测试");
            Shutdown(); return;
        }
#endif
        MainWindow = new MainWindow(root, engine);
#if DISCOVERY_PORTABLE
        MainWindow.Title = "年年互联 · 发现便携测试";
#endif
        MainWindow.Show();
    }
}
