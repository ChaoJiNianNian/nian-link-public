using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using NianLink.Windows;

namespace MultiPeerTrust.Tests;

public sealed class VaultSnapshotV2Tests
{
    private const int MaxTransactions = 64;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private sealed record Device(string Name, string Fingerprint, string Token, string Platform, int Slot);
    private sealed record Transaction(Guid TransactionId, Guid StageId, Guid SessionId, Guid OfferNonce,
        string HostName, string HostFingerprint, string PeerName, string PeerFingerprint, string Platform,
        int Slot, string InvitationDigest, string CredentialDigest, string State, DateTimeOffset ExpiresAtUtc);
    private sealed record Document(int Version, Device[] Devices, Transaction[] Transactions);

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static string TokenDigest(string token)
    {
        var bytes = Convert.FromBase64String(token);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes("nian-link-vault-token-v2\0"));
            hash.AppendData(bytes);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static Transaction TransactionFor(Device entry, string state = "committed") => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "host", new string('A', 64),
        entry.Name, entry.Fingerprint, entry.Platform, entry.Slot, new string('F', 64), TokenDigest(entry.Token),
        state, DateTimeOffset.UtcNow.AddMinutes(1));
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "nian-vault-v2-" + Guid.NewGuid().ToString("N"));
    private static void Cleanup(string root)
    {
        var full = Path.GetFullPath(root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("nian-vault-v2-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe test cleanup target.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    private static async Task Seed(string root, object doc, IMultiDeviceSecretProtector protector)
    {
        Directory.CreateDirectory(root);
        var plain = JsonSerializer.SerializeToUtf8Bytes(doc, Web);
        try { await File.WriteAllBytesAsync(Path.Combine(root, "multi-device-trust.bin"), protector.Protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private static async Task<JsonObject> Read(string root, IMultiDeviceSecretProtector protector)
    {
        var plain = protector.Unprotect(await File.ReadAllBytesAsync(Path.Combine(root, "multi-device-trust.bin")));
        try { return JsonNode.Parse(plain)!.AsObject(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [Fact]
    public async Task LegacyV1OpensAndOrdinaryAddRevokeRemainV1AcrossRestart()
    {
        var root=TempRoot(); var protector=new WindowsCredentialProtector();
        var initial=new Device("one",new string('1',64),NewToken(),"windows",1);
        try
        {
            await Seed(root, new { version=1, devices=new[]{ initial } },protector);
            string second;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                Assert.True(vault.IsAuthorized("one",initial.Token));
                second=await vault.AddConfirmedAsync("two",new string('2',64));
                Assert.True(await vault.RevokeAsync("one"));
            }
            var raw=await Read(root,protector);
            Assert.Equal(1,raw["version"]!.GetValue<int>()); Assert.False(raw.ContainsKey("transactions"));
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.False(reopened.IsAuthorized("one",initial.Token)); Assert.True(reopened.IsAuthorized("two",second));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task V2AddKeepsLedgerAndRevokeMarksExactGrantWithoutRevivingOldToken()
    {
        var root=TempRoot(); var protector=new WindowsCredentialProtector();
        var initial=new Device("peer",new string('1',64),NewToken(),"windows",1);
        var old=new Device("old",new string('2',64),NewToken(),"mac",2);
        var committed=TransactionFor(initial); var revoked=TransactionFor(old,"revoked");
        try
        {
            await Seed(root,new Document(2,[initial],[committed,revoked]),protector);
            string manual;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                Assert.True(vault.IsAuthorized("peer",initial.Token));
                manual=await vault.AddConfirmedAsync("another",new string('3',64));
                var afterAdd=await Read(root,protector);
                Assert.Equal(2,afterAdd["version"]!.GetValue<int>());
                Assert.Equal(JsonSerializer.SerializeToNode(new[]{committed,revoked},Web)!.ToJsonString(),afterAdd["transactions"]!.ToJsonString());
                Assert.True(await vault.RevokeAsync("peer"));
            }
            var after=await Read(root,protector);
            Assert.Equal(2,after["version"]!.GetValue<int>());
            var items=after["transactions"]!.AsArray(); Assert.Equal(2,items.Count);
            Assert.Equal("revoked",items[0]!["state"]!.GetValue<string>());
            Assert.Equal("revoked",items[1]!["state"]!.GetValue<string>());
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.False(reopened.IsAuthorized("peer",initial.Token)); Assert.True(reopened.IsAuthorized("another",manual));
            var replacement=await reopened.AddConfirmedAsync("peer",new string('1',64));
            Assert.NotEqual(initial.Token,replacement);
            Assert.True(reopened.IsAuthorized("peer",replacement)); Assert.False(reopened.IsAuthorized("peer",initial.Token));
            Assert.Equal("revoked",(await Read(root,protector))["transactions"]![0]!["state"]!.GetValue<string>());
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("rootVersion")]
    [InlineData("rootDowngradeVersion")]
    [InlineData("rootDuplicateMixedVersion")]
    [InlineData("rootMissing")]
    [InlineData("rootExtra")]
    [InlineData("rootDuplicate")]
    [InlineData("deviceMissing")]
    [InlineData("transactionMissing")]
    [InlineData("transactionExtra")]
    [InlineData("transactionDuplicate")]
    [InlineData("badState")]
    [InlineData("emptyId")]
    [InlineData("emptyStage")]
    [InlineData("emptySession")]
    [InlineData("emptyNonce")]
    [InlineData("badSlot")]
    [InlineData("badDigest")]
    [InlineData("lowercaseDigest")]
    [InlineData("badToken")]
    [InlineData("duplicateTransaction")]
    [InlineData("duplicateStage")]
    [InlineData("duplicateCredential")]
    [InlineData("mismatchedPeer")]
    [InlineData("unmatchedCommitted")]
    [InlineData("activeRevoked")]
    [InlineData("tooMany")]
    public async Task InvalidV2IsRejectedWithoutChangingProtectedFile(string mutation)
    {
        var root=TempRoot(); var protector=new WindowsCredentialProtector();
        var device=new Device("peer",new string('1',64),NewToken(),"windows",1);
        var tx=TransactionFor(device);
        try
        {
            var rootNode=JsonSerializer.SerializeToNode(new Document(2,[device],[tx]),Web)!.AsObject();
            var txs=rootNode["transactions"]!.AsArray(); var item=txs[0]!.AsObject();
            switch(mutation)
            {
                case "rootVersion": rootNode["version"]=3; break;
                case "rootDowngradeVersion": rootNode["version"]=1; break;
                case "rootMissing": rootNode.Remove("transactions"); break;
                case "rootExtra": rootNode["extra"]=true; break;
                case "deviceMissing": rootNode["devices"]![0]!.AsObject().Remove("platform"); break;
                case "transactionMissing": item.Remove("invitationDigest"); break;
                case "transactionExtra": item["extra"]=true; break;
                case "badState": item["state"]="sent"; break;
                case "emptyId": item["transactionId"]=Guid.Empty.ToString(); break;
                case "emptyStage": item["stageId"]=Guid.Empty.ToString(); break;
                case "emptySession": item["sessionId"]=Guid.Empty.ToString(); break;
                case "emptyNonce": item["offerNonce"]=Guid.Empty.ToString(); break;
                case "badSlot": item["slot"]=5; break;
                case "badDigest": item["invitationDigest"]=new string('F',63); break;
                case "lowercaseDigest": item["credentialDigest"]=new string('a',64); break;
                case "badToken": rootNode["devices"]![0]!["token"]="not-base64"; break;
                case "duplicateTransaction": txs.Add(item.DeepClone()); break;
                case "duplicateStage":
                    var duplicateStage=item.DeepClone().AsObject(); duplicateStage["transactionId"]=Guid.NewGuid().ToString(); duplicateStage["credentialDigest"]=new string('A',64); duplicateStage["state"]="aborted"; txs.Add(duplicateStage); break;
                case "duplicateCredential":
                    var duplicateCredential=item.DeepClone().AsObject(); duplicateCredential["transactionId"]=Guid.NewGuid().ToString(); duplicateCredential["stageId"]=Guid.NewGuid().ToString(); txs.Add(duplicateCredential); break;
                case "mismatchedPeer": item["peerName"]="other"; break;
                case "unmatchedCommitted": item["credentialDigest"]=new string('A',64); break;
                case "activeRevoked": item["state"]="revoked"; break;
                case "tooMany":
                    for(var i=1;i<=MaxTransactions;i++) { var extra=TransactionFor(new Device("past"+i,new string('4',64),NewToken(),"mac",1),"aborted"); txs.Add(JsonSerializer.SerializeToNode(extra,Web)); }
                    break;
            }
            var json=rootNode.ToJsonString();
            if(mutation=="rootDuplicate") json=json.Insert(1,"\"version\":2,");
            if(mutation=="rootDuplicateMixedVersion") json=json.Replace("\"version\":2", "\"version\":2,\"Version\":1", StringComparison.Ordinal);
            if(mutation=="transactionDuplicate") json=json.Replace("\"state\":\"committed\"","\"state\":\"committed\",\"state\":\"committed\"",StringComparison.Ordinal);
            Directory.CreateDirectory(root); var bytes=Encoding.UTF8.GetBytes(json); byte[] encrypted;
            try { encrypted=protector.Protect(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
            var path=Path.Combine(root,"multi-device-trust.bin"); await File.WriteAllBytesAsync(path,encrypted);
            var error = await Record.ExceptionAsync(async () =>
            {
                await using var unexpected = await MultiDeviceCredentialVault.OpenAsync(root, protector);
            });
            Assert.IsType<InvalidDataException>(error);
            Assert.Equal(encrypted,await File.ReadAllBytesAsync(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task FailedReplacementRequiresReopenAndPreservesFile()
    {
        var root=TempRoot(); var protector=new WindowsCredentialProtector();
        var device=new Device("peer",new string('1',64),NewToken(),"windows",1);
        try
        {
            await Seed(root,new Document(2,[device],[TransactionFor(device)]),protector);
            var path=Path.Combine(root,"multi-device-trust.bin"); var original=await File.ReadAllBytesAsync(path);
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                using (var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    var error=await Record.ExceptionAsync(()=>vault.RevokeAsync("peer"));
                    Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
                    Assert.False(vault.IsAuthorized("peer",device.Token));
                }
            }
            Assert.Equal(original,await File.ReadAllBytesAsync(path));
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.True(reopened.IsAuthorized("peer",device.Token));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task UncertainWriteRejectsLoopbackTlsRequestsAndServerStops()
    {
        var root=TempRoot(); var protector=new WindowsCredentialProtector();
        var device=new Device("peer",new string('1',64),NewToken(),"windows",1);
        try
        {
            var vaultRoot=Path.Combine(root,"vault");
            await Seed(vaultRoot,new Document(2,[device],[TransactionFor(device)]),protector);
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(vaultRoot,protector);
            var certRoot=Path.Combine(root,"cert");
            await using var control=new PairedControlServer(new() { ProfileDirectory=certRoot, Port=0, AllowedTargets=["host","peer"] },
                _=>Task.FromResult(new DeskLinkState()), (_,_)=>Task.FromResult(new DeskLinkSwitchResult()));
            await control.StartAsync();
            await using var endpoint=MultiPeerCollaborationEndpoint.Create(vault,"peer",Path.Combine(root,"data"),Path.Combine(certRoot,"control.pem"),System.Net.IPAddress.Loopback,0);
            await endpoint.StartAsync();
            var invite=new MultiPeerInvitation { Host="127.0.0.1", Port=control.Port, DataPort=endpoint.Port,
                HostName="host", PeerName="peer", CertificateSha256=control.CertificateSha256!, Token=device.Token };
            await using var client=MultiPeerCollaborationClient.Open(invite,true);
            Assert.NotNull(await client.GetStatusAsync());
            using (var locked=new FileStream(Path.Combine(vaultRoot,"multi-device-trust.bin"),FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var error=await Record.ExceptionAsync(()=>vault.RevokeAsync("peer"));
                Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected failure: {error}");
            }
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>client.GetStatusAsync());
            Assert.False(vault.IsAuthorized("peer",device.Token));
            await endpoint.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task V2FailedProtectionDoesNotChangeExistingFileOrActiveSnapshot()
    {
        var root=TempRoot(); var protector=new FailingProtector();
        var device=new Device("peer",new string('1',64),NewToken(),"windows",1); var tx=TransactionFor(device);
        try
        {
            await Seed(root,new Document(2,[device],[tx]),protector);
            await using var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            var original=await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin"));
            protector.Fail=true;
            await Assert.ThrowsAsync<CryptographicException>(()=>vault.AddConfirmedAsync("another",new string('2',64)));
            await Assert.ThrowsAsync<CryptographicException>(()=>vault.RevokeAsync("peer"));
            Assert.Equal(original,await File.ReadAllBytesAsync(Path.Combine(root,"multi-device-trust.bin")));
            Assert.True(vault.IsAuthorized("peer",device.Token)); Assert.Single(vault.GetTrustedDevices());
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task MaximumLedgerSurvivesOrdinaryAddAndRestart()
    {
        var root=TempRoot(); var protector=new WindowsCredentialProtector();
        var device=new Device("peer",new string('1',64),NewToken(),"windows",1);
        var ledger=Enumerable.Range(0,MaxTransactions)
            .Select(i=>TransactionFor(new Device("past"+i,new string('2',64),NewToken(),"mac",1),"aborted"))
            .ToArray();
        try
        {
            await Seed(root,new Document(2,[device],ledger),protector);
            string token;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
                token=await vault.AddConfirmedAsync("another",new string('3',64));
            var raw=await Read(root,protector);
            Assert.Equal(JsonSerializer.SerializeToNode(ledger,Web)!.ToJsonString(),raw["transactions"]!.ToJsonString());
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.True(reopened.IsAuthorized("peer",device.Token)); Assert.True(reopened.IsAuthorized("another",token));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task ConcurrentAddAndRevokeKeepV2LedgerAndValidSnapshot()
    {
        var root=TempRoot(); var protector=new WindowsCredentialProtector();
        var device=new Device("peer",new string('1',64),NewToken(),"windows",1); var tx=TransactionFor(device);
        try
        {
            await Seed(root,new Document(2,[device],[tx]),protector);
            string token;
            await using (var vault=await MultiDeviceCredentialVault.OpenAsync(root,protector))
            {
                var add=vault.AddConfirmedAsync("other",new string('2',64)); var revoke=vault.RevokeAsync("peer");
                token=await add; Assert.True(await revoke); Assert.True(vault.IsAuthorized("other",token));
            }
            var raw=await Read(root,protector); Assert.Equal(2,raw["version"]!.GetValue<int>());
            Assert.Equal("revoked",raw["transactions"]![0]!["state"]!.GetValue<string>());
            await using var reopened=await MultiDeviceCredentialVault.OpenAsync(root,protector);
            Assert.True(reopened.IsAuthorized("other",token)); Assert.False(reopened.IsAuthorized("peer",device.Token));
        }
        finally { Cleanup(root); }
    }

    private sealed class FailingProtector : IMultiDeviceSecretProtector
    {
        private readonly WindowsCredentialProtector inner=new(); public bool Fail { get; set; }
        public byte[] Protect(byte[] data) => Fail ? throw new CryptographicException("synthetic") : inner.Protect(data);
        public byte[] Unprotect(byte[] data) => inner.Unprotect(data);
    }
}
