using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Prototype client that refuses unapproved invitations and pins the host certificate.</summary>
public sealed class MultiPeerControlClient : IAsyncDisposable
{
    private sealed record Response(int Version, string RequestId, bool Ok, string? Error, DeskflowPreviewState? Preview = null,
        DeskLinkSwitchResult? Switch = null);
    private readonly MultiPeerInvitation invitation;
    private bool disposed;

    public MultiPeerControlClient(MultiPeerInvitation invitation, bool userApproved)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        invitation.Validate();
        if (!userApproved) throw new InvalidOperationException("The user must approve this device first.");
        this.invitation = invitation;
    }

    public async Task<bool> SwitchAsync(string target, CancellationToken ct = default) => (await SwitchDetailedAsync(target, ct)).InputTransferred;

    public async Task<DeskLinkSwitchResult> SwitchDetailedAsync(string target, CancellationToken ct = default)
    {
        if (target != invitation.HostName && target != invitation.PeerName)
            throw new ArgumentException("Target is outside the approved device pair.", nameof(target));
        var response = await RequestAsync("switch", target, null, ct);
        if (response.Switch is not { } result)
            return new DeskLinkSwitchResult { Ok = response.Ok, Target = target, ActiveComputer = response.Ok ? target : null,
                InputTransferred = response.Ok, VideoStatus = "notAttempted", Error = response.Error };
        if (result.Target != target || result.VideoStatus is not ("notAttempted" or "disabled" or "verified" or "acceptedUnverified" or "failed" or "canceled") ||
            result.InputTransferred != response.Ok || result.InputTransferred && result.ActiveComputer != target ||
            result.ActiveComputer is not null && result.ActiveComputer != invitation.HostName && result.ActiveComputer != invitation.PeerName || result.Error?.Length > 1000)
            throw new InvalidDataException("Switch response does not match the request.");
        return result;
    }

    public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken ct = default) => PreviewAsync("preview-begin", invitation.HostName, null, ct);
    public Task<DeskflowPreviewState> RenewPreviewAsync(string id, CancellationToken ct = default) => PreviewAsync("preview-renew", null, id, ct);
    public Task<DeskflowPreviewState> EndPreviewAsync(string id, CancellationToken ct = default) => PreviewAsync("preview-end", null, id, ct);
    public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken ct = default) => PreviewAsync("preview-read", null, null, ct);

    private async Task<DeskflowPreviewState> PreviewAsync(string action, string? target, string? previewId, CancellationToken ct)
    {
        if (previewId is not null && (!Guid.TryParseExact(previewId, "N", out var parsed) || parsed == Guid.Empty)) throw new ArgumentException("Invalid preview identifier.");
        var response = await RequestAsync(action, target, previewId, ct);
        if (!response.Ok) throw response.Error == "preview unauthorized"
            ? new UnauthorizedAccessException("无权操作其他设备的预览。") : new InvalidOperationException("预览操作暂不可用，请检查设备状态。");
        var state = response.Preview ?? throw new InvalidDataException("Missing preview state.");
        if (!Enum.IsDefined(state.Phase) || state.Id is not null && (!Guid.TryParseExact(state.Id, "N", out var stateId) || stateId == Guid.Empty) ||
            state.Phase == DeskflowPreviewPhase.Viewing && state.Id is not null || state.Phase != DeskflowPreviewPhase.Viewing && state.Id is null ||
            state.HasSession && !((state.ViewerComputer == invitation.HostName && state.TargetComputer == invitation.PeerName) ||
                                  (state.ViewerComputer == invitation.PeerName && state.TargetComputer == invitation.HostName)))
            throw new InvalidDataException("Preview state does not match this device.");
        return state;
    }

    private async Task<Response> RequestAsync(string action, string? target, string? previewId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var socket = new TcpClient();
        await socket.ConnectAsync(invitation.Host, invitation.Port, deadline.Token);
        await using var tls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && DeskLinkFingerprint.Matches(invitation.CertificateSha256,
                Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "nian-desklink",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption
        }, deadline.Token);
        var id = Guid.NewGuid().ToString("N");
        await JsonLineProtocol.WriteAsync(tls, new
        {
            version = 1, requestId = id, peer = invitation.PeerName, token = invitation.Token,
            action, target, previewId
        }, deadline.Token);
        var frame = await JsonLineProtocol.ReadAsync(tls, deadline.Token)
            ?? throw new EndOfStreamException("Peer closed before replying.");
        var response = JsonSerializer.Deserialize<Response>(frame, JsonLineProtocol.Json)
            ?? throw new InvalidDataException("Empty control response.");
        if (response.Version != 1 || response.RequestId != id)
            throw new InvalidDataException("Control response does not match the request.");
        if (!response.Ok && response.Error == "unauthorized")
            throw new UnauthorizedAccessException("This device's pairing was revoked.");
        return response;
    }

    public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
}
