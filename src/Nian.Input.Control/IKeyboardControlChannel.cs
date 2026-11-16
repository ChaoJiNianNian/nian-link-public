namespace Nian.Input.Control;

/// <summary>An already authenticated, keyboard-session-owned channel. Pairing and transport ownership belong to the host.</summary>
public interface IKeyboardControlChannel
{
    Task<InputReply> BeginAsync(CancellationToken cancellationToken = default);
    Task<InputReply> SendAsync(InputPacket packet, CancellationToken cancellationToken = default);
    Task<InputReply> HeartbeatAsync(CancellationToken cancellationToken = default);
    Task<InputReply> EndAsync(CancellationToken cancellationToken = default);
}
