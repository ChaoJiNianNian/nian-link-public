using System.Net;
using DesktopToolbox.DeskLink;
namespace DeskLink.Tests;
public sealed class PairedControlTests
{
    [Fact]
    public async Task TlsControlRequiresManualBundleApprovalAndReturnsUnifiedSwitchResult()
    {
        using var folder=new TestFolder(); var calls=0;
        await using var server=Server(folder.Path,()=>calls++); await server.StartAsync();
        var bundle=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        Assert.Throws<InvalidOperationException>(()=>new PairedControlClient(bundle,false));
        Assert.DoesNotContain(bundle.Token,bundle.ToString());
        Assert.Equal(bundle,DeskLinkPairingBundle.Parse(bundle.ToJson()));
        await using var client=new PairedControlClient(bundle,true);
        Assert.Equal(DeskLinkConnectionState.Connected,(await client.GetStateAsync()).ConnectionState);
        var result=await client.SwitchAsync("My-Mac");
        Assert.True(result.InputTransferred); Assert.Equal("accepted",result.VideoStatus); Assert.Equal(1,calls);
    }
    [Fact]
    public async Task UnpairedTokenOrChangedCertificateCannotInvokeSwitch()
    {
        using var folder=new TestFolder(); var calls=0;
        await using var server=Server(folder.Path,()=>calls++); await server.StartAsync();
        var bundle=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        await using var badToken=new PairedControlClient(bundle with {Token=Convert.ToBase64String(new byte[32])},true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>badToken.SwitchAsync("My-Mac"));
        await using var wrongCertificate=new PairedControlClient(bundle with {CertificateSha256=new string('0',64)},true);
        await Assert.ThrowsAnyAsync<System.Security.Authentication.AuthenticationException>(()=>wrongCertificate.SwitchAsync("My-Mac"));
        Assert.Equal(0,calls);
    }
    [Fact]
    public async Task PairedClientCannotExecuteCommandsOrSwitchOutsideThePair()
    {
        using var folder=new TestFolder(); var calls=0;
        await using var server=Server(folder.Path,()=>calls++); await server.StartAsync();
        await using var client=new PairedControlClient(server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac"),true);
        await Assert.ThrowsAsync<ArgumentException>(()=>client.SwitchAsync("Other-PC"));
        await Assert.ThrowsAsync<ArgumentException>(()=>client.SwitchAsync("cmd.exe /c echo unsafe"));
        Assert.Equal(0,calls);
    }
    [Fact]
    public async Task ServerRejectsArbitraryActionsEvenWithAValidPairedToken()
    {
        using var folder=new TestFolder(); var calls=0;
        await using var server=Server(folder.Path,()=>calls++); await server.StartAsync();
        var bundle=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        using var client=new System.Net.Sockets.TcpClient(); await client.ConnectAsync(bundle.Host,bundle.Port);
        await using var secure=new System.Net.Security.SslStream(client.GetStream(),false,(_,cert,_,_)=>
            cert is not null && DeskLinkFingerprint.Matches(bundle.CertificateSha256,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData()))));
        await secure.AuthenticateAsClientAsync("nian-desklink");
        await JsonLineProtocol.WriteAsync(secure,new {version=1,requestId=Guid.NewGuid().ToString("N"),token=bundle.Token,action="execute",target="My-Mac",command="arbitrary"});
        using var response=System.Text.Json.JsonDocument.Parse((await JsonLineProtocol.ReadAsync(secure))!);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean()); Assert.Equal(0,calls);
    }
    [Fact]
    public async Task RevokingPairingPreservesTheCertificateButRejectsTheOldToken()
    {
        using var folder=new TestFolder(); await using var server=Server(folder.Path,()=>{}); await server.StartAsync();
        var first=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        await server.RegeneratePairingTokenAsync();
        var next=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        Assert.Equal(first.CertificateSha256,next.CertificateSha256); Assert.NotEqual(first.Token,next.Token);
        await using var oldClient=new PairedControlClient(first,true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>oldClient.GetStateAsync());
        await using var newClient=new PairedControlClient(next,true);
        Assert.Equal(DeskLinkConnectionState.Connected,(await newClient.GetStateAsync()).ConnectionState);
    }
    [Fact]
    public async Task DisplayBindingExportDoesNotRotatePairingSecretsOrChangeStateAndSwitch()
    {
        using var folder=new TestFolder(); var calls=0;
        await using var server=Server(folder.Path,()=>calls++); await server.StartAsync();
        var before=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        var binding=new DeskLinkDisplayBinding {Enabled=true,VendorId=0x61A9,ProductId=0x27B1,WindowsInput=15,MacInput=16};
        var exported=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac",binding);
        Assert.Equal(before.Token,exported.Token);
        Assert.Equal(before.CertificateSha256,exported.CertificateSha256);
        Assert.Equal(binding,DeskLinkPairingBundle.Parse(exported.ToJson()).Display);
        await using var client=new PairedControlClient(exported,true);
        Assert.Equal("Win-PC",(await client.GetStateAsync()).ActiveComputer);
        Assert.True((await client.SwitchAsync("My-Mac")).InputTransferred);
        Assert.Equal(1,calls);
        Assert.Throws<ArgumentException>(()=>server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac",binding with {MacInput=15}));
        var after=server.CreatePairingBundle("127.0.0.1","Win-PC","My-Mac");
        Assert.Equal(before.Token,after.Token);
        Assert.Equal(before.CertificateSha256,after.CertificateSha256);
        Assert.Null(after.Display);
    }
    private static PairedControlServer Server(string root,Action switched)=>new(
        new PairedControlServerOptions {ProfileDirectory=root,BindAddress=IPAddress.Loopback,Port=0,AllowedTargets=["Win-PC","My-Mac"]},
        _=>Task.FromResult(new DeskLinkState {ConnectionState=DeskLinkConnectionState.Connected,ActiveComputer="Win-PC"}),
        (target,_)=>{switched();return Task.FromResult(new DeskLinkSwitchResult {Ok=true,Target=target,ActiveComputer=target,InputTransferred=true,VideoStatus="accepted"});});
}
