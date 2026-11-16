using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
namespace DesktopToolbox.DeskLink;

/// <summary>Only connects after user-approved import; certificate changes fail closed.</summary>
public sealed partial class PairedControlClient : IAsyncDisposable, IDeskflowPreviewControl
{
    private readonly DeskLinkPairingBundle bundle;
    private bool disposed;
    public PairedControlClient(DeskLinkPairingBundle bundle, bool userApproved)
    {
        ArgumentNullException.ThrowIfNull(bundle); bundle.Validate();
        if (!userApproved) throw new InvalidOperationException("The user must review and approve the pairing bundle before connecting.");
        this.bundle = bundle;
    }
    public async Task<DeskLinkState> GetStateAsync(CancellationToken ct = default)
    {
        var response = await RequestAsync("state",null,ct);
        var state = response.State ?? throw new InvalidDataException("Paired endpoint omitted its state.");
        Volatile.Write(ref observedPreviewState, state);
        return state;
    }
    public async Task<DeskLinkSwitchResult> SwitchAsync(string target,CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(target);
        if (target != bundle.WindowsName && target != bundle.MacName) throw new ArgumentException("Target is outside the paired computers.",nameof(target));
        var response = await RequestAsync("switch",target,ct);
        return response.Result ?? throw new InvalidDataException("Paired endpoint omitted its switch result.");
    }
    private async Task<PairedControlResponse> RequestAsync(string action,string? target,CancellationToken ct,string? previewId=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new TcpClient();
        await client.ConnectAsync(bundle.Host,bundle.Port,deadline.Token);
        await using var secure = new SslStream(client.GetStream(),false,(_,certificate,_,_) =>
        {
            if (certificate is null) return false;
            var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            return DeskLinkFingerprint.Matches(bundle.CertificateSha256,actual);
        });
        await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost="nian-desklink", EnabledSslProtocols=SslProtocols.Tls12 | SslProtocols.Tls13,
            EncryptionPolicy=EncryptionPolicy.RequireEncryption
        },deadline.Token);
        var id = Guid.NewGuid().ToString("N");
        await JsonLineProtocol.WriteAsync(secure,new PairedControlRequest(1,id,bundle.Token,action,target,previewId,PreviewHandoffClient:action=="state"),deadline.Token);
        var line = await JsonLineProtocol.ReadAsync(secure,deadline.Token) ?? throw new EndOfStreamException("Paired endpoint closed before replying.");
        var response = JsonSerializer.Deserialize<PairedControlResponse>(line,JsonLineProtocol.Json) ?? throw new InvalidDataException("Empty paired control response.");
        if (response.Version != 1 || response.RequestId != id) throw new InvalidDataException("Paired control response did not match this request.");
        if (!response.Ok)
        {
            if (response.Error == "unauthorized") throw new UnauthorizedAccessException("Pairing token was rejected. Import a newly approved pairing bundle.");
            if (action.StartsWith("preview-",StringComparison.Ordinal) && (response.Error is "preview unsupported" or "unsupported action or target"))
                throw new NotSupportedException("The paired service does not support keyboard-only preview handoff.");
            if (response.Error == "preview not owner") throw new UnauthorizedAccessException("Only the preview viewer can renew this session.");
            throw new InvalidOperationException(response.Error ?? "Paired control request failed.");
        }
        return response;
    }
    public ValueTask DisposeAsync() { disposed=true; return ValueTask.CompletedTask; }
}
