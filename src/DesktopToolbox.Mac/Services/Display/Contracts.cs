namespace DesktopToolbox.Mac.Services.Display;
public readonly record struct MacDisplayIdentity(uint VendorId, uint ProductId);
public sealed record MacDisplayDescriptor(MacDisplayIdentity Identity, ulong RegistryId, uint ServiceId, string Name, string? SerialNumber, uint ChipAddress);
public sealed record MacDisplayReadResult(bool Ok, ushort? CurrentInput, ushort? MaximumInput, int? NativeError, string? Error) { public string? ReadVariant { get; init; } public IReadOnlyList<MacDisplayReadAttempt> Attempts { get; init; } = []; }
public sealed record MacDisplaySetResult(bool CommandAccepted, int? NativeError, string? Error)
{
    public string Status => CommandAccepted ? "acceptedUnverified" : "failed";
}
public interface IMacDisplayControl : IDisposable
{
    Task<IReadOnlyList<MacDisplayDescriptor>> ListAsync(CancellationToken cancellationToken = default);
    Task<MacDisplayReadResult> ReadInputAsync(MacDisplayIdentity identity, CancellationToken cancellationToken = default);
    Task<MacDisplaySetResult> SetInputAsync(MacDisplayIdentity identity, ushort input, CancellationToken cancellationToken = default);
}


public sealed record MacDisplayReadAttempt(uint Offset, bool StandardChecksum, int WriteError, int? ReadError, string? ReplyHex);

