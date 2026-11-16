using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DesktopToolbox.DeskLink;

public enum ReadyWireFault { None, Role, Type, Version, Session, Transaction, OfferNonce, Stage, SenderChallenge, ReceiverChallenge, Slot, DataPortBase, Digest, Missing, Extra, CaseCollision, Duplicate, BadType, InvalidUtf8, Oversize, Truncated }
internal sealed class ReadyWireControls
{
    internal ReadyWireFault Fault { get; init; }
    internal bool Capture { get; init; }
    internal byte[]? Captured { get; set; }
    internal byte[]? Replay { get; init; }
    internal bool FrameWritten { get; set; }
    internal bool TruncatedWritten { get; set; }
}

// Frame manipulation is solely a test transport: v3 is already staged before this is called.
internal sealed partial class PairingTlsConsentSession
{
    internal ReadyWireControls? ReadyWireControls { get; set; }
    private async Task WriteReadyFrameTestAsync<T>(T frame, CancellationToken ct)
    {
        var controls = ReadyWireControls;
        if (controls is null) { await WritePrepareFrameAsync(frame, ct); return; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonLineProtocol.Json);
        try
        {
            if (controls.Capture) controls.Captured = bytes.ToArray();
            if (controls.Replay is not null) { CryptographicOperations.ZeroMemory(bytes); bytes = controls.Replay.ToArray(); }
            if (controls.Fault != ReadyWireFault.None)
            {
                var node = JsonNode.Parse(bytes) ?? throw new InvalidDataException("Invalid test READY.");
                switch (controls.Fault)
                {
                    case ReadyWireFault.Role: node["role"] = node["role"]!.GetValue<string>() == "host" ? "peer" : "host"; break;
                    case ReadyWireFault.Type: node["type"] = "prepare"; break;
                    case ReadyWireFault.Version: node["version"] = 2; break;
                    case ReadyWireFault.Session: node["sessionId"] = Guid.NewGuid(); break;
                    case ReadyWireFault.Transaction: node["transactionId"] = Guid.NewGuid(); break;
                    case ReadyWireFault.OfferNonce: node["offerNonce"] = Guid.NewGuid(); break;
                    case ReadyWireFault.Stage: node["stageId"] = Guid.NewGuid(); break;
                    case ReadyWireFault.SenderChallenge: node["senderChallenge"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); break;
                    case ReadyWireFault.ReceiverChallenge: node["receiverChallenge"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); break;
                    case ReadyWireFault.Slot: node["slot"] = 999; break;
                    case ReadyWireFault.DataPortBase: node["dataPortBase"] = 60000; break;
                    case ReadyWireFault.Digest: node["digest"] = new string('A', 64); break;
                    case ReadyWireFault.Missing: node.AsObject().Remove("stageId"); break;
                    case ReadyWireFault.Extra: node["unexpected"] = true; break;
                    case ReadyWireFault.CaseCollision: node["Type"] = "ready-v3"; break;
                    case ReadyWireFault.BadType: node["slot"] = "not-an-integer"; break;
                }
                var altered = JsonSerializer.SerializeToUtf8Bytes(node, JsonLineProtocol.Json);
                if (controls.Fault == ReadyWireFault.Duplicate)
                {
                    var duplicated = Encoding.UTF8.GetString(altered).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal);
                    CryptographicOperations.ZeroMemory(altered); altered = Encoding.UTF8.GetBytes(duplicated);
                }
                if (controls.Fault == ReadyWireFault.InvalidUtf8) { CryptographicOperations.ZeroMemory(altered); altered = [0xff]; }
                if (controls.Fault == ReadyWireFault.Oversize) { CryptographicOperations.ZeroMemory(altered); altered = Enumerable.Repeat((byte)'x', MaximumPrepareFrameBytes).ToArray(); }
                CryptographicOperations.ZeroMemory(bytes); bytes = altered;
            }
            await writeGate.WaitAsync(ct);
            try
            {
                if (controls.Fault == ReadyWireFault.Truncated)
                {
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), ct); await stream.FlushAsync(ct);
                    controls.TruncatedWritten = true;
                    throw new IOException("Synthetic truncated READY.");
                }
                await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { (byte)'\n' }, ct); await stream.FlushAsync(ct);
                controls.FrameWritten = true;
            }
            finally { writeGate.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
