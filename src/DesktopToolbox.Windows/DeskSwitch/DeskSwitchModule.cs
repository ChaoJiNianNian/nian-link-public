using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DesktopToolbox.Core;

namespace DesktopToolbox.Windows.DeskSwitch;

public sealed class DeskSwitchModule : IToolModule
{
    private readonly ModuleContext context;
    private readonly DeskflowEngine engine;
    private readonly IDisplayBridge displayBridge;
    private readonly IIntegratedDeskSwitchService integrated;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeskSwitchSettings? settings;
    private readonly string directory;
    private readonly MacInstallerExport macInstaller;
    private readonly PairingFileExport pairingExport;
    public IReadOnlyCollection<string> Methods { get; } =
        ["deskSwitch.status", "deskSwitch.save", "deskSwitch.monitors", "deskSwitch.prepare", "deskSwitch.openEngine", "deskSwitch.openFolder", "deskSwitch.testInput", "deskSwitch.openMacDownload", "deskSwitch.start", "deskSwitch.stop", "deskSwitch.session", "deskSwitch.switch", "deskSwitch.confirmPeer", "deskSwitch.rejectPeer", "deskSwitch.exportPairing", "deskSwitch.revealPairing", "deskSwitch.exportMacInstaller"];

    public DeskSwitchModule(ModuleContext context, string packagedDirectory, string bridgeExecutable, IDisplayBridge? displayBridge = null, IIntegratedDeskSwitchService? integrated = null)
    {
        this.context = context;
        directory = context.Paths.Resolve("desk-switch");
        macInstaller = new MacInstallerExport(Path.Combine(Path.GetDirectoryName(packagedDirectory)!, "MacClient"), context.Prompts);
        engine = new DeskflowEngine(packagedDirectory, directory, bridgeExecutable, context.Paths.Resolve("desk-switch.json"));
        this.displayBridge = displayBridge ?? new DisplayBridgeProcess(bridgeExecutable);
        this.integrated = integrated ?? new IntegratedDeskSwitchService(context, Path.Combine(Path.GetDirectoryName(packagedDirectory)!, "DeskflowIntegrated"), this.displayBridge);
        pairingExport = new PairingFileExport(context.Prompts, this.integrated.ExportPairingAsync);
    }

    public async Task<object?> InvokeAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        if (!Methods.Contains(method)) throw new ArgumentException("未知的电脑切换操作。");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (integrated.Snapshot.Backend == DeskSwitchBackend.Native && (method is "deskSwitch.prepare" or "deskSwitch.openEngine" or "deskSwitch.openMacDownload"))
                throw new InvalidOperationException("自研键鼠共享不提供旧版引擎操作。请在工具箱内启动共享并导出 Mac 配对文件。");
            settings ??= (await context.Store.LoadAsync("desk-switch.json", () => new DeskSwitchSettings(), cancellationToken)).Validate();
            switch (method)
            {
                case "deskSwitch.session":
                    return await integrated.RefreshAsync(cancellationToken);
                case "deskSwitch.start":
                    await context.Store.SaveAsync("desk-switch.json", settings, cancellationToken);
                    await integrated.StartAsync(settings, cancellationToken);
                    return Status();
                case "deskSwitch.stop":
                    await integrated.StopAsync(cancellationToken);
                    return Status();
                case "deskSwitch.switch":
                {
                    var target = parameters.GetProperty("target").GetString();
                    if (target is not ("local" or "remote")) throw new ArgumentException("只能切到 Windows 或 Mac。");
                    return await integrated.SwitchAsync(target, cancellationToken);
                }
                case "deskSwitch.confirmPeer":
                {
                    var fingerprint = parameters.GetProperty("fingerprint").GetString();
                    if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 256) throw new ArgumentException("待核对的指纹无效。");
                    if (!await context.Prompts.ConfirmAsync("核对 Mac 的安全指纹", "请对照 Mac 工具箱显示的本机键鼠指纹。\n\n" + fingerprint + "\n\n只有完全一致且是你自己的 Mac 时才确认信任。"))
                        return new { canceled = true, message = "尚未信任这台设备。" };
                    await integrated.ConfirmPeerAsync(fingerprint, cancellationToken);
                    return new { canceled = false, message = "已保存你确认的设备指纹，正在等待连接重试。" };
                }
                case "deskSwitch.rejectPeer":
                    await integrated.RejectPeerAsync(cancellationToken);
                    return new { message = "已拒绝该设备并停止本次共享。" };
                case "deskSwitch.exportPairing":
                    return await pairingExport.ExportAsync(cancellationToken);
                case "deskSwitch.revealPairing":
                    return await pairingExport.RevealAsync();
                case "deskSwitch.exportMacInstaller":
                    return await macInstaller.ExportAsync(cancellationToken);
                case "deskSwitch.status": return Status();
                case "deskSwitch.monitors": return await displayBridge.ListAsync(cancellationToken);
                case "deskSwitch.save":
                {
                    if (integrated.IsRunning || integrated.Snapshot.Backend != DeskSwitchBackend.Native && engine.IsRunning) throw new InvalidOperationException("请先停止正在运行的共享，再修改连接设置。");
                    var next = parameters.GetProperty("settings").Deserialize<DeskSwitchSettings>(JsonFileStore.JsonOptions)
                        ?? throw new ArgumentException("连接设置不能为空。");
                    next.Validate();
                    await context.Store.SaveAsync("desk-switch.json", next, cancellationToken);
                    settings = next;
                    var status = Status();
                    context.Emit("deskSwitch.changed", status);
                    return status;
                }
                case "deskSwitch.prepare":
                    if (integrated.IsRunning) throw new InvalidOperationException("请先在工具箱停止共享，再使用旧版配对诊断。");
                    // Persist even default settings before exposing a generated hook that will read this file.
                    await context.Store.SaveAsync("desk-switch.json", settings, cancellationToken);
                    var prepared = await engine.PrepareAsync(settings, ServerAddress(), cancellationToken);
                    await DeskSwitchPairingGuide.WriteAsync(directory, cancellationToken);
                    return prepared;
                case "deskSwitch.openEngine":
                    if (integrated.IsRunning) throw new InvalidOperationException("工具箱正在管理共享，请先停止后再打开旧版 Deskflow。");
                    await context.Store.SaveAsync("desk-switch.json", settings, cancellationToken);
                    var opened = await engine.OpenAsync(settings, ServerAddress(), cancellationToken);
                    await DeskSwitchPairingGuide.WriteAsync(directory, cancellationToken);
                    return new { message = opened };
                case "deskSwitch.openFolder":
                    if (!Directory.Exists(directory)) throw new InvalidOperationException("请先生成配对文件。");
                    await context.Prompts.RevealAsync(directory);
                    return new { message = "已打开配对目录。" };
                case "deskSwitch.testInput": return await TestInputAsync(parameters, cancellationToken);
                case "deskSwitch.openMacDownload":
                {
                    var architecture = parameters.GetProperty("architecture").GetString();
                    var file = architecture switch { "arm64" => "deskflow-1.27.0-macos-arm64.dmg", "x64" => "deskflow-1.27.0-macos-x86_64.dmg", _ => throw new ArgumentException("请选择 Apple 芯片或 Intel Mac。") };
                    cancellationToken.ThrowIfCancellationRequested();
                    using var process = Process.Start(new ProcessStartInfo("https://github.com/deskflow/deskflow/releases/download/v1.27.0/" + file) { UseShellExecute = true });
                    return new { message = "已在浏览器打开 Deskflow 官方 Mac 下载。" };
                }
                default: throw new ArgumentException("此操作不可用。");
            }
        }
        finally { gate.Release(); }
    }

    private object Status()
    {
        var session = integrated.Snapshot;
        var native = session.Backend == DeskSwitchBackend.Native;
        return new
        {
            settings, engineAvailable = native ? session.Available : engine.IsAvailable, engineVersion = native ? null : "1.27.0",
            engineRunning = native ? integrated.IsRunning : engine.IsRunning || integrated.IsRunning, integrated = session,
            configReady = !native && engine.IsPrepared(settings!), configDirectory = native ? Path.Combine(context.Paths.Resolve("desk-link"), "native-input") : directory,
            localAddresses = LocalAddresses(), message = (native
                ? "自研键鼠共享。请在工具箱内配对自己的 Mac，并明确确认双方键鼠控制与预览点控权限。画面联动按显示器读回结果报告。"
                : "键鼠共享基于 Deskflow。集成会话连接和键鼠焦点在上方显示，画面联动按显示器读回结果报告。") + RecentDisplayMessage()
        };
    }

    private string RecentDisplayMessage()
    {
        var file = Path.Combine(directory, "bridge-last-result.json");
        try
        {
            for (var cursor = file; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
                if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    return "最近切屏记录是文件链接，未读取。";
            if (!File.Exists(file)) return "";
            if (new FileInfo(file).Length > 16384) return "最近切屏记录超出范围，未读取。";
            using var result = JsonDocument.Parse(File.ReadAllText(file));
            var root = result.RootElement;
            if (!root.TryGetProperty("message", out var text) || text.ValueKind != JsonValueKind.String) return "";
            var message = text.GetString() ?? "";
            message = message[..Math.Min(message.Length, 800)];
            var accepted = root.TryGetProperty("commandAccepted", out var flag) && flag.ValueKind == JsonValueKind.True;
            return accepted ? "最近切源命令已接受，实际画面仍需确认。" : "最近自动切屏需要检查：" + message;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return "最近切屏记录暂时无法读取：" + error.Message; }
    }

    private async Task<object> TestInputAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var target = parameters.GetProperty("target").GetString();
        if (target is not ("local" or "remote")) throw new ArgumentException("切换目标无效。");
        (settings! with { LinkDisplay = true }).Validate();
        if (!File.Exists(context.Paths.Resolve("desk-switch.json"))) throw new InvalidOperationException("请先保存显示器与输入源设置。");
        if (target == "remote" && !await context.Prompts.ConfirmAsync("测试切到 Mac 输入", "画面可能暂时变黑。请确认 Mac 已接到所选输入，并知道如何用显示器按钮切回 Win。是否发送本次测试命令？"))
            return new { canceled = true, message = "已取消切屏测试。" };
        await displayBridge.ApplyAsync(context.Paths.Resolve("desk-switch.json"), target!, true, cancellationToken);
        return new { canceled = false, message = "显示器已接受切源命令，请以实际画面确认；这不代表键鼠已配对。" };
    }

    private static string[] LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address).Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(address => address.ToString()).Distinct().Take(16).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }
    private static string ServerAddress() => LocalAddresses().FirstOrDefault() ?? "Win-PC";
    public async ValueTask DisposeAsync() { await integrated.DisposeAsync(); gate.Dispose(); }
}
