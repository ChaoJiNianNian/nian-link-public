using System.Text.Json;
using System.Windows;
using DesktopToolbox.App.DeskLink;
using DesktopToolbox.App.Hosting;
using DesktopToolbox.Core;
using DesktopToolbox.Windows.DeskSwitch;
using Forms = System.Windows.Forms;

namespace NianLink.Windows;

internal sealed class StandaloneApplication : System.Windows.Application
{
    private SingleInstance? instance;
    private DataDirectoryLease? lease;
    private DeskSwitchModule? module;
    private WindowsCollaborationHost? collaboration;
    private MiniCollaborationWindow? mini;
    private TargetReturnButtonWindow? returnWindow;
    private HostController? returnHost;
    private IIntegratedDeskSwitchService? keyboard;
    private SetupWindow? setup;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private DeskSwitchSettings names = new();
    private readonly ShutdownCoordinator shutdown = new();
    private bool exiting;

    [STAThread] public static void Main() => new StandaloneApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown }.Run();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NianLinkStandalone");
            var paths = new AppPaths(root);
            instance = new SingleInstance(root);
            if (!instance.IsOwner) { await instance.ActivateExistingAsync(); await instance.DisposeAsync(); Shutdown(); return; }
            lease = paths.AcquireDataDirectory(createIfMissing: true);
            var store = new JsonFileStore(paths);
            names = (await store.LoadAsync("desk-switch.json", () => new DeskSwitchSettings())).Validate();
            var prompts = new NativePrompts(Dispatcher, () => setup);
            var context = new ModuleContext(paths, store, prompts, Publish);
            var bridgePath = Path.Combine(AppContext.BaseDirectory, "DisplayBridge.exe");
            var bridge = new DisplayBridgeProcess(bridgePath);
            keyboard = new IntegratedDeskSwitchService(context, Path.Combine(AppContext.BaseDirectory, "DeskflowIntegrated"), bridge);
            module = new DeskSwitchModule(context, Path.Combine(AppContext.BaseDirectory, "Deskflow"), bridgePath, bridge, keyboard);
            returnHost = new HostController(keyboard, () => names.LocalName, Log);
            collaboration = new WindowsCollaborationHost(context, keyboard, Dispatcher, Log);
            await collaboration.InitializeAsync();
            mini = new MiniCollaborationWindow(collaboration, store, Log, Path.Combine(root, "webview-profile"));
            setup = new SetupWindow(module, keyboard, bridge, names, SaveNames, () => collaboration.ReloadDeviceNamesAsync(), () => mini.ShowMiniAsync(), ExitAsync);
            mini.ConnectionSettingsRequested += () =>
            {
                if (setup is null || exiting) throw new InvalidOperationException("连接设置尚未就绪，请稍后重试。");
                if (setup.WindowState == WindowState.Minimized) setup.WindowState = WindowState.Normal;
                setup.Show(); setup.Activate();
            };
            collaboration.CoordinateKeyboardStartAsync = async (token, start) =>
            {
                token.ThrowIfCancellationRequested();
                await Dispatcher.InvokeAsync(() => setup is not null && !exiting
                    ? setup.StartFromMiniAsync(start, token) : Task.FromException(new InvalidOperationException("连接设置尚未就绪，请稍后重试。"))).Task.Unwrap();
                token.ThrowIfCancellationRequested();
            };
            await mini.StartAsync();
            SetupTray();
            instance.Listen(() => Dispatcher.BeginInvoke(() => { if (setup is not null) { setup.Show(); setup.Activate(); } }));
            setup.Show();
        }
        catch (Exception error) { Log(error); System.Windows.MessageBox.Show(error.Message, "年年互联启动失败"); await ExitAsync(); }
    }

    private void SaveNames(DeskSwitchSettings value) => names = value;
    private void Log(Exception error) => System.Diagnostics.Trace.TraceError(error.ToString());
    private void Publish(string name, object? data)
    {
        if (name != "deskSwitch.sessionChanged" || exiting) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (exiting || returnHost is null) return;
            setup?.RefreshStatus();
            var id = TargetReturnButtonPolicy.SessionId(returnHost.CurrentDeskSwitch, returnHost.DeskLocalName);
            if (id is null) { returnWindow?.Close(); returnWindow = null; return; }
            returnWindow ??= new TargetReturnButtonWindow(returnHost);
            returnWindow.UpdateSession(id);
        });
    }
    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("连接设置", null, (_, _) => Dispatcher.BeginInvoke(() => { setup?.Show(); setup?.Activate(); }));
        menu.Items.Add("打开互联小窗", null, (_, _) => Dispatcher.BeginInvoke(async () => { if (mini is not null) await mini.ShowMiniAsync(); }));
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        var executable = Path.Combine(AppContext.BaseDirectory, "NianLink.exe");
        try { trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(executable); }
        catch (Exception error) when (error is ArgumentException or IOException or System.ComponentModel.Win32Exception) { Log(error); }
        trayIcon ??= (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        tray = new Forms.NotifyIcon { Text = "年年互联", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
    }
    private async Task ExitAsync()
    {
        if (exiting) return; exiting = true;
        var keyboardService = keyboard;
        async Task<ShutdownPreparation> Prepare() => await shutdown.PrepareAsync(
            () => keyboardService?.IsRunning == true,
            async () => keyboardService is null ? null : (await keyboardService.StopAsync(CancellationToken.None)).LastError);
        ShutdownPreparation ready;
        try { ready = setup is null ? await Prepare() : await setup.PrepareShutdownAsync(Prepare); }
        catch (Exception error) { ready = new(false, false, "退出准备失败：" + error.Message); }
        if (ready.Busy) { exiting = false; return; }
        if (!ready.CanExit)
        {
            exiting = false;
            if (setup is not null) { setup.Show(); setup.Activate(); }
            System.Windows.MessageBox.Show(ready.Error ?? "共享尚未停止，请重试。", "无法退出年年互联", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        setup?.PermitClose();
        tray?.Dispose(); trayIcon?.Dispose(); returnWindow?.Close();
        try { if (mini is not null) await mini.StopAsync(); }
        finally
        {
            try { if (collaboration is not null) await collaboration.DisposeAsync(); }
            finally { if (module is not null) await module.DisposeAsync(); lease?.Dispose(); if (instance is not null) await instance.DisposeAsync(); Shutdown(); }
        }
    }
}
