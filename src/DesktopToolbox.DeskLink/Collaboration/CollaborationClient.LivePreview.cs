using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace DesktopToolbox.DeskLink.Collaboration;

public sealed partial class CollaborationClient
{
    private readonly LivePreviewClient livePreview;
    public LivePreviewClient LivePreview => livePreview;
    internal async Task<(TcpClient Socket, SslStream Secure)> OpenLiveAsync(string action, LivePreviewDemand demand, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var socket = new TcpClient { NoDelay = true };
        SslStream? secure = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(bundle.Host, port, deadline.Token);
            secure = new SslStream(socket.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
                DeskLinkFingerprint.Matches(bundle.CertificateSha256, Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))));
            await secure.AuthenticateAsClientAsync(new() { TargetHost = "nian-desklink", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, EncryptionPolicy = EncryptionPolicy.RequireEncryption }, deadline.Token);
            var id = Guid.NewGuid().ToString("N");
            await CollaborationProtocol.WriteAsync(secure, new CollaborationRequest(1, id, bundle.Token, action, CollaborationProtocol.Element(demand)), deadline.Token);
            var response = await CollaborationProtocol.ReadAsync<CollaborationResponse>(secure, deadline.Token);
            if (response.Version != 1 || response.Id != id) throw new InvalidDataException("Data response did not match this request.");
            if (!response.Ok)
            {
                if (response.Code is "unauthorized" or "denied") throw new UnauthorizedAccessException(response.Error);
                if (response.Code == "busy") throw new InvalidOperationException(response.Error);
                if (response.Code == "unsupported") throw new NotSupportedException(response.Error);
                throw new IOException(response.Error ?? "Live preview request failed.");
            }
            return (socket, secure);
        }
        catch { if (secure is not null) await secure.DisposeAsync(); socket.Dispose(); throw; }
    }
}
