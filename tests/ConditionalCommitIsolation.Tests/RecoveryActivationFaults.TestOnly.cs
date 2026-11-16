using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopToolbox.DeskLink;

// Applied only after a real vault permit was claimed; no original stream is exposed.
internal static class RecoveryActivationFaults
{
    internal static byte[] Mutate(byte[] original, RecoveryAuthorizationFault fault)
    {
        var node = JsonNode.Parse(original) ?? throw new InvalidDataException("Invalid test authorization frame.");
        switch (fault)
        {
            case RecoveryAuthorizationFault.Role: node["role"] = "peer"; break;
            case RecoveryAuthorizationFault.Type: node["type"] = "other"; break;
            case RecoveryAuthorizationFault.Version: node["version"] = 2; break;
            case RecoveryAuthorizationFault.RecoveryId: node["recoveryId"] = Guid.NewGuid(); break;
            case RecoveryAuthorizationFault.RequestId: node["requestId"] = Guid.NewGuid(); break;
            case RecoveryAuthorizationFault.HostChallenge: node["hostChallenge"] = RecoveryActivationProtocol.Challenge(); break;
            case RecoveryAuthorizationFault.PeerChallenge: node["peerChallenge"] = RecoveryActivationProtocol.Challenge(); break;
            case RecoveryAuthorizationFault.Expiry: node["expiresAtUtc"] = DateTimeOffset.UtcNow.AddHours(1); break;
            case RecoveryAuthorizationFault.Identity: node["identity"]!["hostName"] = "other"; break;
            case RecoveryAuthorizationFault.Offer: node["offer"]!["platform"] = "mac"; break;
            case RecoveryAuthorizationFault.Stage: node["stageId"] = Guid.NewGuid(); break;
            case RecoveryAuthorizationFault.Slot: node["slot"] = 999; break;
            case RecoveryAuthorizationFault.DataPortBase: node["dataPortBase"] = 60000; break;
            case RecoveryAuthorizationFault.Host: node["host"] = "127.0.0.2"; break;
            case RecoveryAuthorizationFault.ControlPort: node["controlPort"] = 60001; break;
            case RecoveryAuthorizationFault.KeyboardPort: node["keyboardPort"] = 60002; break;
            case RecoveryAuthorizationFault.DataPort: node["dataPort"] = 60003; break;
            case RecoveryAuthorizationFault.Digest: node["invitationDigest"] = new string('A', 64); break;
            case RecoveryAuthorizationFault.Missing: node.AsObject().Remove("stageId"); break;
            case RecoveryAuthorizationFault.Extra: node["unexpected"] = true; break;
            case RecoveryAuthorizationFault.CaseCollision: node["Type"] = "other"; break;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(node, JsonLineProtocol.Json);
        if (fault == RecoveryAuthorizationFault.Duplicate)
        {
            var duplicate = Encoding.UTF8.GetString(bytes).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
            CryptographicOperations.ZeroMemory(bytes); bytes = Encoding.UTF8.GetBytes(duplicate);
        }
        if (fault == RecoveryAuthorizationFault.InvalidUtf8) { CryptographicOperations.ZeroMemory(bytes); bytes = [0xff]; }
        if (fault == RecoveryAuthorizationFault.Oversize) { CryptographicOperations.ZeroMemory(bytes); bytes = Enumerable.Repeat((byte)'x', 8192).ToArray(); }
        return bytes;
    }
}
