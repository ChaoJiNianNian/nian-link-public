#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppZip,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$AppSha256,
    [Parameter(Mandatory)][string]$DeskflowDmg,
    [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
    [Parameter(Mandatory)][switch]$TestingOnly,
    [switch]$MultiDeviceTesting
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $TestingOnly) { throw 'This script creates an internal test package only.' }
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = Join-Path $root '.artifacts/mac-export'
foreach ($path in @($root, (Join-Path $root '.artifacts'), $AppZip, $DeskflowDmg)) {
    if (-not (Test-Path -LiteralPath $path) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Missing or linked input: $path" }
}
Import-Module (Join-Path $PSScriptRoot 'BuildArtifactLock.psm1')
$taskLock = Enter-ToolboxBuildLock -ProjectRoot $root
try {
    if ((Test-Path -LiteralPath $outputRoot) -and ((Get-Item -LiteralPath $outputRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked output root refused.' }
    [void][IO.Directory]::CreateDirectory($outputRoot)
    $output = Join-Path $outputRoot ('test-' + [guid]::NewGuid().ToString('N'))
    $python = (Get-Command python -ErrorAction Stop).Source
    $mode = @()
    if ($MultiDeviceTesting) { $mode += '--multi-device-testing' }
    & $python -B (Join-Path $root 'scripts/build-mac-export-test.py') --app-zip $AppZip --app-sha256 $AppSha256 --dmg $DeskflowDmg --output $output --version $Version @mode
    if ($LASTEXITCODE -ne 0) { throw 'Mac test export bundle failed; any partial output was retained.' }
    Write-Output "Internal Mac export bundle: $output"
} finally { Exit-ToolboxBuildLock -Lock $taskLock }
