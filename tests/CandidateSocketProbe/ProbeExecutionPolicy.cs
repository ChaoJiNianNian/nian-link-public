namespace NianLink.PrivateUdpProbe;

internal static class ProbeExecutionPolicy
{
    internal static bool Matches(string? processPath, string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(assemblyPath) ||
            !Path.IsPathFullyQualified(processPath) || !Path.IsPathFullyQualified(assemblyPath)) return false;
        try
        {
            var exe = Path.GetFullPath(processPath);
            var dll = Path.GetFullPath(assemblyPath);
            return Path.GetFileName(exe).Equals("CandidateSocketProbe.exe", StringComparison.OrdinalIgnoreCase) &&
                Path.ChangeExtension(exe, ".dll").Equals(dll, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
