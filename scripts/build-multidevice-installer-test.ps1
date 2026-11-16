#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$CandidateManifestSha256,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [switch]$BetaPortable,
    [Parameter(Mandatory)][switch]$TestingOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $TestingOnly) { throw 'Only an isolated internal test installer is available.' }
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$candidateRoot = [IO.Path]::GetFullPath((Join-Path $root $(if ($BetaPortable) { '.artifacts/discovery-portable-managed/fileless' } else { '.artifacts/multidevice-windows' })))
$candidate = [IO.Path]::GetFullPath($CandidateDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ([IO.Path]::GetDirectoryName($candidate) -ine $candidateRoot -or [IO.Path]::GetFileName($candidate) -cnotmatch '^test-[a-f0-9]{32}$') { throw 'Candidate must be an isolated project test directory.' }
foreach ($path in @($root, (Join-Path $root '.artifacts'), $candidateRoot, $candidate)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Missing or linked input: $path" }
}
$Compiler = [IO.Path]::GetFullPath($Compiler)
$signature = Get-AuthenticodeSignature -LiteralPath $Compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Unverified Inno Setup compiler.' }
Import-Module (Join-Path $PSScriptRoot 'BuildArtifactLock.psm1')
$lock = Enter-ToolboxBuildLock -ProjectRoot $root
try {
$manifestPath = Join-Path $candidate 'test-build-status.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or ((Get-Item -LiteralPath $manifestPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ine $CandidateManifestSha256) { throw 'Candidate manifest is missing, linked or changed.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.testingOnly -ne $true -or $manifest.sourceRebuildVerified -ne $false -or $manifest.signed -ne $false) { throw 'Unexpected candidate status.' }
$bundle = Join-Path $candidate $(if ($BetaPortable) { '年年互联-免文件便携测试' } else { '年年互联-多设备测试' })
if ($BetaPortable) {
    $betaProperty = $manifest.PSObject.Properties['betaPortable']
    $filelessProperty = $manifest.PSObject.Properties['filelessIsolatedTest']
    if ($null -eq $betaProperty -or $betaProperty.Value -ne $true -or $null -eq $filelessProperty -or $filelessProperty.Value -ne $true -or
        $manifest.runId -cne [IO.Path]::GetFileName($candidate).Substring(5)) { throw 'Expected an identified beta fileless test candidate.' }
    $python = (Get-Command python -ErrorAction Stop).Source
    & $python -B (Join-Path $root 'scripts/discovery_portable_policy.py') --bundle $bundle --source $root --manifest $manifestPath
    if ($LASTEXITCODE -ne 0) { throw 'Beta candidate verification failed.' }
}
if (-not (Test-Path -LiteralPath $bundle -PathType Container) -or ((Get-Item -LiteralPath $bundle -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Missing or linked candidate bundle.' }
$seen = @{}
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or $relative.Contains(':') -or $relative -match '[\x00-\x1f]' -or @($relative.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -or $relative -match '(?i)\.(pem|key|pfx|p12)$' -or $seen.ContainsKey($relative)) { throw 'Unsafe or duplicate manifest item.' }
    $seen[$relative] = $true
    $full = Join-Path $bundle $relative
    for ($cursor = $full; $cursor -and $cursor.StartsWith($bundle, [StringComparison]::OrdinalIgnoreCase); $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (-not (Test-Path -LiteralPath $cursor) -or ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Missing or linked candidate item.' }
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf) -or (Get-Item -LiteralPath $full).Length -ne [long]$entry.bytes -or (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ine [string]$entry.sha256) { throw "Candidate item changed: $relative" }
}
$actual = @(Get-ChildItem -LiteralPath $bundle -Recurse -Force)
if (@($actual | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Linked candidate content refused.' }
foreach ($file in @($actual | Where-Object { -not $_.PSIsContainer })) {
    $relative = [IO.Path]::GetRelativePath($bundle, $file.FullName).Replace('\','/')
    if (-not $seen.ContainsKey($relative)) { throw "Unexpected candidate file: $relative" }
}
if (-not $seen.ContainsKey($(if ($BetaPortable) { 'App/NianLink.DiscoveryPortable.exe' } else { 'App/NianLink.MultiDevice.exe' })) -or -not $seen.ContainsKey('Keyboard/deskflow-core.exe')) { throw 'Candidate is incomplete.' }
if ((Get-FileHash -LiteralPath (Join-Path $bundle 'Keyboard/deskflow-core.exe') -Algorithm SHA256).Hash -ine [string]$manifest.keyboardCoreSha256) { throw 'Keyboard core does not match the manifest.' }
$macProperty = $manifest.PSObject.Properties['macClientIncluded']
$macIncluded = $null -ne $macProperty -and $macProperty.Value -eq $true
if ($macIncluded) {
    if (-not $seen.ContainsKey('App/MacClient/NianDeskLink-macOS-arm64.zip') -or -not $seen.ContainsKey('App/MacClient/manifest.json')) { throw 'Mac test bundle is incomplete.' }
    $python = (Get-Command python -ErrorAction Stop).Source
    & $python -B (Join-Path $root 'scripts/verify-mac-export-test.py') --directory (Join-Path $bundle 'App/MacClient') --version $Version --multi-device-testing
    if ($LASTEXITCODE -ne 0) { throw 'Mac multi-device bundle verification failed.' }
} elseif (@($seen.Keys | Where-Object { $_ -like 'App/MacClient/*' }).Count) { throw 'Unexpected Mac client files in candidate.' }
    $outputRoot = Join-Path $root $(if ($BetaPortable) { '.artifacts/multidevice-beta-installers' } else { '.artifacts/multidevice-installers' })
    if ((Test-Path -LiteralPath $outputRoot) -and ((Get-Item -LiteralPath $outputRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked output refused.' }
    if ($BetaPortable -and (Test-Path -LiteralPath $outputRoot) -and @(Get-ChildItem -LiteralPath $outputRoot -Force).Count -ge 2) { throw 'Beta installer retention is full; retain and review the existing outputs.' }
    $output = Join-Path $outputRoot ('test-' + $(if ($BetaPortable) { $manifest.runId } else { [guid]::NewGuid().ToString('N') }))
    if (Test-Path -LiteralPath $output) { throw 'Installer output already exists; no overwrite.' }
    [void][IO.Directory]::CreateDirectory($output)
    $installer = Join-Path $root $(if ($BetaPortable) { 'installer/NianLinkMultiDeviceBetaTest.iss' } else { 'installer/NianLinkMultiDeviceTest.iss' })
    $compilerArgs = @('/Q', "/DAppVersion=$Version", "/DBundleDir=$bundle", "/DReleaseDir=$output")
    if ($BetaPortable) { $compilerArgs += "/DBetaId=$($manifest.runId)" }
    & $Compiler @compilerArgs $installer
    if ($LASTEXITCODE -ne 0) { throw 'Internal installer compilation failed; partial output retained.' }
    $setup = Join-Path $output $(if ($BetaPortable) { "NianLink-Multi-Beta-$Version-win-x64-TEST-Setup.exe" } else { "NianLink-Multi-$Version-win-x64-TEST-Setup.exe" })
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer output missing.' }
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ine $CandidateManifestSha256) { throw 'Candidate manifest changed while compiling the installer.' }
    [ordered]@{ testingOnly=$true; signed=$false; sourceRebuildVerified=$false; candidateManifestSha256=$CandidateManifestSha256.ToUpperInvariant(); sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash; bytes=(Get-Item -LiteralPath $setup).Length } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'test-installer-status.json') -Encoding utf8
    Write-Output "Internal multi-device test installer: $setup"
} finally { Exit-ToolboxBuildLock -Lock $lock }
