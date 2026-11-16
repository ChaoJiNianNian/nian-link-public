using System.Text;
using System.Text.Json;
using DesktopToolbox.Windows.DeskSwitch;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
string? configFile = null;
string? target = null;
using var watchdog = new System.Threading.Timer(_ =>
{
    const string message = "显示器驱动响应超时，切屏桥已停止；请确认实际输入源。";
    if (configFile is not null)
    {
        try { WriteResult(configFile, new { commandAccepted = (bool?)null, target, utc = DateTimeOffset.UtcNow, message }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    Console.Error.WriteLine(message);
    Environment.Exit(124);
}, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
try
{
    var monitors = new NativeMonitorControl();
    if (args.SequenceEqual(["--list"]))
    {
        Console.WriteLine(JsonSerializer.Serialize(monitors.Enumerate(), json));
        return 0;
    }
    if (args.Length is not (4 or 5) || args[0] != "--config" || args[2] != "--target" || (args.Length == 5 && args[4] != "--test") ||
        args[3] is not ("local" or "remote") || !Path.IsPathFullyQualified(args[1]) || Path.GetFileName(args[1]) != "desk-switch.json")
        throw new ArgumentException("只接受 --list 或 --config <desk-switch.json绝对路径> --target local|remote [--test]。");
    configFile = Path.GetFullPath(args[1]);
    target = args[3];
    CheckPath(configFile);
    var info = new FileInfo(configFile);
    if (!info.Exists || info.Length > 16384) throw new InvalidDataException("切屏设置不存在或超出范围。");
    var settings = JsonSerializer.Deserialize<DeskSwitchSettings>(File.ReadAllText(configFile, Encoding.UTF8), json)
        ?? throw new InvalidDataException("切屏设置为空。");
    settings.Validate();
    if (!settings.LinkDisplay && args.Length != 5) throw new InvalidOperationException("画面联动尚未启用，本次没有切换输入。");
    (settings with { LinkDisplay = true }).Validate();
    var mutexName = @"Local\NianDeskSwitch." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(configFile.ToUpperInvariant())));
    using var operation = new Mutex(false, mutexName);
    var acquired = false;
    try
    {
        try { acquired = operation.WaitOne(TimeSpan.FromSeconds(8)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("另一项切屏操作仍未完成，请稍后重试。");
        monitors.SetInput(settings.MonitorId, target == "local" ? settings.LocalInput : settings.RemoteInput);
    }
    finally { if (acquired) operation.ReleaseMutex(); }
    try { WriteResult(configFile, new { commandAccepted = true, target, utc = DateTimeOffset.UtcNow, message = "命令已接受，实际画面仍需确认。" }); }
    catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
    { Console.Error.WriteLine("切源命令已接受，但诊断记录未能保存：" + logError.Message); }
    Console.WriteLine(JsonSerializer.Serialize(new { commandAccepted = true, target }, json));
    return 0;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or TimeoutException)
{
    if (configFile is not null)
    {
        try { WriteResult(configFile, new { commandAccepted = false, target, utc = DateTimeOffset.UtcNow, message = error.Message }); }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
    Console.Error.WriteLine(error.Message);
    return 1;
}

static void CheckPath(string file)
{
    for (var cursor = file; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        if ((Directory.Exists(cursor) || File.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("切屏设置与结果目录不能经过文件链接。");
}

static void WriteResult(string config, object value)
{
    var directory = Path.Combine(Path.GetDirectoryName(config)!, "desk-switch");
    CheckPath(directory);
    Directory.CreateDirectory(directory);
    var result = Path.Combine(directory, "bridge-last-result.json");
    CheckPath(result);
    var temporary = result + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8);
        File.Move(temporary, result, true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}
