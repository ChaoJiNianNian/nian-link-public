#if FILELESS_PAIRING_CANDIDATE
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    // The capability is never shared with the old file-pairing review.
    private readonly DiscoveryUiLifetime<SecurePairingHost> filelessHost = new();
    private readonly DiscoveryUiLifetime<SecurePairingPeer> filelessPeer = new();
    private long filelessEpoch;
    private bool filelessStarting, incomingPromptOpen, filelessPeerHandoff;
    private TaskCompletionSource? filelessDeliveryFinished;
    private enum DeliveryOutcome { None, Running, Unknown, Completed, Aborted, PeerAbsent }
    private DeliveryOutcome filelessDeliveryOutcome;
    private Guid lastIncomingRequest;
    private CancellationTokenSource? incomingPromptCancellation;

    private void InitializeFilelessPairingUi()
    {
#if FILELESS_PAIRING_DIAGNOSTICS
        InitializePairingDiagnostics();
#endif
        HostFilelessPairingStart.Visibility = Visibility.Visible;
        HostFilelessPairingStart.IsEnabled = false;
        HostFilelessPairingStart.Click += StartFilelessPairing_Click;
        HostFilelessPairingCancel.Click += CancelFilelessPairing_Click;
        PeerFilelessPairingCancel.Click += CancelFilelessPairing_Click;
        HostPairingRecovery.Visibility = Visibility.Visible;
        PeerPairingRecovery.Visibility = Visibility.Visible;
        HostPairingRecovery.Click += async (_, _) => await WorkAsync(ReviewHostRecoveryAsync);
        PeerPairingRecovery.Click += async (_, _) => await WorkAsync(ReviewPeerRecoveryAsync);
        DiscoveryChoices.SelectionChanged += (_, _) => RefreshFilelessPairingControls();
    }

    private void RefreshFilelessPairingControls()
    {
        var run = discoveryLifetime.Current;
        HostFilelessPairingStart.IsEnabled = HostDiscoveryPage && discoveryIsScan && !selectingCandidate && !filelessStarting &&
            unsavedInvitation is null && host is not null && run is not null && !run.Completion.IsCompleted &&
            DiscoveryChoices.SelectedItem is CandidateDiscoveryChoice;
    }

    private void FinishFilelessPairingStatus(bool hostSide, long discoveryGeneration, long sessionGeneration, bool recoveryPending = false)
    {
        if (discoveryGeneration != discoveryUiEpoch || sessionGeneration != filelessEpoch ||
            !(hostSide ? HostDiscoveryPage : PeerDiscoveryPage)) return;
        var status = hostSide ? HostDiscoveryStatus : PeerDiscoveryStatus;
        if (status.Text is not ("正在请求对方确认…" or "正在建立安全连接…" or "连接已建立，请核对双方指纹。" or
            "本机已确认，正在等待对方确认…" or "本机已确认，正在等待主控确认…" or "双方已确认，正在完成配对…")) return;
        status.Text = filelessDeliveryOutcome switch
        {
            DeliveryOutcome.Running or DeliveryOutcome.Unknown => "配对结果待确认，请查看未完成的配对。",
            DeliveryOutcome.Completed or DeliveryOutcome.Aborted or DeliveryOutcome.PeerAbsent => Feedback.Text,
            _ when recoveryPending => "存在未完成的配对，请查看未完成的配对。",
            _ => hostSide ? "安全配对未完成或已取消，请重新扫描后重试。"
                          : "安全配对未完成或已取消，请重新开启发现后重试。"
        };
    }

    private async void StartFilelessPairing_Click(object sender, RoutedEventArgs e)
    {
        var run = discoveryLifetime.Current;
        var currentHost = host;
        var currentPairing = pairing;
        var currentChanges = changes;
        if (busy || !HostDiscoveryPage || selectingCandidate || filelessStarting || unsavedInvitation is not null ||
            !discoveryIsScan || run is null || currentHost is null || currentPairing is null || currentChanges is null ||
            DiscoveryChoices.SelectedItem is not CandidateDiscoveryChoice choice)
        {
            HostDiscoveryStatus.Text = "请先扫描并选择要配对的电脑。";
            return;
        }

        var discoveryGeneration = discoveryUiEpoch;
        var sessionGeneration = ++filelessEpoch;
        filelessDeliveryOutcome = DeliveryOutcome.None;
        filelessStarting = selectingCandidate = true;
        HostFilelessPairingStart.IsEnabled = false;
        HostFilelessPairingCancel.Visibility = Visibility.Visible;
        HostDiscoveryStatus.Text = "正在请求对方确认…";
        Task? remote = null;
        var recoveryPending = false;
        try
        {
            var currentVault = vault ?? throw new InvalidOperationException("请先准备主控。");
            var recovery = await currentVault.GetPairingRecoveryStatusAsync();
            if (sessionGeneration != filelessEpoch || discoveryGeneration != discoveryUiEpoch ||
                !HostDiscoveryPage || !ReferenceEquals(vault, currentVault)) return;
            var recovering = recovery.Phase is PairingRecoveryPhase.AwaitingPeer or PairingRecoveryPhase.CommittedAwaitingPeer;
            recoveryPending = recovery.Phase != PairingRecoveryPhase.None;
            if (!recovering && recovery.Phase != PairingRecoveryPhase.None)
                throw new InvalidOperationException("存在待处理的配对，请先查看未完成的配对。");
            if (recovering && !AskBlueConfirmation("恢复未完成的配对", $"与 {recovery.PeerName} 的上次配对结果待确认。继续与选中的设备重新核对并恢复？", "继续核对", CancellationToken.None)) return;
            if (sessionGeneration != filelessEpoch || discoveryGeneration != discoveryUiEpoch || !HostDiscoveryPage) return;
            var started = await filelessHost.StartAsync(async ct =>
            {
                var selected = await run.SelectCandidateAsync(run.Generation, choice.Id, ct).ConfigureAwait(false);
                try
                {
                    await discoveryLifetime.StopAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return await SecurePairingHost.StartAsync(currentHost, selected, ct).ConfigureAwait(false);
                }
                catch (Exception startup)
                {
                    // Startup may fail before it claims ownership (including cancellation).
                    try { await selected.Lease.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup) { throw new PairingStartupCleanupException(startup, [cleanup]); }
                    throw;
                }
            });
            var session = filelessHost.Current;
            if (!started || session is null || sessionGeneration != filelessEpoch ||
                discoveryGeneration != discoveryUiEpoch || !HostDiscoveryPage || !ReferenceEquals(host, currentHost)) return;
            DiscoveryChoices.ItemsSource = null;
            HostDiscoveryStatus.Text = "连接已建立，请核对双方指纹。";

            // The peer confirmation is read concurrently; it never substitutes for this user's confirmation.
            remote = session.WaitForRemoteConfirmationAsync();
            var accepted = AskFilelessPairing(
                "核对本次安全连接",
                $"请逐字核对对方设备上的本机键鼠指纹：\n\n{session.RemoteFingerprint}\n\n" +
                $"请让对方核对本机控制通道指纹：\n\n{session.LocalFingerprint}\n\n" +
                (recovering ? "两边一致后再确认。接下来将核验并恢复上次配对；结果可能是完成或安全终止。"
                    : "两边一致后再确认。双方确认后将保存配对，允许本机连接并控制对方设备。"),
                "一致，确认", session.LifetimeToken);
            if (!accepted || sessionGeneration != filelessEpoch || discoveryGeneration != discoveryUiEpoch || !HostDiscoveryPage ||
                !ReferenceEquals(filelessHost.Current, session)) return;
            await session.ConfirmAsync();
            HostDiscoveryStatus.Text = "本机已确认，正在等待对方确认…";
            await remote;
            if (!session.BothConfirmed) throw new InvalidOperationException("尚未收到双方确认。");
            if (sessionGeneration != filelessEpoch || discoveryGeneration != discoveryUiEpoch || !HostDiscoveryPage ||
                !ReferenceEquals(filelessHost.Current, session) || !ReferenceEquals(pairing, currentPairing) ||
                !ReferenceEquals(changes, currentChanges) || busy) return;
            // The candidate guard is checked before marking a transaction as started.
            FilelessPairingAvailability.EnsureCompletionAvailable();
            var finished = filelessDeliveryFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            filelessDeliveryOutcome = DeliveryOutcome.Running;
            try
            {
                HostDiscoveryStatus.Text = "双方已确认，正在完成配对…";
                var completed = recovering
                    ? await session.RecoverHistoricalPairingAsync(currentPairing, session.LifetimeToken)
                    : await session.CompletePairingAsync(currentPairing, session.LifetimeToken);
                if (completed is null)
                {
                    filelessDeliveryOutcome = DeliveryOutcome.Aborted;
                    Feedback.Text = "上次配对已安全终止，设备未因此配对。";
                    if (sessionGeneration == filelessEpoch && HostDiscoveryPage) HostDiscoveryStatus.Text = Feedback.Text;
                    return;
                }
                filelessDeliveryOutcome = DeliveryOutcome.Completed;
                try
                {
                    HostKeyboardSnapshot = null;
                    await CloseHostMiniAsync();
                    await CancelTransferAsync();
                    await StopHostPreviewAsync();
                    await currentChanges.RebuildAfterFilelessPairingAsync(completed, CancellationToken.None);
                    RefreshDevices();
                    Feedback.Text = "设备已配对，共享已重启。请在对方电脑开始连接。";
                }
                catch
                {
                    RefreshDevices();
                    Feedback.Text = "设备已配对，但共享重启未完成；请手动重新启动共享。";
                }
                if (sessionGeneration == filelessEpoch && HostDiscoveryPage) HostDiscoveryStatus.Text = Feedback.Text;
            }
            catch
            {
                if (filelessDeliveryOutcome != DeliveryOutcome.Completed)
                {
                    filelessDeliveryOutcome = DeliveryOutcome.Unknown;
                    Feedback.Text = "配对结果待确认，请查看未完成的配对。";
                }
                throw;
            }
            finally { filelessDeliveryFinished = null; finished.TrySetResult(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (sessionGeneration == filelessEpoch && discoveryGeneration == discoveryUiEpoch && HostDiscoveryPage)
            {
                if (filelessDeliveryOutcome == DeliveryOutcome.Unknown) HostDiscoveryStatus.Text = Feedback.Text;
                else if (filelessDeliveryOutcome == DeliveryOutcome.Completed)
                    HostDiscoveryStatus.Text = Feedback.Text = "设备已配对，但共享重启未完成；请手动重新启动共享。";
                else if (error is NotSupportedException) HostDiscoveryStatus.Text = recoveryPending
                    ? "存在未完成的配对，请查看未完成的配对。" : "免文件配对尚未开放。";
                else ShowFailure(error, "安全配对未完成");
            }
        }
        finally
        {
            if (sessionGeneration == filelessEpoch)
            {
                try { await filelessHost.StopAsync(); }
                catch (Exception error) { if (sessionGeneration == filelessEpoch) ShowFailure(error, "停止安全配对"); }
                if (sessionGeneration == filelessEpoch)
                {
                    FinishFilelessPairingStatus(true, discoveryGeneration, sessionGeneration, recoveryPending);
                    filelessStarting = selectingCandidate = false;
                    RefreshFilelessPairingControls();
                    HostFilelessPairingCancel.Visibility = Visibility.Collapsed;
                }
            }
            if (remote is not null)
                try { await remote; } catch { /* The current session already reports failure or cancellation. */ }
        }
    }

    private async void CancelFilelessPairing_Click(object sender, RoutedEventArgs e)
    {
        var expectedSessionGeneration = filelessEpoch + 1;
        var discoveryGeneration = discoveryUiEpoch;
        try
        {
            await StopFilelessPairingUiAsync();
            if (expectedSessionGeneration != filelessEpoch || discoveryGeneration != discoveryUiEpoch) return;
            var status = filelessDeliveryOutcome switch {
                DeliveryOutcome.Unknown => "配对结果待确认，请查看未完成的配对。",
                DeliveryOutcome.Completed => "设备已配对，请查看设备状态。",
                DeliveryOutcome.Aborted => "上次配对已安全终止，设备未因此配对。",
                DeliveryOutcome.PeerAbsent => "本机未收到上次配对，请在主控查看最终状态。",
                _ => "本次配对尝试已停止；如有未完成的配对，请查看未完成的配对。"
            };
            if (HostDiscoveryPage) HostDiscoveryStatus.Text = status;
            if (PeerDiscoveryPage) PeerDiscoveryStatus.Text = status;
        }
        catch (Exception error)
        {
            if (expectedSessionGeneration == filelessEpoch && discoveryGeneration == discoveryUiEpoch)
                ShowFailure(error, "停止安全配对");
        }
    }

    private async Task StopFilelessPairingUiAsync()
    {
        var stoppedGeneration = ++filelessEpoch;
        // Signal both cancellation paths before waiting for either one to drain.
        incomingPromptCancellation?.Cancel();
        var stopHost = filelessHost.StopAsync();
        var stopPeer = filelessPeer.StopAsync();
        await Task.WhenAll(stopHost, stopPeer);
        var delivery = filelessDeliveryFinished;
        if (delivery is not null) await delivery.Task;
        if (stoppedGeneration != filelessEpoch) return;
        filelessStarting = selectingCandidate = false;
        incomingPromptOpen = filelessPeerHandoff = false; lastIncomingRequest = Guid.Empty;
        RefreshFilelessPairingControls();
        HostFilelessPairingCancel.Visibility = Visibility.Collapsed;
        PeerFilelessPairingCancel.Visibility = Visibility.Collapsed;
    }

    private void ObserveFilelessPairingRequest()
    {
        var run = discoveryLifetime.Current;
        if (busy || discoveryIsScan || !PeerDiscoveryPage || run is null || incomingPromptOpen || filelessPeer.Current is not null) return;
        var request = run.GetPendingPairingRequest();
        if (request is null || request.Id == lastIncomingRequest) return;
        lastIncomingRequest = request.Id;
        incomingPromptOpen = true;
        filelessDeliveryOutcome = DeliveryOutcome.None;
        _ = HandleFilelessPairingRequestAsync(run, request, discoveryUiEpoch, filelessEpoch);
    }

    private async Task ReviewHostRecoveryAsync()
    {
        if (filelessStarting || filelessHost.Current is not null || filelessPeer.Current is not null || incomingPromptOpen)
            throw new InvalidOperationException("请先停止正在进行的安全配对。");
        // A failed or still-draining startup must not be mistaken for an idle session.
        await Task.WhenAll(filelessHost.StopAsync(), filelessPeer.StopAsync());
        var current = vault ?? throw new InvalidOperationException("请先准备主控，再查看未完成的配对。");
        var reviewed = await current.GetPairingRecoveryStatusAsync();
        if (reviewed.Phase == PairingRecoveryPhase.AbortedCleanup)
        {
            Feedback.Text = await ConfirmAbortedCleanupAsync(
                () => AskBlueConfirmation("已终止的配对", "这次配对已安全终止，但还留有本地记录。清理这些遗留记录？\n\n这台设备没有因此配对，已有配对不会被移除。", "确认清理", CancellationToken.None),
                () => current.CleanupAbortedPairingAsync(reviewed));
            return;
        }
        if (reviewed.Phase == PairingRecoveryPhase.AppliedCleanup)
        {
            var result = AskBlueConfirmation("未完成的配对", "已核验的配对存在遗留记录。清理这次配对的遗留记录？\n\n已有配对不会被移除。", "确认清理", CancellationToken.None);
            if (!result) { Feedback.Text = "保留配对遗留记录。"; return; }
            Feedback.Text = await current.CleanupAppliedPairingAsync(reviewed)
                ? "已清理配对遗留记录。" : "配对状态已变化，请重新查看。";
            return;
        }
        if (reviewed.Phase == PairingRecoveryPhase.Unsent)
        {
            var result = AskBlueConfirmation("未完成的配对", $"与 {reviewed.PeerName} 的配对尚未发送。放弃这次未发送的配对？\n\n已配对设备不会被移除。", "确认放弃", CancellationToken.None);
            if (!result) { Feedback.Text = "保留未发送的配对。"; return; }
            Feedback.Text = await current.AbandonUnsentPairingAsync(reviewed)
                ? "已放弃这次未发送的配对。"
                : "配对状态已变化，请重新查看。";
            return;
        }
        Feedback.Text = RecoveryMessage(reviewed.Phase);
        ShowBlueNotice("未完成的配对", Feedback.Text);
    }

    private static async Task<string> ConfirmAbortedCleanupAsync(Func<bool> confirm, Func<Task<bool>> cleanup)
    {
        if (!confirm()) return "已保留终止配对的遗留记录。";
        return await cleanup() ? "这次配对已取消，遗留记录已清理。" : "配对状态已变化，请重新查看。";
    }

    private async Task RequireSettledPeerPairingAsync()
    {
        var status = await (peerStore ?? throw new InvalidOperationException("请先准备配对存储。"))
            .GetPairingRecoveryStatusAsync();
        if (status.Phase != PairingRecoveryPhase.None)
            throw new InvalidOperationException("存在待处理的配对，请先查看未完成的配对。");
    }

    private async Task ReviewPeerRecoveryAsync()
    {
        if (filelessStarting || filelessHost.Current is not null || filelessPeer.Current is not null || incomingPromptOpen)
            throw new InvalidOperationException("请先停止正在进行的安全配对。");
        await Task.WhenAll(filelessHost.StopAsync(), filelessPeer.StopAsync());
        await EnsurePeerStoreAsync();
        var current = peerStore!;
        var reviewed = await current.GetPairingRecoveryStatusAsync();
        if (reviewed.Phase == PairingRecoveryPhase.Unsent)
        {
            var result = AskBlueConfirmation("未完成的配对", $"与 {reviewed.PeerName} 的配对尚未发送确认。放弃这次未发送的配对？\n\n已有配对不会被移除。", "确认放弃", CancellationToken.None);
            if (!result) { Feedback.Text = "保留未发送的配对。"; return; }
            Feedback.Text = await current.AbandonUnsentPairingAsync(reviewed)
                ? "已放弃这次未发送的配对。"
                : "配对状态已变化，请重新查看。";
            return;
        }
        Feedback.Text = RecoveryMessage(reviewed.Phase);
        ShowBlueNotice("未完成的配对", Feedback.Text);
    }

    private static string RecoveryMessage(PairingRecoveryPhase phase) => phase switch
    {
        PairingRecoveryPhase.None => "当前没有未完成的配对。",
        PairingRecoveryPhase.AwaitingPeer or PairingRecoveryPhase.CommittedAwaitingPeer =>
            "上次配对的结果待确认。恢复开放后，请在主控重新发现并选择原设备，双方再次核对指纹。",
        PairingRecoveryPhase.CompletedAwaitingHost =>
            "本机已保存配对记录，正在等待主控完成确认。恢复开放后，请在主控重新发现并选择本机，双方再次核对指纹。",
        PairingRecoveryPhase.EvidenceProblem => "上次配对资料需要检查。请保留现有资料。",
        _ => "配对状态需要重新检查。"
    };

    private async Task HandleFilelessPairingRequestAsync(CandidateDiscoveryRun run, PairingIncomingRequest request,
        long discoveryGeneration, long sessionGeneration)
    {
        Task? remote = null;
        CancellationTokenSource? prompt = null;
        try
        {
            using var requestCancellation = new CancellationTokenSource();
            incomingPromptCancellation = prompt = requestCancellation;
            var remaining = request.ExpiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) { run.RejectPairingRequest(request.Id); return; }
            requestCancellation.CancelAfter(remaining);
            var proceed = AskFilelessPairing("安全配对请求",
                $"收到来自 {request.HostName} 的安全配对请求。该请求尚未验证。\n\n" +
                $"对方声明的控制通道指纹：\n{request.HostFingerprint}\n\n" +
                "允许继续后仍需双方核对本次连接的指纹；此时不会授权控制。",
                "继续核对", requestCancellation.Token);
            if (ReferenceEquals(incomingPromptCancellation, requestCancellation)) incomingPromptCancellation = null;
            if (!proceed || discoveryGeneration != discoveryUiEpoch || sessionGeneration != filelessEpoch ||
                !PeerDiscoveryPage || !ReferenceEquals(discoveryLifetime.Current, run))
            {
                run.RejectPairingRequest(request.Id);
                return;
            }
            if (peer is not null) throw new InvalidOperationException("请先停止当前连接。");
            await EnsurePeerStoreAsync();
            var currentStore = peerStore ?? throw new InvalidOperationException("无法准备配对存储。");
            if (discoveryGeneration != discoveryUiEpoch || sessionGeneration != filelessEpoch || !PeerDiscoveryPage) return;
            // The accepted request transfers to a separate network observation; discovery expiry alone does not cancel it.
            filelessPeerHandoff = true;
            PeerFilelessPairingCancel.Visibility = Visibility.Visible;
            PeerDiscoveryStatus.Text = "正在建立安全连接…";
            var profile = System.IO.Path.Combine(root, "client", "keyboard");
            var started = await filelessPeer.StartAsync(ct => SecurePairingPeer.AcceptAsync(request, profile, ct));
            var session = filelessPeer.Current;
            if (!started || session is null || discoveryGeneration != discoveryUiEpoch || sessionGeneration != filelessEpoch || !PeerDiscoveryPage) return;
            PeerDiscoveryStatus.Text = "连接已建立，请核对双方指纹。";
            remote = session.WaitForRemoteConfirmationAsync();
            var confirmed = AskFilelessPairing("核对本次安全连接",
                $"请逐字核对主控上显示的控制通道指纹：\n\n{session.RemoteFingerprint}\n\n" +
                $"请让主控核对本机键鼠指纹：\n\n{session.LocalFingerprint}\n\n" +
                "两边一致后再确认。双方确认后将进行新配对或恢复未完成的配对；成功配对后允许主控连接并控制本机。",
                "一致，确认", session.LifetimeToken);
            if (!confirmed || discoveryGeneration != discoveryUiEpoch || sessionGeneration != filelessEpoch ||
                !PeerDiscoveryPage || !ReferenceEquals(filelessPeer.Current, session)) return;
            await session.ConfirmAsync();
            PeerDiscoveryStatus.Text = "本机已确认，正在等待主控确认…";
            await remote;
            if (!session.BothConfirmed) throw new InvalidOperationException("尚未收到双方确认。");
            if (discoveryGeneration != discoveryUiEpoch || sessionGeneration != filelessEpoch || !PeerDiscoveryPage ||
                !ReferenceEquals(filelessPeer.Current, session) || !ReferenceEquals(peerStore, currentStore) || peer is not null || busy) return;
            FilelessPairingAvailability.EnsureCompletionAvailable();
            // A previously reviewed file must not be confirmed after this new transaction starts.
            hostPreview = null;
            InvitationDetails.Text = "";
            var finished = filelessDeliveryFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            filelessDeliveryOutcome = DeliveryOutcome.Running;
            try
            {
                PeerDiscoveryStatus.Text = "双方已确认，正在完成配对…";
                var applied = await session.AcceptPairingOperationAsync(currentStore, session.LifetimeToken);
                if (!applied)
                {
                    filelessDeliveryOutcome = DeliveryOutcome.PeerAbsent;
                    Feedback.Text = "本机未收到上次配对，请在主控查看最终状态。";
                    if (sessionGeneration == filelessEpoch && PeerDiscoveryPage) PeerDiscoveryStatus.Text = Feedback.Text;
                    return;
                }
                var invitation = currentStore.Get() ?? throw new InvalidOperationException("配对资料暂不可用。");
                peerReceiveDirectory = peerSettings!.Get(InvitationKey(invitation)).ReceiveDirectory;
                peerClipboardEnabled = false; peerClipboardCursor = null; peerPairingConfirmed = true;
                filelessDeliveryOutcome = DeliveryOutcome.Completed;
                Feedback.Text = "配对已保存。现在可以开始连接。";
                RefreshPairingGuide();
                if (sessionGeneration == filelessEpoch && PeerDiscoveryPage) PeerDiscoveryStatus.Text = Feedback.Text;
            }
            catch
            {
                filelessDeliveryOutcome = DeliveryOutcome.Unknown;
                Feedback.Text = "配对结果待确认，请查看未完成的配对。";
                throw;
            }
            finally { filelessDeliveryFinished = null; finished.TrySetResult(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (discoveryGeneration == discoveryUiEpoch && sessionGeneration == filelessEpoch && PeerDiscoveryPage)
            {
                if (filelessDeliveryOutcome == DeliveryOutcome.Unknown) PeerDiscoveryStatus.Text = Feedback.Text;
                else if (error is NotSupportedException) PeerDiscoveryStatus.Text = "免文件配对尚未开放。";
                else ShowFailure(error, "安全配对未完成");
            }
        }
        finally
        {
            if (ReferenceEquals(incomingPromptCancellation, prompt)) incomingPromptCancellation = null;
            if (sessionGeneration == filelessEpoch)
            {
                try { await filelessPeer.StopAsync(); }
                catch (Exception error) { if (sessionGeneration == filelessEpoch) ShowFailure(error, "停止安全配对"); }
                if (sessionGeneration == filelessEpoch)
                {
                    FinishFilelessPairingStatus(false, discoveryGeneration, sessionGeneration);
                    incomingPromptOpen = filelessPeerHandoff = false;
                    PeerFilelessPairingCancel.Visibility = Visibility.Collapsed;
                }
            }
            if (remote is not null)
                try { await remote; } catch { /* The current session already reports failure or cancellation. */ }
        }
    }

    private bool AskFilelessPairing(string title, string message, string acceptLabel, CancellationToken cancellation)
        => AskBlueConfirmation(title, message, acceptLabel, cancellation);
}
#endif
