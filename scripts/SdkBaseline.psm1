function Resolve-NianLongPath([string]$Path) {
    if (-not ('NianLink.Build.WindowsNativePathV1' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
namespace NianLink.Build {
    public static class WindowsNativePathV1 {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        public static extern uint GetLongPathName(string path, StringBuilder output, uint size);
    }
}
'@
    }
    $buffer = [System.Text.StringBuilder]::new(32768)
    $length = [NianLink.Build.WindowsNativePathV1]::GetLongPathName($Path, $buffer, $buffer.Capacity)
    if ($length -eq 0 -or $length -ge $buffer.Capacity) {
        throw "Unable to resolve the existing build directory: $Path"
    }
    return $buffer.ToString()
}

function Assert-NianSdk {
    param([Parameter(Mandatory)][string]$Dotnet, [Parameter(Mandatory)][string]$ProjectRoot)
    # PowerShell may store a mixed short/long Windows path that Pop-Location cannot
    # restore. Move to the same physical caller directory with expanded names.
    $location = Get-Location
    if ($IsWindows -and $location.Provider.Name -eq 'FileSystem') {
        $provider = $null
        $drive = $null
        $nativeProject = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ProjectRoot, [ref]$provider, [ref]$drive)
        if ($provider.Name -ne 'FileSystem') { throw 'The SDK project directory must be on the file system.' }
        $longProject = Resolve-NianLongPath $nativeProject
        # Native volume drives can contain mixed long/8.3 names after Push-Location.
        # Preserve custom PowerShell drives: their logical location must survive.
        if ($null -ne $location.Drive -and $location.Drive.Name.Length -eq 1 -and
            $location.Drive.Root -ieq ($location.Drive.Name + ':\')) {
            $longCaller = Resolve-NianLongPath $location.ProviderPath
            Set-Location -LiteralPath $longCaller
        }
        $ProjectRoot = $longProject
    }
    Push-Location -LiteralPath $ProjectRoot
    try {
        $sdk = (& $Dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $sdk -cne '10.0.404') { throw "Expected .NET SDK 10.0.404; actual: $sdk" }
    } finally { Pop-Location }
}
Export-ModuleMember -Function Assert-NianSdk
