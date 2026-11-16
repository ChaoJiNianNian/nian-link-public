#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('discovery','fileless','diagnostics')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('missing-previous','missing-pending')][string]$Action,
    [Parameter(Mandatory)][switch]$TestingOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $TestingOnly) { throw 'Only internal test package records may be maintained.' }
$normalizedMode = $Mode.ToLowerInvariant()
$normalizedAction = $Action.ToLowerInvariant()
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Import-Module (Join-Path $PSScriptRoot 'DiscoveryManagedRetention.psm1') -ErrorAction Stop
$lock = Enter-NianManagedDiscoveryLock -ProjectRoot $root
try {
    if ($normalizedAction -ceq 'missing-previous') { Clear-NianManagedMissingPrevious -ProjectRoot $root -Mode $normalizedMode -Lock $lock }
    elseif ($normalizedAction -ceq 'missing-pending') { Clear-NianManagedMissingPending -ProjectRoot $root -Mode $normalizedMode -Lock $lock }
    else { throw 'Unknown test-package maintenance action.' }
    Write-Output "Updated the $normalizedMode test-package index; no package or profile was deleted."
} finally { Exit-NianManagedDiscoveryLock -Lock $lock }
