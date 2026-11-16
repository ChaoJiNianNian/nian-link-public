using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DesktopToolbox.Mac.Views;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.ViewModels;
using DesktopToolbox.Mac.Models;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Preview;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Nian.Input.Control;
using Nian.Input.Security;

internal static class Program
{
    [STAThread] static void Main() => AppBuilder.Configure<FixtureApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);

    private sealed class FixtureApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://NianDeskLink/")) { Source = new Uri("avares://NianDeskLink/Themes/DeskLinkTheme.axaml") });
        }
        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            Dispatcher.UIThread.Post(async () =>
            {
                var life = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
                try
                {
                    foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    {
                        var window = new MacMultiDeviceWindow(Path.Combine(AppContext.BaseDirectory, "unused-synthetic-profile"), true, new FakeVault())
                        { RequestedThemeVariant = variant, ShowInTaskbar = false };
                        window.Show(); window.UpdateLayout();
                        var start = window.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "开始连接");
                        var startPresenter = start.GetVisualDescendants().OfType<ContentPresenter>().Single(x => x.Name == "PART_ContentPresenter");
                        if (startPresenter.Background is not ISolidColorBrush startBackground || startBackground.Color != Color.Parse("#356FEB") ||
                            startPresenter.Foreground is not ISolidColorBrush startForeground || startForeground.Color != Colors.White)
                            throw new InvalidOperationException($"Primary action colors do not match selected blue: {startPresenter.Background}/{startPresenter.Foreground}");
                        // The candidate must expose the existing real controls through distinct pages.
                        foreach (var page in new[] { "设备", "发现与配对", "画面", "键鼠", "文件", "设置与权限" })
                            if (!window.GetVisualDescendants().OfType<Button>().Any(x => x.Tag as string == page && AutomationProperties.GetName(x) == page))
                                throw new InvalidOperationException($"Missing navigation page: {page}");
                        foreach (var (page, expectedButtons) in new[]
                        {
                            ("设备", new[] { "开始连接", "停止连接", "归还主控", "核对指纹", "查看设置" }),
                            ("发现与配对", new[] { "刷新网络", "生成身份卡", "导入配对文件", "确认主控" }),
                            ("画面", new[] { "刷新画面", "开始操作", "放大预览" }),
                            ("键鼠", new[] { "核对键鼠指纹", "切到 Mac", "打开互联小窗" }),
                            ("文件", new[] { "发送文件", "选择收件文件夹", "查看共享文件夹" }),
                            ("设置与权限", new[] { "打开辅助功能", "打开屏幕录制" })
                        })
                        {
                            var navigation = window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == page);
                            navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                            var buttons = window.GetVisualDescendants().OfType<Button>().Where(x => x.IsEffectivelyVisible).Select(x => x.Content as string).ToArray();
                            foreach (var expected in expectedButtons)
                                if (!buttons.Contains(expected)) throw new InvalidOperationException($"{page}: missing {expected}");
                            var requiredTitles = page switch
                            {
                                "发现与配对" => new[] { "01  允许被发现", "02  核对完整指纹", "03  连接主控" },
                                "文件" => new[] { "发送文件", "接收文件", "主控共享文件夹" },
                                _ => Array.Empty<string>()
                            };
                            var visibleTexts = window.GetVisualDescendants().OfType<TextBlock>().Where(x => x.IsEffectivelyVisible).Select(x => x.Text).ToArray();
                            foreach (var required in requiredTitles)
                                if (!visibleTexts.Contains(required)) throw new InvalidOperationException($"{page}: missing visual group {required}");
                            window.Width = 700; await Task.Delay(10); window.UpdateLayout();
                            if (Math.Abs(window.Bounds.Width - 700) > 1)
                                throw new InvalidOperationException($"{page}: failed to test minimum window width; actual={window.Bounds.Width}");
                            foreach (var button in window.GetVisualDescendants().OfType<Button>().Where(x => x.IsEffectivelyVisible))
                            {
                                var origin = button.TranslatePoint(new Point(0, 0), window);
                                if (origin is not null && (origin.Value.X < -1 || origin.Value.X + button.Bounds.Width > window.Bounds.Width + 1))
                                    throw new InvalidOperationException($"{page}: button clipped horizontally at minimum width: {button.Content}");
                            }
                            var captures = Environment.GetEnvironmentVariable("NIAN_MAC_UI_CAPTURE_DIR");
                            var capturePage = Environment.GetEnvironmentVariable("NIAN_MAC_CAPTURE_PAGE");
                            if (variant == ThemeVariant.Light && !string.IsNullOrEmpty(captures) && Environment.GetEnvironmentVariable("NIAN_MAC_DIALOG_ONLY") != "1" && (string.IsNullOrEmpty(capturePage) || capturePage == page))
                            {
                                Directory.CreateDirectory(captures);
                                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                                bitmap.Render(window); bitmap.Save(Path.Combine(captures, $"{page}-narrow.png"), PngBitmapEncoderOptions.Default);
                            }
                            window.Width = 980; await Task.Delay(10); window.UpdateLayout();
                            if (variant == ThemeVariant.Light && !string.IsNullOrEmpty(captures) && Environment.GetEnvironmentVariable("NIAN_MAC_DIALOG_ONLY") != "1" && (string.IsNullOrEmpty(capturePage) || capturePage == page))
                            {
                                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                                bitmap.Render(window); bitmap.Save(Path.Combine(captures, $"{page}.png"), PngBitmapEncoderOptions.Default);
                            }
                        }
                        foreach (var expected in new[] { "查看对方画面", "允许对方看 Mac 画面", "同步文字剪贴板" })
                        {
                            var page = expected == "同步文字剪贴板" ? "键鼠" : "画面";
                            window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == page)
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            window.UpdateLayout();
                            var box = window.GetVisualDescendants().OfType<CheckBox>().Single(x => (string?)x.Content == expected);
                            box.BringIntoView(); window.UpdateLayout();
                            foreach (var isChecked in new[] { false, true })
                            {
                                box.IsChecked = isChecked; window.UpdateLayout();
                                var label = box.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == expected);
                                var foreground = ((ISolidColorBrush)label.Foreground!).Color;
                                var background = box.GetVisualAncestors().OfType<Border>()
                                    .Select(b => b.Background).OfType<ISolidColorBrush>().First(b => b.Color.A == 255).Color;
                                var ratio = Contrast(foreground, background);
                                Console.WriteLine($"{variant}: {expected}; checked={isChecked}; foreground={foreground}; background={background}; contrast={ratio:F2}; bounds={label.Bounds}");
                                if (label.Bounds.Width <= 0 || label.Bounds.Height <= 0 || foreground.A != 255 ||
                                    label.Opacity < 1 || label.GetVisualAncestors().Any(v => v.Opacity < 1) || ratio < 4.5)
                                    throw new InvalidOperationException($"{variant}: {expected} is not legible on the actual card background (contrast {ratio:F2}).");
                            }
                        }
                        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "画面")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.IsEffectivelyVisible && x.Text == "等待对方画面"))
                            throw new InvalidOperationException("The empty preview needs a visible placeholder.");
                        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "每次重新启动本机多设备连接后，需要重新允许共享 Mac 画面。"))
                            throw new InvalidOperationException("The share-per-session policy is not visible next to screen permissions.");
                        foreach (var action in new[] { "刷新画面", "开始操作" })
                        {
                            window.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == action)
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                            var feedback = (TextBlock)typeof(MacMultiDeviceWindow).GetField("feedback", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                            if (feedback.Text?.Contains("请先", StringComparison.Ordinal) != true)
                                throw new InvalidOperationException($"{action}: unavailable operation did not report a real connection requirement.");
                        }
                        foreach (var text in new[] { "查看对方画面", "允许对方看 Mac 画面" })
                        {
                            var choice = window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Content as string == text);
                            choice.IsChecked = true;
                            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                            if (choice.IsChecked != false) throw new InvalidOperationException($"{text}: unchecked connection was shown as enabled.");
                        }
                        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "设备")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        window.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "查看设置")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        if (!window.GetVisualDescendants().OfType<Button>().Any(x => x.IsEffectivelyVisible && x.Content as string == "打开屏幕录制"))
                            throw new InvalidOperationException("Device permissions shortcut did not open settings.");
                        window.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "查看网络")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                        if (!window.GetVisualDescendants().OfType<Button>().Any(x => x.IsEffectivelyVisible && x.Content as string == "刷新网络"))
                            throw new InvalidOperationException("Network shortcut did not open discovery settings.");
                        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "文件")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                        var fileStatusField = typeof(MacMultiDeviceWindow).GetField("fileStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
                        ((TextBlock)fileStatusField.GetValue(window)!).Text = "synthetic transfer status"; window.UpdateLayout();
                        if (window.GetVisualDescendants().OfType<TextBlock>().Count(x => x.Text == "synthetic transfer status") != 2)
                            throw new InvalidOperationException("File and shared-folder statuses are not synchronized.");
                        window.Height = 520; await Task.Delay(10); window.UpdateLayout();
                        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "发现与配对")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        var pageScroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "MacCandidatePageScroll");
                        pageScroll.Offset = new Vector(0, 100); window.UpdateLayout();
                        if (pageScroll.Offset.Y < 1) throw new InvalidOperationException("Cannot exercise scrolled navigation state.");
                        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "设备")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        if (pageScroll.Offset.Y > 0.5) throw new InvalidOperationException("Page navigation did not return to the top.");
                        window.Close();
                    }
                    await VerifyMainPreviewRoutingAsync();
                    await VerifyShareCanBeRevokedDuringPermissionAsync();
                    await VerifyMiniPreviewRoutingAsync();
                    await VerifyCandidateDialogsAsync(life);
                    await VerifyCandidatePreviewWindowsAsync();
                    await VerifyRealHandoffPreviewAsync();
                    await VerifyShareRevocationDuringScreenActionsAsync();
                    await VerifyViewerVisibilityAggregationAsync();
                    await Task.Delay(20); life.Shutdown(0);
                }
                catch (Exception e) { Console.Error.WriteLine(e); life.Shutdown(1); }
            });
        }
    }
    private static async Task VerifyCandidateDialogsAsync(IClassicDesktopStyleApplicationLifetime life)
    {
        var owner = new MacMultiDeviceWindow(Path.Combine(AppContext.BaseDirectory, "unused-dialog-profile"), true, new FakeVault()) { ShowInTaskbar = false };
        owner.Show(); owner.UpdateLayout();
        using var stop = new CancellationTokenSource();
        var message = "请逐字核对主控控制通道指纹：\n\n" + new string('A', 64) + "\n\n本机键鼠指纹：\n\n" + new string('B', 64);
        var method = typeof(MacMultiDeviceWindow).GetMethod("AskFilelessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var outcome = (Task<bool>)method.Invoke(owner, ["核对本次安全连接", message, "一致，确认", stop.Token])!;
        Window? dialog = null;
        for (var i = 0; i < 100 && dialog is null; i++) { dialog = life.Windows.FirstOrDefault(x => x != owner && x.Title == "核对本次安全连接"); if (dialog is null) await Task.Delay(5); }
        if (dialog is null) throw new InvalidOperationException("The real secure confirmation dialog did not open.");
        dialog.UpdateLayout();
        var text = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().SingleOrDefault(x => x.Text == message);
        if (text is null || text.Text?.Contains(new string('A', 64)) != true || text.Text?.Contains(new string('B', 64)) != true)
            throw new InvalidOperationException("The candidate dialog must expose both full fingerprints as selectable text.");
        if (!dialog.GetVisualDescendants().OfType<Button>().Any(x => x.Content as string == "一致，确认") ||
            !dialog.GetVisualDescendants().OfType<Button>().Any(x => x.Content as string == "取消"))
            throw new InvalidOperationException("The secure confirmation actions must remain available.");
        var confirmPresenter = dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "一致，确认")
            .GetVisualDescendants().OfType<ContentPresenter>().Single(x => x.Name == "PART_ContentPresenter");
        if (confirmPresenter.Background is not ISolidColorBrush fill || fill.Color != Color.Parse("#356FEB"))
            throw new InvalidOperationException("Secure confirmation did not use the selected blue accent.");
        dialog.Width = 440; dialog.Height = 320; await Task.Delay(10); dialog.UpdateLayout();
        var messageScroll = text.GetVisualAncestors().OfType<ScrollViewer>().First();
        var confirmation = dialog.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "一致，确认");
        if (messageScroll.Extent.Height > messageScroll.Viewport.Height + 1 && confirmation.IsEnabled)
            throw new InvalidOperationException("Confirmation was enabled while some fingerprint text was hidden below the scroll area.");
        messageScroll.Offset = new Vector(0, messageScroll.Extent.Height); await Task.Delay(10); dialog.UpdateLayout();
        if (!confirmation.IsEnabled) throw new InvalidOperationException("Confirmation stayed disabled after all fingerprint text was scrolled into view.");
        foreach (var action in dialog.GetVisualDescendants().OfType<Button>().Where(x => x.Content as string is "一致，确认" or "取消"))
        {
            var location = action.TranslatePoint(new Point(0, 0), dialog);
            if (location is null || location.Value.X < 0 || location.Value.Y < 0 ||
                location.Value.X + action.Bounds.Width > dialog.Bounds.Width || location.Value.Y + action.Bounds.Height > dialog.Bounds.Height)
                throw new InvalidOperationException("Confirmation action is clipped at the minimum dialog size.");
        }
        var captures = Environment.GetEnvironmentVariable("NIAN_MAC_UI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captures))
        {
            Directory.CreateDirectory(captures);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96));
            bitmap.Render(dialog); bitmap.Save(Path.Combine(captures, "合成指纹核对.png"), PngBitmapEncoderOptions.Default);
        }
        stop.Cancel();
        if (await outcome.WaitAsync(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Cancelled confirmation returned accepted.");
        var accepted = (Task<bool>)method.Invoke(owner, ["核对本次安全连接", message, "一致，确认", CancellationToken.None])!;
        Window? second = null;
        for (var i = 0; i < 100 && second is null; i++) { second = life.Windows.FirstOrDefault(x => x != owner && x != dialog && x.Title == "核对本次安全连接"); if (second is null) await Task.Delay(5); }
        if (second is null) throw new InvalidOperationException("The second confirmation dialog did not open.");
        second.UpdateLayout();
        second.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "一致，确认")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!await accepted.WaitAsync(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Explicit confirmation was not preserved.");
        var closed = (Task<bool>)method.Invoke(owner, ["核对本次安全连接", message, "一致，确认", CancellationToken.None])!;
        Window? third = null;
        for (var i = 0; i < 100 && third is null; i++) { third = life.Windows.FirstOrDefault(x => x != owner && x != dialog && x != second && x.Title == "核对本次安全连接"); if (third is null) await Task.Delay(5); }
        if (third is null) throw new InvalidOperationException("The title-bar close test did not open its confirmation dialog.");
        third.Close();
        if (await closed.WaitAsync(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Closing the confirmation window returned accepted.");
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        var rejectedBeforeShow = (Task<bool>)method.Invoke(owner, ["核对本次安全连接", message, "一致，确认", alreadyCancelled.Token])!;
        try { await rejectedBeforeShow; throw new InvalidOperationException("Already-cancelled confirmation unexpectedly proceeded."); }
        catch (OperationCanceledException) { }
        owner.Close();
        Console.WriteLine("Secure dialog: complete synthetic fingerprints retained; cancellation returns false.");
    }
    private static async Task VerifyShareRevocationDuringScreenActionsAsync()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Path.Combine(AppContext.BaseDirectory, "synthetic-screen-action-" + Guid.NewGuid().ToString("N"));
        var service = new MacCollaborationService(path, async token =>
        {
            requestStarted.TrySetResult(); await releaseRequest.Task.WaitAsync(token); return null;
        }, new FakeMedia());
        await service.SetViewPreviewEnabledAsync(true);
        await service.SetShareScreenEnabledAsync(true);
        var window = new MacMultiDeviceWindow(Path.Combine(path, "unused"), true, new FakeVault()) { ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        typeof(MacMultiDeviceWindow).GetField("collaboration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, service);
        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "画面")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        var share = window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Content as string == "允许对方看 Mac 画面");
        share.IsChecked = true;
        try
        {
            var refresh = window.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "刷新画面");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!window.IsEnabled || !share.IsEffectivelyEnabled)
                throw new InvalidOperationException("A pending screen refresh blocked revocation of Mac screen sharing.");
            share.IsChecked = false; share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && service.Snapshot.Settings.ShareScreenEnabled; i++) await Task.Delay(5);
            if (service.Snapshot.Settings.ShareScreenEnabled) throw new InvalidOperationException("Sharing stayed enabled during a pending refresh.");
            releaseRequest.TrySetResult();
            for (var i = 0; i < 100 && !refresh.IsEnabled; i++) await Task.Delay(5);
            if (!refresh.IsEnabled) throw new InvalidOperationException("Pending refresh failed to finish after the synthetic request returned.");

            await service.SetShareScreenEnabledAsync(true); share.IsChecked = true;
            var backend = new FakeDeskflowPreview();
            var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new DeskflowPreviewSession(backend, (enabled, _) =>
            { if (enabled) captureStarted.TrySetResult(); else captureStopped.TrySetResult(); return Task.CompletedTask; });
            var viewer = new MacRemotePreviewWindow(session, "Mac", "Windows", candidateBlue: true) { ShowInTaskbar = false };
            viewer.Show(); viewer.SetConnectionState(true, ""); viewer.UpdateLayout();
            typeof(MacMultiDeviceWindow).GetField("detachedPreview", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, viewer);
            var start = window.GetVisualDescendants().OfType<Button>().Single(x => x.Content as string == "开始操作");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!window.IsEnabled || !share.IsEffectivelyEnabled)
                throw new InvalidOperationException("Preparing preview control blocked revocation of Mac screen sharing.");
            share.IsChecked = false; share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && service.Snapshot.Settings.ShareScreenEnabled; i++) await Task.Delay(5);
            if (service.Snapshot.Settings.ShareScreenEnabled) throw new InvalidOperationException("Sharing stayed enabled while preview control prepared.");
            viewer.Hide(); await captureStopped.Task.WaitAsync(TimeSpan.FromSeconds(2)); viewer.Close();
            if (backend.BeginCount != 0) throw new InvalidOperationException("Control began without a fresh preview frame.");
            Console.WriteLine("Pending refresh and control preparation: sharing can still be revoked.");
        }
        finally
        {
            releaseRequest.TrySetResult();
            var stop = typeof(MacMultiDeviceWindow).GetMethod("StopCollaborationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)stop.Invoke(window, [])!; window.Close();
        }
    }
    private static async Task VerifyRealHandoffPreviewAsync()
    {
        var backend = new FakeDeskflowPreview();
        var capture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new DeskflowPreviewSession(backend, (enabled, _) => { if (enabled) capture.TrySetResult(); return Task.CompletedTask; });
        var viewer = new MacRemotePreviewWindow(session, "Mac", "Windows", candidateBlue: true) { ShowInTaskbar = false };
        viewer.Show(); viewer.SetConnectionState(true, ""); viewer.UpdateLayout();
        var start = typeof(MacRemotePreviewWindow).GetMethod("StartControlFromUserAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var starting = (Task)start.Invoke(viewer, [])!;
        await capture.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (backend.BeginCount != 0) throw new InvalidOperationException("Deskflow handoff began without a fresh frame.");
        session.ObserveFrame(new PreviewFrame { State = "live", Width = 1, Height = 1, JpegBytes = [1], CapturedAtUtc = DateTimeOffset.UtcNow });
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        if (!session.IsActive || backend.BeginCount != 1) throw new InvalidOperationException("Fresh frame did not start the Deskflow handoff.");
        await (Task)start.Invoke(viewer, [])!;
        if (!session.IsActive || backend.BeginCount != 1 || backend.EndCount != 0)
            throw new InvalidOperationException("Repeated start stopped or duplicated the Deskflow handoff.");
        viewer.Hide(); await backend.Ended.Task.WaitAsync(TimeSpan.FromSeconds(2)); viewer.Close();

        var duringPrepare = new FakeDeskflowPreview();
        var pendingCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pendingSession = new DeskflowPreviewSession(duringPrepare, (enabled, _) =>
        { if (enabled) pendingCapture.TrySetResult(); else captureStopped.TrySetResult(); return Task.CompletedTask; });
        var pendingViewer = new MacRemotePreviewWindow(pendingSession, "Mac", "Windows", candidateBlue: true) { ShowInTaskbar = false };
        pendingViewer.Show(); pendingViewer.SetConnectionState(true, ""); pendingViewer.UpdateLayout();
        var preparing = (Task)start.Invoke(pendingViewer, [])!;
        await pendingCapture.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pendingViewer.Hide(); await captureStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await preparing.WaitAsync(TimeSpan.FromSeconds(2));
        if (duringPrepare.BeginCount != 0 || pendingSession.IsActive) throw new InvalidOperationException("Hidden preparing preview started a Deskflow handoff.");
        pendingViewer.Close();
        Console.WriteLine("Deskflow handoff: fresh frame required, repeated start safe, hide cancels or returns.");
    }

    private static async Task VerifyViewerVisibilityAggregationAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "synthetic-viewer-visibility-" + Guid.NewGuid().ToString("N"));
        var service = new MacCollaborationService(path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), new FakeMedia());
        var window = new MacMultiDeviceWindow(Path.Combine(path, "unused"), true, new FakeVault()) { ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        typeof(MacMultiDeviceWindow).GetField("collaboration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, service);
        bool Visible(string field) => (bool)typeof(MacCollaborationService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        void Navigate(string page)
        {
            window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == page)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        try
        {
            Navigate("设备");
            if (Visible("miniVisible") || Visible("detachedVisible")) throw new InvalidOperationException("Device page subscribed to an unseen preview.");
            Navigate("画面");
            if (!Visible("miniVisible")) throw new InvalidOperationException("Visible main preview was not subscribed.");
            window.WindowState = WindowState.Minimized; await Task.Delay(10);
            if (Visible("miniVisible")) throw new InvalidOperationException("Minimized main preview stayed subscribed.");
            window.WindowState = WindowState.Normal; await Task.Delay(10);
            if (!Visible("miniVisible")) throw new InvalidOperationException("Restored visible main preview did not resubscribe.");
            window.Hide(); await Task.Delay(10);
            if (Visible("miniVisible")) throw new InvalidOperationException("Hidden main preview stayed subscribed.");
            window.Show(); await Task.Delay(10);
            if (!Visible("miniVisible")) throw new InvalidOperationException("Shown main preview did not resubscribe.");
            var updateMini = typeof(MacMultiDeviceWindow).GetMethod("UpdateMiniViewerVisibility", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Multi-device mini visibility aggregation is missing.");
            await using var wireless = new MacWirelessViewModel(new FakeWireless());
            var miniOwner = new Window { ShowInTaskbar = false }; miniOwner.Show();
            var link = new MacMultiMiniLink("Host", "Mac", _ => Task.CompletedTask, _ => Task.CompletedTask);
            using (var mini = new MacCollaborationControl(miniOwner, service, link, wireless,
                (visible, detached) => updateMini.Invoke(window, [visible, detached])))
            {
                mini.RefreshViewerVisibility(); Navigate("设备");
                if (!Visible("miniVisible")) throw new InvalidOperationException("Switching the main page disabled the visible mini preview.");
                Navigate("画面"); mini.Dispose();
                if (!Visible("miniVisible")) throw new InvalidOperationException("Closing the mini disabled the visible main preview.");
            }
            miniOwner.Close(); Navigate("设备");
            updateMini.Invoke(window, [false, true]);
            if (!Visible("detachedVisible")) throw new InvalidOperationException("Visible mini detached preview lost its subscription.");
            Navigate("画面"); Navigate("设备");
            if (!Visible("detachedVisible")) throw new InvalidOperationException("Main navigation overrode the visible detached mini preview.");
            updateMini.Invoke(window, [false, false]);
            if (Visible("miniVisible") || Visible("detachedVisible")) throw new InvalidOperationException("Hidden preview surfaces remained subscribed.");
            Console.WriteLine("Preview visibility: main page, minimize and mini/detached surfaces aggregate correctly.");
        }
        finally
        {
            var stop = typeof(MacMultiDeviceWindow).GetMethod("StopCollaborationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)stop.Invoke(window, [])!; window.Close();
        }
    }
    private static async Task VerifyCandidatePreviewWindowsAsync()
    {
        var candidate = new MacRemotePreviewWindow((PreviewControlSession?)null, candidateBlue: true) { ShowInTaskbar = false };
        var legacy = new MacRemotePreviewWindow((PreviewControlSession?)null) { ShowInTaskbar = false };
        candidate.Show(); legacy.Show(); candidate.SetAppearance(true); legacy.SetAppearance(true);
        candidate.UpdateLayout(); legacy.UpdateLayout();
        var blueAction = candidate.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "PreviewControlAction");
        var legacyAction = legacy.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "PreviewControlAction");
        if (blueAction.Background is not ISolidColorBrush blue || blue.Color != Color.Parse("#356FEB"))
            throw new InvalidOperationException("Candidate preview did not use the selected blue action.");
        if (legacyAction.Background is not ISolidColorBrush old || old.Color != Color.Parse("#A7C4FF"))
            throw new InvalidOperationException("Candidate preview style leaked into the legacy window.");
        var start = typeof(MacRemotePreviewWindow).GetMethod("StartControlFromUserAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            await (Task)start.Invoke(candidate, [])!;
            throw new InvalidOperationException("Unpaired preview incorrectly began control.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("请先等待预览连接就绪", StringComparison.Ordinal)) { }
        candidate.Close(); legacy.Close();
        var channel = new FakePreviewChannel();
        var captureRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new PreviewControlSession(channel, (enabled, _) => { if (enabled) captureRequested.TrySetResult(); return Task.CompletedTask; });
        var viewer = new MacRemotePreviewWindow(session, candidateBlue: true) { ShowInTaskbar = false };
        viewer.Show(); viewer.SetConnectionState(true, ""); viewer.UpdateLayout();
        var starting = (Task)start.Invoke(viewer, [])!;
        await captureRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (channel.BeginCount != 0) throw new InvalidOperationException("Control started before a fresh displayed frame.");
        var geometry = new PreviewSourceGeometry("synthetic", "one", 0, 0, 100, 100, 1, 1);
        var bytes = new byte[] { 1, 2, 3 };
        var proof = new PreviewFrameAuthority(() => geometry).Register(bytes, geometry);
        session.ObserveDisplayedFrame(proof, bytes, 1, 1);
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        if (!session.IsActive || channel.BeginCount != 1) throw new InvalidOperationException("Explicit start did not reach the existing control session.");
        await (Task)start.Invoke(viewer, [])!;
        if (!session.IsActive || channel.BeginCount != 1 || channel.EndCount != 0)
            throw new InvalidOperationException("A repeated start unexpectedly stopped or duplicated control.");
        viewer.Hide(); await channel.Ended.Task.WaitAsync(TimeSpan.FromSeconds(2)); viewer.Close();
        var calls = 0;
        var returnWindow = new MacRemotePreviewReturnWindow("synthetic", _ => { calls++; return Task.CompletedTask; }, candidateBlue: true) { ShowInTaskbar = false };
        returnWindow.Show(); returnWindow.UpdateLayout();
        var returnButton = returnWindow.GetVisualDescendants().OfType<Button>().Single();
        if (returnButton.Background is not ISolidColorBrush returnBlue || returnBlue.Color != Color.Parse("#356FEB"))
            throw new InvalidOperationException("Candidate return overlay did not use the selected blue.");
        returnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(5);
        if (calls != 1) throw new InvalidOperationException("Return action did not invoke the original safe return callback.");
        var oldReturn = new MacRemotePreviewReturnWindow("synthetic-old", _ => Task.CompletedTask) { ShowInTaskbar = false };
        oldReturn.Show(); oldReturn.UpdateLayout();
        var oldButton = oldReturn.GetVisualDescendants().OfType<Button>().Single();
        if (oldButton.Background is not ISolidColorBrush original || original.Color != Color.Parse("#DD263C5C"))
            throw new InvalidOperationException("Candidate return style leaked into the legacy window.");
        oldReturn.Close();
        await VerifyReturnStatusAsync();
        Console.WriteLine("Candidate preview and return overlay: blue styling isolated and original return action preserved.");
    }

    private static async Task VerifyReturnStatusAsync()
    {
        foreach (var scenario in new[] { "unknown", "ordinary", "state-failure", "legacy" })
        {
            var calls = 0;
            Func<string, Task> action = _ => { calls++; return Task.FromException(new InvalidOperationException("synthetic failure")); };
            Func<bool>? owner = scenario == "legacy" ? null : () => scenario == "state-failure"
                ? throw new InvalidOperationException("synthetic state failure") : scenario == "unknown";
            var window = new MacRemotePreviewReturnWindow("synthetic", action, candidateBlue: scenario != "legacy", isUnconfirmed: owner);
            try
            {
                window.Show(); window.UpdateLayout();
                var button = window.GetVisualDescendants().OfType<Button>().Single();
                var invoke = typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
                await ((Task)invoke.Invoke(window, [])!).WaitAsync(TimeSpan.FromSeconds(5));
                var expected = scenario == "unknown" ? "尚未确认 · 检查键鼠" : scenario == "state-failure" ? "状态不可用 · 检查键鼠" : "归还失败，点击重试";
                if (button.Content as string != expected || !button.IsEnabled || !window.IsVisible)
                    throw new InvalidOperationException($"{scenario}: return status or retry is incorrect.");
                window.UpdateLayout();
                var text = button.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Text == expected);
                var position = text.TranslatePoint(new Point(0, 0), window);
                if (position is null || position.Value.X < 0 || position.Value.X + text.Bounds.Width > window.Bounds.Width || text.Bounds.Height <= 0)
                    throw new InvalidOperationException($"{scenario}: return status is outside the overlay.");
                if (scenario == "unknown")
                {
                    var tip = ToolTip.GetTip(button) as string ?? "";
                    if (!tip.Contains("仍在处理", StringComparison.Ordinal) || tip.Contains("快捷键", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unknown outcome must not imply that a shortcut confirms return.");
                }
                await ((Task)invoke.Invoke(window, [])!).WaitAsync(TimeSpan.FromSeconds(5));
                if (calls != 2 || !window.IsVisible) throw new InvalidOperationException($"{scenario}: return retry was not preserved.");
            }
            finally { window.Close(); }
        }
    }
    private static async Task VerifyMainPreviewRoutingAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "synthetic-main-preview-" + Guid.NewGuid().ToString("N"));
        var media = new FakeMedia();
        var service = new MacCollaborationService(path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), media);
        var window = new MacMultiDeviceWindow(Path.Combine(path, "unused"), true, new FakeVault()) { ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        typeof(MacMultiDeviceWindow).GetField("collaboration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, service);
        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "画面")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        async Task Change(string text, bool enabled)
        {
            var choice = window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Content as string == text);
            choice.IsChecked = enabled; choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100; i++)
            {
                var settings = service.Snapshot.Settings;
                var actual = text == "查看对方画面" ? settings.PreviewEnabled : settings.ShareScreenEnabled;
                if (actual == enabled && (text != "允许对方看 Mac 画面" || !enabled || media.PermissionRequests > 0)) break;
                await Task.Delay(5);
            }
            if (choice.IsChecked != enabled) throw new InvalidOperationException($"{text}: UI state did not match the resulting choice.");
        }
        try
        {
            await Change("查看对方画面", true);
            if (!service.Snapshot.Settings.PreviewEnabled || service.Snapshot.Settings.ShareScreenEnabled || media.PermissionRequests != 0)
                throw new InvalidOperationException("Enabling viewing must not enable capture or request permission.");
            await Change("允许对方看 Mac 画面", true);
            if (!service.Snapshot.Settings.PreviewEnabled || !service.Snapshot.Settings.ShareScreenEnabled || media.PermissionRequests != 1)
                throw new InvalidOperationException("Enabling sharing should preserve viewing and request capture permission.");
            await Change("查看对方画面", false);
            if (service.Snapshot.Settings.PreviewEnabled || !service.Snapshot.Settings.ShareScreenEnabled)
                throw new InvalidOperationException("Disabling viewing should preserve sharing.");
            await Change("允许对方看 Mac 画面", false);
            if (service.Snapshot.Settings.PreviewEnabled || service.Snapshot.Settings.ShareScreenEnabled)
                throw new InvalidOperationException("Both choices should be off.");
            using var pixels = new RenderTargetBitmap(new PixelSize(16, 16), new Vector(96, 96));
            pixels.Render(window);
            using var encoded = new MemoryStream(); pixels.Save(encoded, PngBitmapEncoderOptions.Default);
            var stale = service.Snapshot with
            {
                Settings = service.Snapshot.Settings with { PreviewEnabled = true },
                Status = "synthetic stale status",
                Preview = new PreviewFrame { State = "live", Width = 16, Height = 16, JpegBytes = encoded.ToArray(), CapturedAtUtc = DateTimeOffset.UtcNow }
            };
            var epoch = (long)typeof(MacMultiDeviceWindow).GetField("collaborationEpoch", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var snapshotField = typeof(MacCollaborationService).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var actual = service.Snapshot;
            snapshotField.SetValue(service, stale);
            typeof(MacMultiDeviceWindow).GetMethod("OnCollaborationChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [epoch, stale]);
            await Task.Delay(15); window.UpdateLayout();
            var placeholder = (TextBlock)typeof(MacMultiDeviceWindow).GetField("previewPlaceholder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var image = (Image)typeof(MacMultiDeviceWindow).GetField("previewImage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            if (image.Source is null || placeholder.IsVisible) throw new InvalidOperationException("A live preview did not replace its empty placeholder.");
            snapshotField.SetValue(service, actual);
            typeof(MacMultiDeviceWindow).GetMethod("OnCollaborationChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [epoch, stale]);
            await Task.Delay(15); window.UpdateLayout();
            var fileStatus = (TextBlock)typeof(MacMultiDeviceWindow).GetField("fileStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            if (image.Source is not null || !placeholder.IsVisible || fileStatus.Text == "synthetic stale status")
                throw new InvalidOperationException("A stale queued snapshot reintroduced a closed preview or old status.");
            Console.WriteLine("Main preview routing: viewing and sharing independently control real service state.");
        }
        finally
        {
            var stop = typeof(MacMultiDeviceWindow).GetMethod("StopCollaborationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)stop.Invoke(window, [])!;
            window.Close();
        }
    }
    private static async Task VerifyShareCanBeRevokedDuringPermissionAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "synthetic-share-cancel-" + Guid.NewGuid().ToString("N"));
        var media = new BlockingMedia();
        var service = new MacCollaborationService(path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), media);
        var window = new MacMultiDeviceWindow(Path.Combine(path, "unused"), true, new FakeVault()) { ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        typeof(MacMultiDeviceWindow).GetField("collaboration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, service);
        window.GetVisualDescendants().OfType<Button>().Single(x => x.Tag as string == "画面")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        var share = window.GetVisualDescendants().OfType<CheckBox>().Single(x => x.Content as string == "允许对方看 Mac 画面");
        try
        {
            share.IsChecked = true; share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await media.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!window.IsEnabled || !share.IsEffectivelyEnabled)
                throw new InvalidOperationException("Screen sharing cannot be revoked while the system permission request is pending.");
            share.IsChecked = false; share.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await media.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var i = 0; i < 100 && service.Snapshot.Settings.ShareScreenEnabled; i++) await Task.Delay(5);
            if (service.Snapshot.Settings.ShareScreenEnabled || share.IsChecked != false)
                throw new InvalidOperationException("Revoking during a pending permission request did not remain off.");
            // An OS response may arrive after cancellation; it must not reopen sharing.
            media.Release.TrySetResult();
            await media.Returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(15);
            if (service.Snapshot.Settings.ShareScreenEnabled || share.IsChecked != false)
                throw new InvalidOperationException("A late permission response restored revoked sharing.");
            Console.WriteLine("Pending permission: user can revoke sharing without waiting for system response.");
        }
        finally
        {
            media.Release.TrySetResult();
            var stop = typeof(MacMultiDeviceWindow).GetMethod("StopCollaborationAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)stop.Invoke(window, [])!;
            window.Close();
        }
    }
    private static async Task VerifyMiniPreviewRoutingAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "synthetic-mini-preview-" + Guid.NewGuid().ToString("N"));
        var media = new FakeMedia();
        await using var service = new MacCollaborationService(path, _ => Task.FromResult<DeskLinkPairingBundle?>(null), media);
        await using var wireless = new MacWirelessViewModel(new FakeWireless());
        var owner = new Window();
        var link = new MacMultiMiniLink("Host", "Mac", _ => Task.CompletedTask, _ => Task.CompletedTask);
        using var control = new MacCollaborationControl(owner, service, link, wireless);
        var dispatch = typeof(MacCollaborationControl).GetMethod("DispatchActionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        static MacWebRequest Request(bool enabled)
        {
            var json = $"{{\"id\":\"synthetic\",\"action\":\"preview.setEnabled\",\"payload\":{{\"enabled\":{enabled.ToString().ToLowerInvariant()}}}}}";
            if (!MacWebRequest.TryParse(json, out var request)) throw new InvalidOperationException("Invalid synthetic UI request.");
            return request!;
        }
        await (Task<object?>)dispatch.Invoke(control, [Request(true)])!;
        if (!service.Snapshot.Settings.PreviewEnabled || service.Snapshot.Settings.ShareScreenEnabled || media.PermissionRequests != 0)
            throw new InvalidOperationException("Multi-device mini enabling viewing must not enable screen sharing or request capture permission.");
        await service.SetShareScreenEnabledAsync(true);
        await (Task<object?>)dispatch.Invoke(control, [Request(false)])!;
        if (service.Snapshot.Settings.PreviewEnabled || !service.Snapshot.Settings.ShareScreenEnabled || media.PermissionRequests != 1)
            throw new InvalidOperationException("Multi-device mini disabling viewing must preserve an independently enabled share.");
        using var legacy = new MacCollaborationControl(owner, service, new FakeLegacyLink(), wireless);
        await (Task<object?>)dispatch.Invoke(legacy, [Request(false)])!;
        if (service.Snapshot.Settings.PreviewEnabled || service.Snapshot.Settings.ShareScreenEnabled)
            throw new InvalidOperationException("The existing single-device mini must retain combined-toggle behavior.");
        Console.WriteLine("Mini preview routing: multi-device view only; legacy combined behavior preserved.");
    }
    private sealed class FakeMedia : IMacMediaBridge
    {
        public int PermissionRequests { get; private set; }
        public Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        { PermissionRequests++; return Task.FromResult(JsonDocument.Parse("{\"status\":\"ready\"}")); }
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class BlockingMedia : IMacMediaBridge
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JsonDocument> RequestAsync(object request, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            await Release.Task; // Simulate an OS prompt that returns after cancellation.
            Returned.TrySetResult();
            return JsonDocument.Parse("{\"status\":\"ready\"}");
        }
        public Task<MacScreenCaptureReply> CaptureAsync(int width = 960, int height = 540, uint? windowId = null, CancellationToken cancellationToken = default) => Task.FromResult(new MacScreenCaptureReply());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakePreviewChannel : IPreviewControlChannel
    {
        public int BeginCount { get; private set; }
        public int EndCount { get; private set; }
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BeginAsync(CancellationToken token) { BeginCount++; return Task.CompletedTask; }
        public Task SendAsync(PreviewInputCommand command, CancellationToken token) => Task.CompletedTask;
        public Task HeartbeatAsync(CancellationToken token) => Task.CompletedTask;
        public Task EndAsync(CancellationToken token) { EndCount++; Ended.TrySetResult(); return Task.CompletedTask; }
    }
    private sealed class FakeDeskflowPreview : IDeskflowPreviewControl
    {
        public int BeginCount { get; private set; }
        public int EndCount { get; private set; }
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DeskflowPreviewState current = new();
        public bool PreviewHandoffSupported => true;
        public string PreviewReturnShortcut => "Ctrl+Alt+F11";
        public string PreviewHandoffHint => "合成交接";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken cancellationToken = default)
        {
            BeginCount++;
            current = new DeskflowPreviewState { Id = Guid.NewGuid().ToString("N"), ViewerComputer = "Mac", TargetComputer = "Windows",
                Phase = DeskflowPreviewPhase.Active, ReturnShortcut = PreviewReturnShortcut, ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(10) };
            return Task.FromResult(current);
        }
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(current);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken cancellationToken = default)
        { EndCount++; current = new DeskflowPreviewState(); Ended.TrySetResult(); return Task.FromResult(current); }
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken cancellationToken = default) => Task.FromResult(current);
    }
    private sealed class FakeWireless : IMacWirelessModeService
    {
        public Task<MacWirelessSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MacWirelessSnapshot(MacAwdlState.Unavailable));
        public Task<MacWirelessSnapshot> SetSmoothModeAsync(bool enabled, CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);
    }
    private sealed class FakeLegacyLink : IMacMiniLink
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string WindowsName => "Host"; public string MacName => "Mac"; public string ErrorMessage => "";
        public bool CanStart => false; public bool CanStop => false; public bool CanSwitch => false; public bool CanReturnToWindows => false;
        public bool IsSharingRunning => false; public bool IsInputConnected => false; public bool IsWindowsActive => false; public bool IsMacActive => false;
        public Task StartAsync() => Task.CompletedTask; public Task StopAsync() => Task.CompletedTask; public Task SwitchAsync(string target) => Task.CompletedTask;
    }
    // Startup-check mode must never touch a real profile or vault.
    private sealed class FakeVault : IIdentityVault
    {
        public Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The synthetic vault must not be used.");
        public Task StoreAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The synthetic vault must not be used.");
    }
    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte channel) { var x = channel / 255d; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
