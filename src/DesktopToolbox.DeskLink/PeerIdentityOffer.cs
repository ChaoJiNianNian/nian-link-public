using System.Text;
using System.Text.Json;

namespace DesktopToolbox.DeskLink;

/// <summary>Short-lived public identity card; it never contains a private key or a pairing token.</summary>
public sealed record PeerIdentityOffer
{
    private const int MaximumJsonBytes = 4096;
    public int Version { get; init; } = 1;
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public required string KeyboardFingerprint { get; init; }
    public required string Nonce { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    public static async Task<PeerIdentityOffer> CreateAsync(string profile, string name, string platform,
        DateTimeOffset now, CancellationToken ct = default)
    {
        DeskflowProfile.ValidateName(name);
        if (platform is not ("windows" or "mac")) throw new ArgumentException("Unsupported device platform.", nameof(platform));
        if (!Path.IsPathFullyQualified(profile)) throw new ArgumentException("A private absolute profile path is required.", nameof(profile));
        profile = Path.GetFullPath(profile);
        RejectLinks(profile);
        PrivateFiles.EnsureDirectory(profile);
        var lockPath = Path.Combine(profile, ".session.lock");
        RejectLinks(lockPath);
        using var profileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var certificatePath = Path.Combine(profile, "tls", "deskflow.pem");
        RejectLinks(certificatePath);
        await PrivateFiles.EnsureCertificateAsync(certificatePath, ct);
        using var certificate = PrivateFiles.LoadCertificate(certificatePath);
        return new PeerIdentityOffer
        {
            Name = name, Platform = platform, KeyboardFingerprint = DeskLinkFingerprint.OfCertificate(certificate),
            Nonce = Guid.NewGuid().ToString("N"), ExpiresAtUtc = now.ToUniversalTime().AddMinutes(10)
        };
    }

    public void Validate(DateTimeOffset now)
    {
        DeskflowProfile.ValidateName(Name);
        if (Version != 1 || Platform is not ("windows" or "mac") ||
            !Guid.TryParseExact(Nonce, "N", out var nonce) || nonce == Guid.Empty ||
            ExpiresAtUtc <= now || ExpiresAtUtc > now.AddMinutes(12))
            throw new ArgumentException("Identity card is invalid or expired.");
        DeskLinkFingerprint.Normalize(KeyboardFingerprint);
    }

    public string ToJson(DateTimeOffset now)
    {
        Validate(now);
        var json = JsonSerializer.Serialize(this, JsonLineProtocol.Json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes) throw new ArgumentException("Identity card is oversized.");
        return json;
    }
    public static PeerIdentityOffer Parse(string json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new ArgumentException("Invalid identity card.");
        var offer = JsonSerializer.Deserialize<PeerIdentityOffer>(json, JsonLineProtocol.Json)
            ?? throw new ArgumentException("Empty identity card.");
        offer.Validate(now);
        return offer;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Device identity cannot use a linked path."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
