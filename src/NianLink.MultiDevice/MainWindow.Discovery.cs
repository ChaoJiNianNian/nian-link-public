using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace NianLink.MultiDevice;

public partial class MainWindow
{
    private readonly DiscoveryUiLifetime<CandidateDiscoveryRun> discoveryLifetime = new();
    private VerifiedLanBinding? discoveryHostBinding;
    private DiscoveryReviewLease? discoveryReviewLease;
    private string? discoveryReviewId;
    private long discoveryUiEpoch;
    private bool discoveryIsScan, selectingCandidate;

    private bool HostDiscoveryPage => ModeTabs.SelectedIndex == 0 && HostPairingCard.Visibility == Visibility.Visible;
    private bool PeerDiscoveryPage => ModeTabs.SelectedIndex == 1 && PeerConnectionCard.Visibility == Visibility.Visible;

    private async void StartDiscoveryResponder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!PeerDiscoveryPage || PeerDiscoveryNetwork.SelectedItem is not IPAddress address)
                throw new InvalidOperationException("请先选择被控电脑的活动局域网。");
            var name = PeerName.Text.Trim();
            DeskflowProfile.ValidateName(name);
            var beforePrompt = discoveryUiEpoch;
            if (!AskBlueConfirmation("公开身份确认", "允许同一局域网中的设备在 30 秒内发现本机？本机名称、系统和完整键鼠指纹可能被同网段设备看到。", "同意，开启 30 秒", CancellationToken.None)) return;
            if (beforePrompt != discoveryUiEpoch || !PeerDiscoveryPage) return;
            var expectedStop = discoveryUiEpoch + 1;
            await StopDiscoveryUiAsync();
            if (expectedStop != discoveryUiEpoch || !PeerDiscoveryPage) return;
            var generation = ++discoveryUiEpoch;
            discoveryIsScan = false;
#if FILELESS_PAIRING_DIAGNOSTICS
            ResetPairingDiagnostics();
#endif
            PeerDiscoveryStatus.Text = "正在准备本机身份…";
            PeerIdentityOffer? card = null;
            var profile = Path.Combine(root, "client", "keyboard");
            var selected = new IPAddress(address.GetAddressBytes());
            var started = await discoveryLifetime.StartAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                var binding = VerifiedLanBinding.VerifyCurrentNetwork(selected, userApproved: true);
                token.ThrowIfCancellationRequested();
                card = await PeerIdentityOffer.CreateAsync(profile, name, "windows", DateTimeOffset.UtcNow, token);
                token.ThrowIfCancellationRequested();
                return CandidateDiscoveryRun.StartResponder(binding, card, TimeSpan.FromSeconds(30), token);
            });
            if (!started || generation != discoveryUiEpoch || !PeerDiscoveryPage) return;
            PeerIdentity.Text = $"本机键鼠指纹：\n{card!.KeyboardFingerprint}\n有效期至：{card.ExpiresAtUtc.ToLocalTime():HH:mm:ss}";
            PeerDiscoveryStatus.Text = "本机可被发现，最多持续 30 秒。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error, "开启局域网发现"); }
    }

    private async void StartDiscoveryScan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!HostDiscoveryPage || pairing is null || discoveryHostBinding is null || unsavedInvitation is not null)
                throw new InvalidOperationException("请先准备主控并选择活动局域网。");
            var expectedStop = discoveryUiEpoch + 1;
            await StopDiscoveryUiAsync();
            if (expectedStop != discoveryUiEpoch || !HostDiscoveryPage) return;
            var generation = ++discoveryUiEpoch;
            discoveryIsScan = true;
            HostDiscoveryStatus.Text = "正在扫描，设备尚未验证…";
            var binding = discoveryHostBinding;
            var started = await discoveryLifetime.StartAsync(token =>
                Task.FromResult(CandidateDiscoveryRun.StartScan(binding, TimeSpan.FromSeconds(30), token)));
            if (started && generation == discoveryUiEpoch && HostDiscoveryPage) RefreshDiscoveryUi();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error, "扫描局域网设备"); }
    }

    private async void ChooseDiscoveryCandidate_Click(object sender, RoutedEventArgs e)
    {
        CandidateReviewSelection? selected = null;
        try
        {
            var run = discoveryLifetime.Current;
            var coordinator = pairing;
            if (!HostDiscoveryPage || unsavedInvitation is not null || !discoveryIsScan || selectingCandidate || run is null || coordinator is null ||
                DiscoveryChoices.SelectedItem is not CandidateDiscoveryChoice choice)
                throw new InvalidOperationException("请先从扫描结果选择一台设备。");
            var generation = discoveryUiEpoch;
            selectingCandidate = true;
            selected = await run.SelectCandidateAsync(run.Generation, choice.Id);
            if (generation != discoveryUiEpoch || !HostDiscoveryPage || !ReferenceEquals(pairing, coordinator) ||
                !ReferenceEquals(discoveryLifetime.Current, run) || DateTimeOffset.UtcNow >= selected.ExpiresAtUtc)
                throw new InvalidOperationException("发现结果已失效，请重新扫描。");
            await discoveryLifetime.StopAsync();
            if (generation != discoveryUiEpoch || !HostDiscoveryPage || DateTimeOffset.UtcNow >= selected.ExpiresAtUtc)
                throw new InvalidOperationException("发现结果已失效，请重新扫描。");
            var now = DateTimeOffset.UtcNow;
            var preview = coordinator.ReviewForDiscovery(selected.Offer.ToJson(now), now, selected.Lease);
            discoveryReviewLease = selected.Lease; discoveryReviewId = preview.Id; selected = null;
            offer = preview; pairingReviewGeneration++; pairingFileSaved = false;
            OfferDetails.Text = $"未验证设备：{offer.Name} · {offer.Platform}\n键鼠指纹：\n{offer.KeyboardFingerprint}\n有效期至：{offer.ExpiresAtUtc.ToLocalTime():HH:mm:ss}";
            HostDiscoveryStatus.Text = "请与另一台电脑逐字核对完整指纹。";
            DiscoveryChoices.ItemsSource = null;
            RefreshPairingGuide();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowFailure(error, "选择发现的设备"); }
        finally
        {
            selectingCandidate = false;
            if (selected is not null) await selected.Lease.DisposeAsync();
        }
    }

    private async void StopDiscovery_Click(object sender, RoutedEventArgs e)
    {
        try { await StopDiscoveryUiAsync(); }
        catch (Exception error) { ShowFailure(error, "停止局域网发现"); }
    }

    private async void PeerDiscoveryNetwork_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        try { await StopDiscoveryUiAsync(); }
        catch (Exception error) { ShowFailure(error, "停止局域网发现"); }
    }

    private async void ModeTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, ModeTabs)) return;
        try { await StopDiscoveryUiAsync(); }
        catch (Exception error) { ShowFailure(error, "停止局域网发现"); }
    }

    private async Task ReleaseDiscoveryReviewAsync(string? id)
    {
        if (id is null || id != discoveryReviewId) return;
        var lease = discoveryReviewLease;
        discoveryReviewId = null; discoveryReviewLease = null;
        if (offer?.Id == id)
        {
            pairing?.CancelReview(id);
            offer = null; pairingReviewGeneration++; OfferDetails.Text = "";
        }
        if (lease is not null) await lease.DisposeAsync();
    }

    private async Task StopDiscoveryUiAsync()
    {
#if FILELESS_PAIRING_DIAGNOSTICS
        var diagnosticRun = discoveryLifetime.Current;
        var diagnosticStopEpoch = discoveryUiEpoch + 1;
#endif
        ++discoveryUiEpoch;
#if FILELESS_PAIRING_CANDIDATE
        await StopFilelessPairingUiAsync();
#endif
        DiscoveryChoices.ItemsSource = null;
        HostDiscoveryStatus.Text = "发现已停止。";
        PeerDiscoveryStatus.Text = "发现已停止。";
        var review = discoveryReviewId;
        try { await DiscoveryUiStop.StopAndReleaseAsync(() => ReleaseDiscoveryReviewAsync(review), () => discoveryLifetime.StopAsync()); }
        finally
        {
#if FILELESS_PAIRING_DIAGNOSTICS
            FinishPairingDiagnostics(diagnosticRun, diagnosticStopEpoch);
#endif
            RefreshPairingGuide();
        }
    }

    private async Task CloseDiscoveryUiAsync()
    {
        ++discoveryUiEpoch;
#if FILELESS_PAIRING_CANDIDATE
        await StopFilelessPairingUiAsync();
#endif
        var review = discoveryReviewId;
        await DiscoveryUiStop.StopAndReleaseAsync(() => ReleaseDiscoveryReviewAsync(review), () => discoveryLifetime.CloseAsync());
    }

    private void RefreshDiscoveryUi()
    {
        var run = discoveryLifetime.Current;
#if FILELESS_PAIRING_DIAGNOSTICS
        UpdatePairingDiagnostics(run);
#endif
        if (run is null || selectingCandidate) return;
        if (run.Completion.IsCompleted)
        {
            _ = FinishDiscoveryUiAsync(run, discoveryUiEpoch);
            return;
        }
#if FILELESS_PAIRING_CANDIDATE
        if (!discoveryIsScan && PeerDiscoveryPage)
        {
            ObserveFilelessPairingRequest();
            return;
        }
#endif
        if (!discoveryIsScan || !HostDiscoveryPage) return;
        var selected = (DiscoveryChoices.SelectedItem as CandidateDiscoveryChoice)?.Id;
        var choices = run.GetCandidateChoices();
        DiscoveryChoices.ItemsSource = choices;
        DiscoveryChoices.SelectedItem = choices.FirstOrDefault(x => x.Id == selected);
        HostDiscoveryStatus.Text = choices.Count == 0 ? "正在扫描，尚未发现设备。" : "发现未验证设备，请选择后核对指纹。";
    }

    private async Task FinishDiscoveryUiAsync(CandidateDiscoveryRun run, long generation)
    {
        if (generation != discoveryUiEpoch || !ReferenceEquals(discoveryLifetime.Current, run)) return;
        try
        {
            var error = run.Error;
#if FILELESS_PAIRING_CANDIDATE
            if (!discoveryIsScan && filelessPeerHandoff && error is null)
            {
                // Natural expiry stops only discovery; a separately approved pairing session may continue.
                await discoveryLifetime.StopAsync();
                return;
            }
#endif
            await StopDiscoveryUiAsync();
            if (error is not null) ShowFailure(error, "局域网发现已停止");
        }
        catch (Exception error) { ShowFailure(error, "停止局域网发现"); }
    }
}
