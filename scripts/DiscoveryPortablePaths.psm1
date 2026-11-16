#requires -Version 7.0
Set-StrictMode -Version Latest

function Assert-DiscoveryBuildPaths {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)][string]$OutputRoot,
          [Parameter(Mandatory)][string]$Output, [Parameter(Mandatory)][string]$DataRoot,
          [Parameter(Mandatory)][string]$Profile,
          [ValidateSet('discovery','fileless','diagnostics')][string]$ManagedMode)
    $project = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $outputBase = [IO.Path]::GetFullPath($OutputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath($Output).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $local = [IO.Path]::GetFullPath($DataRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $profileTarget = [IO.Path]::GetFullPath($Profile).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $expectedOutput = if ($ManagedMode) {
        [IO.Path]::GetFullPath((Join-Path $project ('.artifacts/discovery-portable-managed/' + $ManagedMode)))
    } else {
        [IO.Path]::GetFullPath((Join-Path $project '.artifacts/discovery-portable'))
    }
    $expectedProfile = [IO.Path]::GetFullPath((Join-Path $local 'nian-link/test-profiles/discovery-portable'))
    $name = [IO.Path]::GetFileName($target)
    if ($outputBase -ine $expectedOutput -or [IO.Path]::GetDirectoryName($target) -ine $outputBase -or
        [IO.Path]::GetDirectoryName($profileTarget) -ine $expectedProfile -or
        $name -cnotmatch '^test-[a-f0-9]{32}$' -or [IO.Path]::GetFileName($profileTarget) -cne $name) {
        throw 'Discovery candidate paths are not dedicated matching targets.'
    }
    if (-not (Test-Path -LiteralPath $project -PathType Container) -or -not (Test-Path -LiteralPath $local -PathType Container)) { throw 'Project or local-data root missing.' }
    foreach ($path in @($project, $outputBase, $target, $local, $expectedProfile, $profileTarget)) {
        for ($cursor = $path; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
            if (Test-Path -LiteralPath $cursor) {
                if ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked path refused.' }
            }
        }
    }
    if ((Test-Path -LiteralPath $target) -or (Test-Path -LiteralPath $profileTarget)) { throw 'Existing discovery output or profile refused.' }
}
Export-ModuleMember -Function Assert-DiscoveryBuildPaths
