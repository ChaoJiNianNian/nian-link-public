using System.Security.Cryptography;

namespace MacManagedWifiProbe;

internal static class ProbeDigest
{
    internal static bool IsValid(string? digest) => digest is { Length: 64 } &&
        digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    internal static async Task<bool> MatchesAsync(string path, string digest)
    {
        if (!IsValid(digest)) return false;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 65536, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is <= 0 or > 67108864) return false;
            var actual = await SHA256.HashDataAsync(file);
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(digest));
        }
        catch (Exception) { return false; }
    }
}
