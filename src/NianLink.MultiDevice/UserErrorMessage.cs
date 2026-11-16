using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Windows;

namespace NianLink.MultiDevice;

public static class UserErrorMessage
{
    public static string For(Exception error)
    {
        for (Exception? item = error; item is not null; item = item.InnerException)
        {
            if (item is SocketException or HttpRequestException) return "连接遇到问题，请检查网络和对方设备后重试。";
            if (item is TimeoutException or OperationCanceledException) return "等待超时，请检查对方是否在线后重试。";
            if (item is AuthenticationException) return "设备身份验证未通过，请核对配对信息。";
            if (item is UnauthorizedAccessException) return "没有访问权限，请检查文件夹或系统权限。";
            if (item is FileNotFoundException or DirectoryNotFoundException) return "找不到需要的文件或文件夹，请检查位置。";
            if (item is JsonException) return "文件格式无法识别，请选择正确的配对文件。";
            if (item is InvalidOperationException && HasChinese(item.Message)) return item.Message;
        }
        return "操作没有完成，请查看详细原因后重试。";
    }

    private static bool HasChinese(string value)
    {
        foreach (var character in value) if (character is >= '\u4e00' and <= '\u9fff') return true;
        return false;
    }
}

public partial class MainWindow
{
    private void ShowFailure(Exception error, string action = "操作")
    {
        Feedback.Text = action + "未完成：" + UserErrorMessage.For(error);
        ErrorDetailsText.Text = error.ToString();
        ErrorDetailsExpander.Visibility = Visibility.Visible;
        ErrorDetailsExpander.IsExpanded = false;
    }

    private void ClearFailureDetails()
    {
        ErrorDetailsExpander.Visibility = Visibility.Collapsed;
        ErrorDetailsExpander.IsExpanded = false;
        ErrorDetailsText.Text = "";
    }
}
