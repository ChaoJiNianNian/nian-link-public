#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('missing-previous','missing-pending')][string]$Action,
    [Parameter(Mandatory)][switch]$TestingOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $TestingOnly) { throw 'Only internal Mac test package records may be maintained.' }
$normalizedAction = $Action.ToLowerInvariant()
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Import-Module (Join-Path $PSScriptRoot 'MacFilelessManagedRetention.psm1') -ErrorAction Stop
$lock = Enter-NianManagedMacFilelessLock -ProjectRoot $root
try {
    if ($normalizedAction -ceq 'missing-previous') { Clear-NianManagedMacMissingPrevious -ProjectRoot $root -Lock $lock }
    elseif ($normalizedAction -ceq 'missing-pending') { Clear-NianManagedMacMissingPending -ProjectRoot $root -Lock $lock }
    else { throw 'Unknown Mac test-package maintenance action.' }
    Write-Output 'Updated the Mac fileless test-package index; no package or profile was deleted.'
} finally { Exit-NianManagedMacFilelessLock -Lock $lock }
