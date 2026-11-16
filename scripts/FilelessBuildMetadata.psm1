# Read assembly metadata without loading a cross-compiled ARM64 assembly into the host runtime.
if (-not ('NianBuildMetadata' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

public static class NianBuildMetadata {
    public static bool HasExactlyOne(string path, string key, string value) {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        int matches = 0;
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes()) {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new BadImageFormatException("Invalid metadata attribute.");
            if (blob.ReadSerializedString() == key) {
                var found = blob.ReadSerializedString();
                if (found != value) return false;
                matches++;
            }
        }
        return matches == 1;
    }
}
'@
}
function Assert-NianBuildMetadata([string]$Assembly, [string]$Key, [string]$Value) {
    if (-not [NianBuildMetadata]::HasExactlyOne([IO.Path]::GetFullPath($Assembly), $Key, $Value)) {
        throw "Missing or mismatched build metadata: $Key"
    }
}
Export-ModuleMember -Function Assert-NianBuildMetadata
