using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.DeskLink;
using NianLink.MultiDevice;

internal static partial class Program
{
    // Proposed safety target / expected RED: production WPF, real client and
    // loopback TLS with a synthetic keyboard process and synthetic status IPC.
    private static async Task VerifyLateSwitchAfterDisconnectAsync(MainWindow window, string profile, string mode)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var signingKey = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=nian-desklink", signingKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = generated.Export(X509ContentType.Pkcs12);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var controlListener = new TcpListener(IPAddress.Loopback, 0);
        controlListener.Start();
        using var ipcListener = new TcpListener(IPAddress.Loopback, 0);
        ipcListener.Start();
        using var ipcClient = new TcpClient();
        await ipcClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)ipcListener.LocalEndpoint).Port);
        using var ipcServer = await ipcListener.AcceptTcpClientAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            using var socket = await controlListener.AcceptTcpClientAsync();
            await using var tls = new SslStream(socket.GetStream(), false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
            using var frame = JsonDocument.Parse((await JsonLineProtocol.ReadAsync(tls))!);
            if (frame.RootElement.GetProperty("action").GetString() != "switch" || frame.RootElement.GetProperty("target").GetString() != "host")
                throw new InvalidOperationException("Expected the real client to request a synthetic return.");
            var requestId = frame.RootElement.GetProperty("requestId").GetString();
            entered.TrySetResult();
            await release.Task;
            await JsonLineProtocol.WriteAsync(tls, new { version = 1, requestId, ok = mode != "late-switch-denied", error = "synthetic rejection" });
        });
        var invitation = new MultiPeerInvitation { Host = "127.0.0.1", Port = ((IPEndPoint)controlListener.LocalEndpoint).Port,
            HostName = "host", PeerName = "peer", Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CertificateSha256 = Convert.ToHexString(SHA256.HashData(certificate.RawData)) };
        var process = new LateSwitchFakeProcess();
        var dependencies = new DeskflowSessionDependencies { HasConflictingCore = _ => false,
            StartProcessAsync = (_, _) => Task.FromResult<IDeskflowOwnedProcess>(process),
            ConnectCoreIpcAsync = (_, _) => Task.FromResult<Stream>(ipcClient.GetStream()) };
        await using var peer = new WindowsPeerLink(Path.Combine(profile, "peer"), Path.Combine(profile, "synthetic-engine.exe"), invitation, true, dependencies);
        await using var store = await PeerInvitationStore.OpenAsync(Path.Combine(profile, "store"), new LateSwitchSyntheticProtector());
        try
        {
            await store.SaveConfirmedAsync(invitation, invitation.CertificateSha256);
            await peer.StartAsync();
            await JsonLineProtocol.ReadAsync(ipcServer.GetStream()).WaitAsync(TimeSpan.FromSeconds(5));
            await ipcServer.GetStream().WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n"));
            var keyboard = (DeskflowSession)typeof(MultiPeerClientLink).GetField("keyboard", flags)!.GetValue(peer)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (keyboard.CurrentState.ConnectionState != DeskLinkConnectionState.Connected) await Task.Delay(10, deadline.Token);
            typeof(MainWindow).GetField("peer", flags)!.SetValue(window, peer);
            typeof(MainWindow).GetField("peerStore", flags)!.SetValue(window, store);
            var owner = typeof(MainWindow).GetField("peerControlOwner", flags)!.GetValue(window)!;
            owner.GetType().GetMethod("Bind", flags)!.Invoke(owner, [peer]);
            typeof(MainWindow).GetMethod("ReturnPeer_Click", flags)!.Invoke(window, [window, new RoutedEventArgs()]);
            await entered.Task.WaitAsync(deadline.Token);
            if (mode == "red-late-switch-disconnect") ipcServer.Dispose();
            else if (mode == "red-late-switch-reconnect")
                await ipcServer.GetStream().WriteAsync(Encoding.UTF8.GetBytes("connectionState=Disconnected\n"));
            if (mode is "red-late-switch-disconnect" or "red-late-switch-reconnect")
                while (keyboard.CurrentState.ConnectionState != DeskLinkConnectionState.Disconnected) await Task.Delay(10, deadline.Token);
            if (mode == "red-late-switch-reconnect")
            {
                await ipcServer.GetStream().WriteAsync(Encoding.UTF8.GetBytes("connectionState=Connected\n"));
                while (keyboard.CurrentState.ConnectionState != DeskLinkConnectionState.Connected) await Task.Delay(10, deadline.Token);
            }
            if (mode == "late-switch-unrelated")
            {
                var version = keyboard.CurrentState.SessionStateVersion;
                await ipcServer.GetStream().WriteAsync(Encoding.UTF8.GetBytes("synthetic-log=ignored\nconnectionState=Connected\n"));
                while (keyboard.CurrentState.SessionStateVersion < version + 2) await Task.Delay(10, deadline.Token);
            }
            if (mode == "late-switch-process-error") process.ThrowOnInspection = true;
            release.TrySetResult();
            await serverTask.WaitAsync(deadline.Token);
            while ((bool)typeof(MainWindow).GetField("busy", flags)!.GetValue(window)!) await Task.Delay(10, deadline.Token);
            var feedback = (TextBlock)typeof(MainWindow).GetField("Feedback", flags)!.GetValue(window)!;
            var saysConfirmed = feedback.Text.Contains("已归还", StringComparison.Ordinal) || feedback.Text.Contains("已切换", StringComparison.Ordinal);
            if (mode is "late-switch-connected" or "late-switch-unrelated" && !saysConfirmed)
                throw new InvalidOperationException("The connected positive control did not show a confirmed return: " + feedback.Text);
            if (mode is "red-late-switch-disconnect" or "red-late-switch-reconnect" or "late-switch-denied" or "late-switch-process-error" && saysConfirmed)
                throw new InvalidOperationException("IPC state changed followed by a late synthetic switch response was presented as confirmed: " + feedback.Text);
            if (mode is "red-late-switch-disconnect" or "red-late-switch-reconnect" or "late-switch-denied" or "late-switch-process-error" &&
                !(bool)owner.GetType().GetProperty("IsUncertain", flags)!.GetValue(owner)!)
                throw new InvalidOperationException("The guard did not preserve the unconfirmed result.");
        }
        finally
        {
            release.TrySetResult();
            process.ThrowOnInspection = false;
            process.Exit();
            typeof(MainWindow).GetField("peer", flags)!.SetValue(window, null);
            typeof(MainWindow).GetField("peerStore", flags)!.SetValue(window, null);
            controlListener.Stop();
            try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        }
    }

    private sealed class LateSwitchSyntheticProtector : IMultiDeviceSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed class LateSwitchFakeProcess : IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 5678;
        public bool ThrowOnInspection { get; set; }
        public bool HasExited => ThrowOnInspection ? throw new InvalidOperationException("synthetic process inspection failure") : completion.Task.IsCompleted;
        public Task<int> Completion => completion.Task;
        public void Exit() => completion.TrySetResult(0);
        public Task StopAsync(CancellationToken ct = default) { Exit(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
