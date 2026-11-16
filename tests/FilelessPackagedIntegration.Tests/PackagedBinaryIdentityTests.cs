using System.Reflection;
using System.Security.Cryptography;
using DesktopToolbox.DeskLink;
using NianLink.CandidateDiscovery;

namespace FilelessCandidateIntegration.Tests;

public sealed class PackagedBinaryIdentityTests
{
    [Fact]
    public void ExecutedAssembliesMatchTheProvidedPackagedBinariesByteForByte()
    {
        var directory = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "NianFilelessBinaryDirectory").Value!;
        var hostDirectory = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "NianFilelessHostBinaryDirectory").Value!;
        foreach (var assembly in new[] { typeof(MultiDeviceCredentialVault).Assembly, typeof(SecurePairingHost).Assembly,
            Assembly.Load("Nian.Input.Core"), Assembly.Load("Nian.Input.Control"), Assembly.Load("Nian.Input.Security") })
        {
            var packaged = Path.Combine(assembly == typeof(SecurePairingHost).Assembly ? hostDirectory : directory,
                assembly.GetName().Name + ".dll");
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(packaged)), SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        }
    }
}
