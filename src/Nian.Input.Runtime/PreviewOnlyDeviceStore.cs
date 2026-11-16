using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nian.Input.Security;

namespace Nian.Input.Runtime;

internal sealed record SavedPreviewPeer(int Version, bool UserApproved, DevicePermission Permissions, TrustedEndpoint Endpoint);

/// <summary>Private, atomic approval storage. A trust entry alone never substitutes for this record.</summary>
internal sealed class PreviewOnlyDeviceStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly string profile;
    private FileStream? lease;
    internal PreviewOnlyDeviceStore(string profile) { Check(profile); this.profile = profile; }
    internal void Open()
    {
        Check(profile); Directory.CreateDirectory(profile); Check(profile); Protect(profile, true);
        var path = Path.Combine(profile, ".preview-control.lock"); Check(path);
        lease = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); Protect(path);
    }
    internal SavedPreviewPeer? Read()
    {
        var path = Path.Combine(profile, "peer.json"); Check(path);
        if (!File.Exists(path)) return null;
        Protect(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 8192) throw new InvalidDataException("The preview approval record is invalid.");
        var value = new byte[checked((int)stream.Length)]; stream.ReadExactly(value);
        var peer = JsonSerializer.Deserialize<SavedPreviewPeer?>(value, Json);
        if (peer is not null && !Valid(peer)) throw new InvalidDataException("The preview approval record is invalid.");
        return peer;
    }
    internal async Task WriteAsync(SavedPreviewPeer? peer, CancellationToken token, Func<IDisposable>? commitGuard = null, Action? committed = null)
    {
        if (peer is not null && !Valid(peer)) throw new InvalidDataException("The preview approval record is invalid.");
        var path = Path.Combine(profile, "peer.json"); Check(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                Protect(temporary);
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(peer, Json), token);
                await stream.FlushAsync(token); stream.Flush(true);
            }
            using (commitGuard?.Invoke())
            {
                token.ThrowIfCancellationRequested(); Check(path); File.Move(temporary, path, true); committed?.Invoke();
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static bool Valid(SavedPreviewPeer peer) => peer.Version == 1 && peer.UserApproved &&
        peer.Permissions == DevicePermission.PreviewControl && ValidEndpoint(peer.Endpoint);
    internal static bool ValidEndpoint(TrustedEndpoint? endpoint) => endpoint is not null &&
        endpoint.Port is > 0 and <= 65535 && endpoint.Host is { Length: > 0 and <= 253 } &&
        !endpoint.Host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && Uri.CheckHostName(endpoint.Host) != UriHostNameType.Unknown &&
        endpoint.Fingerprint is { Length: 64 } && endpoint.Fingerprint.All(Uri.IsHexDigit) && ValidName(endpoint.DeviceName);
    private static bool ValidName(string? name) => name is { Length: > 0 and <= 64 } && name.Split('.').All(label =>
        label.Length is > 0 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1]) &&
        label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    private static void Check(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) == Path.GetFullPath(path)) throw new ArgumentException("Choose an absolute preview profile directory.");
        for (string? cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Preview approvals cannot use linked paths.");
    }
    private static void Protect(string path, bool directory = false)
    {
        if (OperatingSystem.IsWindows()) ProtectWindows(path, directory);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : 0));
    }
    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); var user = identity.User ?? throw new IOException("The current user is unavailable.");
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
    public void Dispose() { lease?.Dispose(); lease = null; }
}
