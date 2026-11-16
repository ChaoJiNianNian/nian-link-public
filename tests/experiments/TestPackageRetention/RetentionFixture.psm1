#requires -Version 7.0
Set-StrictMode -Version Latest

# Synthetic experiment only: the allowed root is an empty directory directly
# under this process's temp path. That path is environment-controlled, so this
# convention is not a security sandbox and must not be used with real data.
function Assert-FixtureRoot([string]$Root, [bool]$Initialized) {
    $full = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($full) -ine $temp -or
        [IO.Path]::GetFileName($full) -cnotmatch '^nian-retention-fixture-[a-z0-9_-]+$' -or
        -not (Test-Path -LiteralPath $full -PathType Container)) { throw 'Only a dedicated system-temp fixture root is allowed.' }
    Assert-FixturePath $full
    $marker = Join-Path $full '.synthetic-retention-fixture'
    if ($Initialized) {
        Assert-FixturePath $marker
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
            [IO.File]::ReadAllText($marker) -cne 'nian-synthetic-retention-fixture-v1') { throw 'Fixture marker missing or changed.' }
    }
    return $full
}

function Assert-FixturePath([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked fixture path refused.' }
        }
    }
}

function Enter-FixtureLock([string]$Root, [string]$LockModule) {
    $expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../scripts/BuildArtifactLock.psm1'))
    if ([IO.Path]::GetFullPath($LockModule) -ine $expected) { throw 'Unexpected build lock module.' }
    Import-Module $expected -ErrorAction Stop
    return Enter-ToolboxBuildLock -ProjectRoot $Root
}

function Get-FixtureIndex([string]$Root, [string]$Kind) {
    $path = Join-Path $Root ('retention-' + $Kind + '.json')
    $next = $path + '.next'
    Assert-FixturePath $path
    Assert-FixturePath $next
    if (Test-Path -LiteralPath $next) { throw 'Incomplete index update requires inspection.' }
    if (-not (Test-Path -LiteralPath $path)) {
        return @{ schema = 'synthetic-retention-v1'; kind = $Kind; current = $null; previous = $null; pending = $null; currentHash = $null; previousHash = $null }
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Index must be a file.' }
    $state = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
    if ($null -eq $state -or @($state.Keys).Count -ne 7 -or
        $state.schema -cne 'synthetic-retention-v1' -or $state.kind -cne $Kind -or
        -not $state.Contains('current') -or -not $state.Contains('previous') -or -not $state.Contains('pending') -or
        -not $state.Contains('currentHash') -or -not $state.Contains('previousHash')) { throw 'Invalid fixture index.' }
    $ids = @()
    foreach ($slot in @('current','previous','pending')) {
        $id = $state[$slot]
        if ($null -ne $id) {
            if ($id -isnot [string] -or $id -cnotmatch '^test-[a-f0-9]{32}$' -or $ids -ccontains $id) { throw 'Invalid or duplicate fixture ID.' }
            $ids += $id
            $item = Join-Path $Root ('payloads/' + $Kind + '/' + $id)
            Assert-FixturePath $item
            if (-not (Test-Path -LiteralPath $item -PathType Container)) { throw 'Referenced fixture directory is missing.' }
            if ($slot -ne 'pending') {
                $hash = $state[$slot + 'Hash']
                if ($hash -isnot [string] -or $hash -cnotmatch '^[0-9a-f]{64}$') { throw 'Fixture hash missing or invalid.' }
                Assert-FixturePayload $item $hash
            }
        } elseif ($slot -ne 'pending' -and $null -ne $state[$slot + 'Hash']) {
            throw 'Fixture hash without corresponding generation.'
        }
    }
    if ($null -ne $state.previous -and $null -eq $state.current) { throw 'Previous fixture without current fixture.' }
    return $state
}

function Assert-FixturePayload([string]$Target, [string]$ExpectedSha256) {
    Assert-FixturePath $Target
    $items = @(Get-ChildItem -LiteralPath $Target -Force -ErrorAction Stop)
    if ($items.Count -ne 1 -or $items[0].Name -cne 'sample.bin' -or $items[0].PSIsContainer -or
        ($items[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -or $items[0].Length -gt 1048576) { throw 'Unexpected or linked synthetic payload.' }
    if ((Get-FileHash -LiteralPath $items[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedSha256) { throw 'Synthetic payload hash mismatch.' }
}

function Write-FixtureIndex([string]$Root, [string]$Kind, $State) {
    $path = Join-Path $Root ('retention-' + $Kind + '.json')
    $next = $path + '.next'
    Assert-FixturePath $path
    Assert-FixturePath $next
    $bytes = [Text.Encoding]::UTF8.GetBytes(($State | ConvertTo-Json -Compress))
    $stream = [IO.File]::Open($next, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    # Same-directory rename; a retained .next after interruption fails closed.
    [IO.File]::Move($next, $path, $true)
}

function Assert-FixtureChildren([string]$Root, [string]$Kind, $State) {
    $parent = Join-Path $Root ('payloads/' + $Kind)
    Assert-FixturePath $parent
    $expected = @(@($State.current, $State.previous, $State.pending) | Where-Object { $null -ne $_ })
    if (-not (Test-Path -LiteralPath $parent)) {
        if ($expected.Count -ne 0) { throw 'Referenced fixture parent is missing.' }
        return
    }
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Fixture parent is not a directory.' }
    $items = @(Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop)
    if ($items.Count -ne $expected.Count) { throw 'Unknown or missing fixture output.' }
    foreach ($item in $items) {
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $expected -cnotcontains $item.Name) { throw 'Unknown or linked fixture output.' }
    }
}

function Initialize-RetentionFixture {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$LockModule)
    $rootPath = Assert-FixtureRoot $Root $false
    $lock = Enter-FixtureLock $rootPath $LockModule
    try {
        if (@(Get-ChildItem -LiteralPath $rootPath -Force).Count -ne 0) { throw 'A new fixture root must be empty.' }
        $marker = Join-Path $rootPath '.synthetic-retention-fixture'
        $stream = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.Encoding]::UTF8.GetBytes('nian-synthetic-retention-fixture-v1'); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    } finally { Exit-ToolboxBuildLock -Lock $lock }
}

function Read-RetentionFixture {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$LockModule,
          [Parameter(Mandatory)][ValidateSet('windows-fileless','mac-fileless')][string]$Kind)
    $rootPath = Assert-FixtureRoot $Root $true
    $lock = Enter-FixtureLock $rootPath $LockModule
    try {
        $state = Get-FixtureIndex $rootPath $Kind
        Assert-FixtureChildren $rootPath $Kind $state
        $state | ConvertTo-Json -Compress
    } finally { Exit-ToolboxBuildLock -Lock $lock }
}

function Start-RetentionFixture {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$LockModule,
          [Parameter(Mandatory)][ValidateSet('windows-fileless','mac-fileless')][string]$Kind)
    $rootPath = Assert-FixtureRoot $Root $true
    $lock = Enter-FixtureLock $rootPath $LockModule
    try {
        $state = Get-FixtureIndex $rootPath $Kind
        Assert-FixtureChildren $rootPath $Kind $state
        if ($null -ne $state.pending -or $null -ne $state.previous) { throw 'A fixture is pending or both generation slots are full.' }
        $id = 'test-' + [guid]::NewGuid().ToString('N')
        $parent = Join-Path $rootPath ('payloads/' + $Kind)
        $target = Join-Path $parent $id
        Assert-FixturePath $target
        if (Test-Path -LiteralPath $target) { throw 'Fixture output already exists.' }
        [void][IO.Directory]::CreateDirectory($parent)
        # Record the reservation first: an interruption then fails closed.
        $state.pending = $id
        Write-FixtureIndex $rootPath $Kind $state
        [void][IO.Directory]::CreateDirectory($target)
        Write-Output $target
    } finally { Exit-ToolboxBuildLock -Lock $lock }
}

function Commit-RetentionFixture {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$LockModule,
          [Parameter(Mandatory)][ValidateSet('windows-fileless','mac-fileless')][string]$Kind,
          [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedSha256)
    $rootPath = Assert-FixtureRoot $Root $true
    $lock = Enter-FixtureLock $rootPath $LockModule
    try {
        $state = Get-FixtureIndex $rootPath $Kind
        Assert-FixtureChildren $rootPath $Kind $state
        if ($null -eq $state.pending -or $null -ne $state.previous) { throw 'No commit slot is available.' }
        $target = Join-Path $rootPath ('payloads/' + $Kind + '/' + $state.pending)
        Assert-FixturePayload $target $ExpectedSha256
        $state.previous = $state.current
        $state.previousHash = $state.currentHash
        $state.current = $state.pending
        $state.currentHash = $ExpectedSha256
        $state.pending = $null
        Write-FixtureIndex $rootPath $Kind $state
    } finally { Exit-ToolboxBuildLock -Lock $lock }
}

Export-ModuleMember -Function Initialize-RetentionFixture,Read-RetentionFixture,Start-RetentionFixture,Commit-RetentionFixture
