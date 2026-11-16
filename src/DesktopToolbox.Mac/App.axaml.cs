using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;
#if NATIVE_INPUT_ENGINE
using Nian.Input.Control;
using Nian.Input.Mac;
#endif

namespace DesktopToolbox.Mac;

public sealed partial class App : Application
{
    public override void Initialize()
    {
#if FILELESS_PAIRING_CANDIDATE
        Name = "年年互联·免文件测试";
#elif DISCOVERY_CANDIDATE
        Name = "年年互联·发现测试";
#elif MULTI_DEVICE_CANDIDATE
        Name = "年年互联·多设备测试";
#elif NATIVE_INPUT_CANDIDATE
        Name = MacWindowPresentation.WindowTitle(true);
#elif PREVIEW_CONTROL_CANDIDATE
        Name = "年年互联·预览操作测试";
#endif
        AvaloniaXamlLoader.Load(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = Program.StartupOptions;
            var legacyData=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"NianDesktopToolbox","DeskLink");
            var productionData=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"NianLink","DeskLink");
            string data;
#if NATIVE_INPUT_CANDIDATE || PREVIEW_CONTROL_CANDIDATE
            try{data=NativeCandidateProfile.Require(options.DataDirectory,legacyData);}
            catch(Exception failure) when(failure is ArgumentException or IOException or UnauthorizedAccessException)
            {ScheduleStartupFailure(desktop,failure);base.OnFrameworkInitializationCompleted();return;}
#else
            try{data=NativeCandidateProfile.Require(options.DataDirectory??productionData,legacyData);}
            catch(Exception failure) when(failure is ArgumentException or IOException or UnauthorizedAccessException)
            {ScheduleStartupFailure(desktop,failure);base.OnFrameworkInitializationCompleted();return;}
#endif
#if MULTI_DEVICE_CANDIDATE
#if FILELESS_PAIRING_CANDIDATE
            var identityVault = Nian.Input.Security.MacKeychainIdentityVault.CreateFilelessCandidate();
#elif DISCOVERY_CANDIDATE
            var identityVault = Nian.Input.Security.MacKeychainIdentityVault.CreateDiscoveryCandidate();
#else
            var identityVault = new Nian.Input.Security.MacKeychainIdentityVault();
#endif
            var multiWindow = new MacMultiDeviceWindow(data, options.StartupCheck, identityVault);
            desktop.MainWindow = multiWindow;
#else
#if PREVIEW_CONTROL_ENGINE
            var service=new MacLinkService(data);
#if PREVIEW_CONTROL_CANDIDATE
            const int dataPort=24953;
#else
            const int dataPort=24802;
#endif
            var collaboration=options.StartupCheck?null:new MacCollaborationService(data,service.GetApprovedPairingAsync,null,dataPort,null,null,service);
            if(collaboration is not null)service.Changed+=_=>collaboration.NotifyPreviewControlChanged();
            var window=new MainWindow(service,new StandaloneWirelessUnavailable(),collaboration){IsEnabled=!options.StartupCheck};
#elif NATIVE_INPUT_ENGINE
            Dispatcher.UIThread.VerifyAccess();
            var nativeSurface=new NativeSurfaceSnapshot();
            var ownUiPolicy=new MacDesktopOwnUiPolicy(()=>nativeSurface.ProtectsOwnSurface,nativeSurface.IsProtectedWindow,
                (bundle,pid)=>NativeSurfaceSnapshot.ExcludesPreviewOwner(bundle,pid,Environment.ProcessId));
            var sharedArbiter=new DesktopControlArbiter();
            MacDesktopLocalEventGuard? guard=null;MacDesktopInputTarget? target=null;
            MacNativeLinkService service;
            MainWindow window;
            try
            {
                guard=MacDesktopLocalEventGuard.Install(ownUiPolicy);target=new MacDesktopInputTarget(ownUiPolicy);
                service=new MacNativeLinkService(data,target,guard,sharedArbiter,24932);
                var collaboration=options.StartupCheck?null:new MacCollaborationService(data,service.GetApprovedPairingAsync,null,24802,null,service);
                window=new MainWindow(service,new StandaloneWirelessUnavailable(),collaboration,nativeSurface){IsEnabled=!options.StartupCheck};
            }
            catch(Exception failure)
            {
                Exception reportedFailure=failure;
                try
                {
                    nativeSurface.Protect();try{target?.Dispose();}finally{try{guard?.Dispose();}finally{sharedArbiter.Dispose();}}
                }
                catch(Exception cleanupFailure){reportedFailure=new AggregateException("原生初始化失败，资源释放也未完成。",failure,cleanupFailure);}
                ScheduleStartupFailure(desktop,reportedFailure);base.OnFrameworkInitializationCompleted();return;
            }
            window.Closed+=(_,_)=>
            {
                Dispatcher.UIThread.VerifyAccess();nativeSurface.Protect();
                try{target.Dispose();}finally{try{guard.Dispose();}finally{sharedArbiter.Dispose();}}
            };
#else
            var service = new MacLinkService(data);
            var collaboration = options.StartupCheck ? null : new MacCollaborationService(data, service.GetApprovedPairingAsync);
            var window = new MainWindow(service, new StandaloneWirelessUnavailable(), collaboration) { IsEnabled = !options.StartupCheck };
#endif
            if (!options.StartupCheck) desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = window;
            if (!options.StartupCheck && this.TryGetFeature<IActivatableLifetime>() is { } activation)
                activation.Activated += (_, _) => { if (!window.IsVisible) { window.Show(); window.Activate(); } };
            if (options.StartupCheck)
                window.Opened += async (_, _) =>
                {
                    try { await Task.Delay(800); await MacStartupCheck.WriteAsync(data, window, service); }
                    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                    finally { window.Close(); }
                };
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void ScheduleStartupFailure(IClassicDesktopStyleApplicationLifetime desktop,Exception failure)=>
        MacStartupFailure.Schedule(failure,Console.Error.WriteLine,code=>Environment.ExitCode=code,
            action=>Dispatcher.UIThread.Post(action),desktop.Shutdown);
}
