#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NativeDirectory,
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$PackagesDirectory,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [Parameter(Mandatory)][switch]$TestingOnly,
    [switch]$MultiDeviceTesting,
    [switch]$DiscoveryTesting,
    [switch]$FilelessTesting,
    [switch]$ManagedRetention,
    [string]$DiscoveryBridge,
    [string]$DiscoveryBridgeSha256
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$useManagedRetention = if ($PSBoundParameters.ContainsKey('ManagedRetention')) { [bool]$ManagedRetention } else { [bool]($FilelessTesting -and $DiscoveryTesting) }
if (-not $TestingOnly) { throw 'This script creates internal test builds only.' }
if ($FilelessTesting -and -not $DiscoveryTesting) { throw 'Fileless testing requires the discovery candidate.' }
if ($useManagedRetention -and (-not $FilelessTesting -or -not $DiscoveryTesting)) { throw 'ManagedRetention requires the isolated fileless discovery candidate.' }
if ($DiscoveryTesting -and $MultiDeviceTesting) { throw 'Candidate modes are mutually exclusive.' }
if ($DiscoveryTesting -and (!$DiscoveryBridge -or $DiscoveryBridgeSha256 -cnotmatch '^[0-9a-f]{64}$')) { throw 'Discovery test requires the signed bridge and its exact lowercase SHA-256.' }
if (-not $DiscoveryTesting -and ($DiscoveryBridge -or $DiscoveryBridgeSha256)) { throw 'Discovery bridge inputs are restricted to the discovery candidate.' }
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($useManagedRetention) { Import-Module (Join-Path $PSScriptRoot 'MacFilelessManagedRetention.psm1') -ErrorAction Stop }
$native = [IO.Path]::GetFullPath($NativeDirectory)
$nativeRoot = [IO.Path]::GetFullPath((Join-Path $root '.artifacts/mac-native'))
function Assert-Arm64MachO([string]$file, [int]$expectedKind) {
    [byte[]]$header = Get-Content -LiteralPath $file -AsByteStream -TotalCount 32
    if ($header.Length -lt 32 -or [BitConverter]::ToUInt32($header, 0) -ne 4277009103 -or
        [BitConverter]::ToUInt32($header, 4) -ne 0x0100000c -or [BitConverter]::ToUInt32($header, 12) -ne $expectedKind) {
        throw 'Native input must have the expected arm64 Mach-O executable or dylib header.'
    }
}
if ($DiscoveryTesting) {
    $bridgeRoot = [IO.Path]::GetFullPath((Join-Path $root '.artifacts/mac-discovery-native'))
    $bridgeFile = [IO.Path]::GetFullPath($DiscoveryBridge)
    if (-not $bridgeFile.StartsWith($bridgeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($bridgeFile) -cne 'libNianWifiEvidence.dylib') { throw 'Discovery bridge must be in the dedicated build directory.' }
    $cursor = $bridgeFile
    while ($cursor -and $cursor.Length -ge $bridgeRoot.Length) {
        if (-not (Test-Path -LiteralPath $cursor) -or ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Missing or linked discovery bridge path.' }
        if ($cursor -ceq $bridgeRoot) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    if (-not (Test-Path -LiteralPath $bridgeFile -PathType Leaf) -or (Get-FileHash -LiteralPath $bridgeFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $DiscoveryBridgeSha256) { throw 'Signed discovery bridge hash mismatch.' }
    Assert-Arm64MachO $bridgeFile 6
}
if (-not [string]::Equals([IO.Path]::GetDirectoryName($native), $nativeRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($native) -cnotmatch '^native-[0-9a-f]{32}$') { throw 'Native input must be a direct native-<32 lowercase hex> child of the dedicated build directory.' }
foreach ($path in @($native, $nativeRoot, (Join-Path $root '.artifacts'), $root)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Missing or linked directory: $path" }
}
$Dotnet = [IO.Path]::GetFullPath($Dotnet)
$PackagesDirectory = [IO.Path]::GetFullPath($PackagesDirectory)
if (-not (Test-Path -LiteralPath $Dotnet -PathType Leaf) -or -not (Test-Path -LiteralPath $PackagesDirectory -PathType Container)) { throw 'The specified SDK or package cache does not exist.' }
Import-Module (Join-Path $PSScriptRoot 'SdkBaseline.psm1')
Assert-NianSdk -Dotnet $Dotnet -ProjectRoot $root
$evidencePath = Join-Path $native 'build-evidence.json'
if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf) -or ((Get-Item -LiteralPath $evidencePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Missing or linked native build evidence.' }
$initialEvidenceHash = if ($useManagedRetention) { (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
if ($useManagedRetention -and (Get-Item -LiteralPath $evidencePath -Force).Length -gt 8MB) { throw 'Native evidence is oversized.' }
$evidenceText = Get-Content -LiteralPath $evidencePath -Raw
if ($useManagedRetention) { Assert-NianManagedMacUniqueJson -Text $evidenceText }
$evidence = $evidenceText | ConvertFrom-Json
if ($evidence.schema -cne 'mac-native-build-v1' -or $evidence.label -cne [IO.Path]::GetFileName($native)) { throw 'Unexpected native evidence identity.' }
function Assert-MatchedFiles($entries, [string[]]$expected, [string]$directory) {
    if (@($entries).Count -ne $expected.Count) { throw 'Native manifest has unexpected entries.' }
    $seen = @{}
    foreach ($entry in $entries) {
        $name = [string]$entry.name
        if ($expected -cnotcontains $name -or $seen.ContainsKey($name) -or [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'Unsafe or duplicate native manifest entry.' }
        $seen[$name] = $true
        $file = Join-Path $directory $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Native source or binary missing/linked: $name" }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $entry.sha256) { throw "Native source or binary hash mismatch: $name" }
    }
}
Assert-MatchedFiles $evidence.sourceFiles @('DeskLinkMedia.swift','RealtimeCapture.swift','RealtimeCaptureOutput.swift','build-media.sh','media-sources.json','DeskLinkWeb.swift','build-web.sh') (Join-Path $root 'src/DesktopToolbox.Mac/Native')
Assert-MatchedFiles $evidence.binaries @('DeskLinkMedia','libDeskLinkWeb.dylib') $native
foreach ($entry in $evidence.binaries) {
    if ($entry.architecture -cne 'arm64') { throw 'Unexpected native architecture.' }
    $expectedKind = if ($entry.name -ceq 'DeskLinkMedia') { 2 } else { 6 }
    Assert-Arm64MachO (Join-Path $native $entry.name) $expectedKind
}
$lockModule = Join-Path $PSScriptRoot 'BuildArtifactLock.psm1'
Import-Module $lockModule
$managedLock = $null
$lock = $null
if ($useManagedRetention) {
    $managedLock = Enter-NianManagedMacFilelessLock -ProjectRoot $root
} else { $lock = Enter-ToolboxBuildLock -ProjectRoot $root }
$cacheLease = $null
try {
    if ($FilelessTesting) {
        Import-Module (Join-Path $PSScriptRoot 'FilelessArtifactCache.psm1') -ErrorAction Stop
        $macFilelessBuildProperties = @(Get-NianDiscoveryBuildProperties -FilelessTesting $true -PairingDiagnostics $false) + @('-p:NianMacReturnResearch=false')
        $cacheLease = Enter-NianFilelessCacheLease -ProjectRoot $root
        $isolatedArtifacts = Get-NianFilelessArtifactCache -ProjectRoot $root -Platform 'mac' -Lease $cacheLease
    } else {
        $isolatedArtifacts = $null
    }
    if ($useManagedRetention) {
        # Recheck validated native inputs under the build/cache leases, before reserving output.
        Assert-MatchedFiles $evidence.sourceFiles @('DeskLinkMedia.swift','RealtimeCapture.swift','RealtimeCaptureOutput.swift','build-media.sh','media-sources.json','DeskLinkWeb.swift','build-web.sh') (Join-Path $root 'src/DesktopToolbox.Mac/Native')
        Assert-MatchedFiles $evidence.binaries @('DeskLinkMedia','libDeskLinkWeb.dylib') $native
        $evidenceHash = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($evidenceHash -cne $initialEvidenceHash) { throw 'Native evidence changed before reservation.' }
        if ((Get-FileHash -LiteralPath $bridgeFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $DiscoveryBridgeSha256) { throw 'Discovery bridge changed before reservation.' }
        [void](Get-Command python -ErrorAction Stop)
        [void](Assert-NianManagedMacVerifierInputs -ProjectRoot $root)
        $runId = [guid]::NewGuid().ToString('N')
        $output = Start-NianManagedMacFileless -ProjectRoot $root -RunId $runId -Lock $managedLock
    } else {
        $output = Join-Path $root ('.artifacts/mac-package/test-' + [guid]::NewGuid().ToString('N'))
    }
    $parent = Split-Path -Parent $output
    if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked output root refused.' }
    [void][IO.Directory]::CreateDirectory($output)
    $publish = Join-Path $output 'publish'
    $project = if ($DiscoveryTesting) { Join-Path $root 'src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj' } else { Join-Path $root 'src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj' }
    Push-Location -LiteralPath $root
    try {
        $engineProperties = if ($DiscoveryTesting) { @('-p:PreviewControlEngine=false', '-p:MultiDeviceCandidate=false') } elseif ($MultiDeviceTesting) { @('-p:PreviewControlEngine=false', '-p:MultiDeviceCandidate=true') } else { @('-p:PreviewControlEngine=true', '-p:MultiDeviceCandidate=false') }
        if ($DiscoveryTesting) { $engineProperties += "-p:NianWifiEvidencePinnedSha256=$DiscoveryBridgeSha256" }
        if ($FilelessTesting) { $engineProperties += @('-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$isolatedArtifacts") + $macFilelessBuildProperties }
        $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'osx-arm64', '--self-contained', 'true', '--output', $publish, '--nologo', '--packages', $PackagesDirectory) + $engineProperties + @('-p:PreviewControlCandidate=false', '-p:NativeInputEngine=false', '-p:NativeInputCandidate=false', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', "-p:Version=$Version")
        if ($FilelessTesting) { Invoke-NianFilelessPublish -Dotnet $Dotnet -PublishArgs $publishArgs -Lease $cacheLease }
        else { & $Dotnet @publishArgs }
        if ($LASTEXITCODE -ne 0) { throw 'Mac cross-publish failed; partial output retained.' }
        if ($FilelessTesting) {
            Import-Module (Join-Path $PSScriptRoot 'FilelessBuildMetadata.psm1')
            foreach ($check in @(@('NianDeskLink.dll','NianFilelessPairingCandidate'), @('DesktopToolbox.DeskLink.dll','NianFilelessPairingIsolatedTest'))) {
                Assert-NianBuildMetadata -Assembly (Join-Path $publish $check[0]) -Key $check[1] -Value 'true'
            }
        }
        foreach ($entry in $evidence.binaries) {
            $destination = Join-Path $publish $entry.name
            if (Test-Path -LiteralPath $destination) { throw "Native publish destination unexpectedly exists: $destination" }
            Copy-Item -LiteralPath (Join-Path $native $entry.name) -Destination $destination
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $entry.sha256) { throw 'Copied native helper hash mismatch.' }
        }
        if ($DiscoveryTesting) {
            $destination = Join-Path $publish 'libNianWifiEvidence.dylib'
            if (Test-Path -LiteralPath $destination) { throw 'Discovery bridge publish destination unexpectedly exists.' }
            Copy-Item -LiteralPath $bridgeFile -Destination $destination
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $DiscoveryBridgeSha256) { throw 'Copied discovery bridge hash mismatch.' }
            $assemblyPath = [IO.Path]::GetFullPath((Join-Path $publish 'NianLink.MacCandidateDiscovery.dll'))
            $assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
            $pins = @($assembly.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' -and $_.ConstructorArguments[0].Value -ceq 'NianWifiEvidencePinnedSha256' })
            if ($pins.Count -ne 1 -or $pins[0].ConstructorArguments[1].Value -cne $DiscoveryBridgeSha256) { throw 'Published discovery assembly pin does not match the signed bridge.' }
        }
        $python = (Get-Command python -ErrorAction Stop).Source
        $packageMode = if ($FilelessTesting) { '--fileless-test' } elseif ($DiscoveryTesting) { '--discovery-test' } elseif ($MultiDeviceTesting) { '--multi-device-test' } else { '--preview-control' }
        $bridgeArguments = if ($DiscoveryTesting) { @('--discovery-bridge-sha256', $DiscoveryBridgeSha256) } else { @() }
        $assetsArguments = if ($FilelessTesting) { @('--assets-file', (Join-Path $isolatedArtifacts 'obj/NianLink.MacDiscoveryCandidate/project.assets.json')) } else { @() }
        [string[]]$managedArguments = if ($useManagedRetention) { '--managed-fileless' } else { @() }
        & $python -B (Join-Path $root 'src/DesktopToolbox.Mac/build/package_app.py') --publish $publish --output $output --version $Version $packageMode --packages-dir $PackagesDirectory @bridgeArguments @assetsArguments @managedArguments
        if ($LASTEXITCODE -ne 0) { throw 'Mac test packaging failed; partial output retained.' }
        Copy-Item -LiteralPath $evidencePath -Destination (Join-Path $output 'native-build-evidence.json')
        $buildStatus = [ordered]@{ testingOnly=$true; multiDevice=[bool]$MultiDeviceTesting; sourceRebuildVerified=$false; macRealDeviceVerified=$false; deskflowIncluded=$false; signed=$false; notarized=$false; nativeEvidenceSha256=(Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash }
        if ($DiscoveryTesting) { $buildStatus['discovery'] = $true }
        if ($FilelessTesting) { $buildStatus['filelessIsolatedTest'] = $true }
        $buildStatus | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'test-build-status.json') -Encoding utf8
        if ($useManagedRetention) {
            if ((Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $evidenceHash -or
                (Get-FileHash -LiteralPath $bridgeFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $DiscoveryBridgeSha256) { throw 'Native evidence or bridge changed during packaging; candidate remains pending.' }
            Complete-NianManagedMacFileless -ProjectRoot $root -RunId $runId -Version $Version -BridgeSha256 $DiscoveryBridgeSha256 -EvidenceSha256 $evidenceHash -Lock $managedLock
        }
        Write-Output "Internal Mac test package: $output"
    } finally { Pop-Location }
} finally {
    try { if ($null -ne $cacheLease) { Exit-NianFilelessCacheLease -Lease $cacheLease } }
    finally {
        if ($null -ne $managedLock) { Exit-NianManagedMacFilelessLock -Lock $managedLock }
        elseif ($null -ne $lock) { Exit-ToolboxBuildLock -Lock $lock }
    }
}
