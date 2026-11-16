#requires -Version 7.0
Set-StrictMode -Version Latest

# This only records isolated candidate outputs. It never deletes packages,
# profiles, or installations. The calling builder must hold its project lock.
function Assert-ManagedLock($Lock, [string]$ProjectRoot) {
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $lease = [IO.Path]::GetFullPath((Join-Path $root '.artifacts/discovery-portable-managed/.build.lock'))
    if ($null -eq $Lock -or $Lock.ProjectRoot -ine $root -or
        $Lock.BuildLock.Mutex -isnot [Threading.Mutex] -or $Lock.Stream -isnot [IO.FileStream] -or
        -not $Lock.Stream.CanWrite -or $Lock.Stream.Name -ine $lease -or
        $Lock.BuildLock.ProcessId -ne $PID -or $Lock.BuildLock.ThreadId -ne [Threading.Thread]::CurrentThread.ManagedThreadId -or $Lock.BuildLock.Released) {
        throw 'The active project build lock is required.'
    }
}

function Assert-ManagedPath([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Linked managed output path refused.'
            }
        }
    }
}

function Enter-NianManagedDiscoveryLock {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Project root is missing.' }
    $base = [IO.Path]::GetFullPath((Join-Path $root '.artifacts/discovery-portable-managed'))
    Assert-ManagedPath $base
    Import-Module (Join-Path $PSScriptRoot 'BuildArtifactLock.psm1') -ErrorAction Stop
    $lock = Enter-ToolboxBuildLock -ProjectRoot $root
    try {
        [void][IO.Directory]::CreateDirectory($base)
        $path = Join-Path $base '.build.lock'
        Assert-ManagedPath $path
        $stream = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        return [pscustomobject]@{ ProjectRoot=$root; BuildLock=$lock; Stream=$stream }
    } catch { Exit-ToolboxBuildLock -Lock $lock; throw }
}

function Exit-NianManagedDiscoveryLock {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Lock)
    Assert-ManagedLock $Lock $Lock.ProjectRoot
    try { $Lock.Stream.Dispose() } finally { Exit-ToolboxBuildLock -Lock $Lock.BuildLock }
}

function Assert-ManagedTree([string]$Path) {
    Assert-ManagedPath $Path
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked package content refused.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
}

function Get-PolicyContentHash([string]$Path) {
    Assert-ManagedPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Discovery policy is missing.' }
    # Source snapshots can normalize Windows line endings. This fixed digest is
    # the reviewed policy's UTF-8 text with LF line endings; a policy edit must
    # explicitly update this pin and its tests.
    $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8).Replace("`r`n", "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
}

function Get-ManagedModeRoot([string]$ProjectRoot, [string]$Mode) {
    if ($Mode -cnotin @('discovery','fileless','diagnostics')) { throw 'Unknown managed discovery mode.' }
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Project root is missing.' }
    $target = [IO.Path]::GetFullPath((Join-Path $root ('.artifacts/discovery-portable-managed/' + $Mode)))
    Assert-ManagedPath $target
    return $target
}

function Write-ManagedIndex([string]$Root, $State) {
    $target = Join-Path $Root 'retention.json'
    $next = Join-Path $Root 'retention.json.next'
    Assert-ManagedPath $target
    Assert-ManagedPath $next
    $bytes = [Text.Encoding]::UTF8.GetBytes(($State | ConvertTo-Json -Depth 5 -Compress))
    $stream = [IO.File]::Open($next, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($next, $target, $true)
}

function Get-ManagedArchiveName([string]$Mode) {
    switch -CaseSensitive ($Mode) {
        'discovery' { return 'NianLink-discovery-portable-windows-internal-test.zip' }
        'fileless' { return 'NianLink-fileless-portable-windows-internal-test.zip' }
        'diagnostics' { return 'NianLink-fileless-diagnostics-windows-internal-test.zip' }
        default { throw 'Unknown managed discovery mode.' }
    }
}

function Assert-ManagedPackageContents([string]$Target, [string]$Mode, $Manifest) {
    $bundleName = if ($Mode -ceq 'discovery') { '年年互联-发现便携测试' } else { '年年互联-免文件便携测试' }
    $bundle = Join-Path $Target $bundleName
    $names = @($bundleName, (Get-ManagedArchiveName $Mode), 'test-build-status.json')
    $children = @(Get-ChildItem -LiteralPath $Target -Force -ErrorAction Stop)
    if ($children.Count -ne 3 -or @($children | Where-Object { $names -cnotcontains $_.Name -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count) {
        throw 'Unexpected candidate root content.'
    }
    if (-not (Test-Path -LiteralPath $bundle -PathType Container)) { throw 'Candidate bundle is missing.' }
    Assert-ManagedTree $bundle
    if ($null -eq $Manifest -or $Manifest['files'] -isnot [array] -or $Manifest['files'].Count -eq 0) { throw 'Candidate file inventory is missing.' }
    $rows = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $Manifest['files']) {
        if ($row -isnot [System.Collections.IDictionary] -or $row['path'] -isnot [string] -or
            $row['path'] -notmatch '^[^\\:]+$' -or $row['path'] -match '[\x00-\x1f]' -or
            $row['sha256'] -isnot [string] -or $row['sha256'] -cnotmatch '^[A-F0-9]{64}$' -or
            $row['bytes'] -isnot [ValueType] -or [long]$row['bytes'] -lt 0) { throw 'Unsafe candidate file inventory.' }
        $parts = $row['path'].Split('/')
        if (@($parts | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_ -iin @('profile','profiles','test-profiles','trusted-clients') }).Count -or
            $row['path'] -match '(?i)\.(pem|pfx|p12|key)$' -or $rows.ContainsKey($row['path'])) { throw 'Unsafe or duplicate candidate file path.' }
        $file = Join-Path $bundle ($row['path'].Replace('/',[IO.Path]::DirectorySeparatorChar))
        Assert-ManagedPath $file
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne [long]$row['bytes'] -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $row['sha256']) { throw 'Candidate bundle file changed.' }
        $rows.Add($row['path'], $row)
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $bundle -File -Recurse -Force -ErrorAction Stop)) {
        $relative = [IO.Path]::GetRelativePath($bundle, $file.FullName).Replace('\','/')
        if (-not $rows.ContainsKey($relative)) { throw 'Unlisted candidate bundle file.' }
    }
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $Target (Get-ManagedArchiveName $Mode)))
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $prefix = $bundleName + '/'
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected archive path.' }
            if ($entry.FullName.EndsWith('/')) { continue }
            $relative = $entry.FullName.Substring($prefix.Length)
            if (-not $rows.ContainsKey($relative) -or -not $seen.Add($relative) -or $entry.Length -ne [long]$rows[$relative]['bytes']) { throw 'Unexpected or duplicate archive item.' }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($hash -cne $rows[$relative]['sha256']) { throw 'Candidate archive content changed.' }
        }
        if ($seen.Count -ne $rows.Count) { throw 'Candidate archive is incomplete.' }
    } finally { $archive.Dispose() }
}

function Assert-ManagedRecordedPackage([string]$Root, [string]$Mode, $Entry, [bool]$AllowMissing = $false) {
    if ($Entry -isnot [System.Collections.IDictionary] -or $Entry.Count -ne 3 -or
        $Entry.id -isnot [string] -or $Entry.id -cnotmatch '^[a-f0-9]{32}$' -or
        $Entry.manifestHash -isnot [string] -or $Entry.manifestHash -cnotmatch '^[A-F0-9]{64}$' -or
        $Entry.archiveHash -isnot [string] -or $Entry.archiveHash -cnotmatch '^[A-F0-9]{64}$') { throw 'Invalid retained package record.' }
    $target = Join-Path $Root ('test-' + $Entry.id)
    Assert-ManagedPath $target
    if (-not (Test-Path -LiteralPath $target)) {
        if ($AllowMissing) { return }
        throw 'Retained package is missing.'
    }
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'Retained package is not a directory.' }
    Assert-ManagedTree $target
    $status = Join-Path $target 'test-build-status.json'
    $archive = Join-Path $target (Get-ManagedArchiveName $Mode)
    foreach ($file in @($status, $archive)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Retained package evidence is missing.' }
        Assert-ManagedPath $file
    }
    if ((Get-FileHash -LiteralPath $status -Algorithm SHA256).Hash -cne $Entry.manifestHash -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $Entry.archiveHash) { throw 'Retained package was changed.' }
    $manifest = Read-ManagedJson $status
    Assert-ManagedPackageContents $target $Mode $manifest
}

function Assert-UnambiguousJsonValue([System.Text.Json.JsonElement]$Value) {
    if ($Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Value.EnumerateObject()) {
            $name = $property.Name.Normalize([Text.NormalizationForm]::FormC)
            if (-not $names.Add($name)) { throw 'Ambiguous managed JSON object key.' }
            Assert-UnambiguousJsonValue $property.Value
        }
    } elseif ($Value.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Value.EnumerateArray()) { Assert-UnambiguousJsonValue $item }
    }
}

function Read-ManagedJson([string]$Path) {
    Assert-ManagedPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Managed JSON file is missing.' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt 8MB) { throw 'Managed JSON exceeds size limit.' }
        # Bound the read itself, even if another pre-existing writer grows the file.
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $count = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($count -le 0) { throw 'Managed JSON changed while reading.' }
            $offset += $count
        }
        if ($stream.Length -ne $bytes.Length) { throw 'Managed JSON changed while reading.' }
    } finally { $stream.Dispose() }
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $reader = [IO.StreamReader]::new($memory, [Text.Encoding]::UTF8, $true)
    try { $json = $reader.ReadToEnd() } finally { $reader.Dispose(); $memory.Dispose() }
    # Both parsers consume the same bounded snapshot, before any state is written.
    $document = [System.Text.Json.JsonDocument]::Parse($json)
    try { Assert-UnambiguousJsonValue $document.RootElement } finally { $document.Dispose() }
    return ConvertFrom-Json -InputObject $json -AsHashtable
}

function Get-ManagedIndex([string]$Root, [string]$Mode, [bool]$AllowMissingPrevious = $false) {
    $marker = Join-Path $Root '.nian-managed-discovery-v1'
    $index = Join-Path $Root 'retention.json'
    $next = Join-Path $Root 'retention.json.next'
    foreach ($path in @($Root,$marker,$index,$next)) { Assert-ManagedPath $path }
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        [IO.File]::ReadAllText($marker) -cne ('nian-managed-discovery-v1:' + $Mode) -or
        -not (Test-Path -LiteralPath $index -PathType Leaf) -or (Test-Path -LiteralPath $next)) {
        throw 'Managed output is unowned or its index update was interrupted.'
    }
    $state = Read-ManagedJson $index
    if ($null -eq $state -or $state.Count -ne 5 -or $state.schema -cne 'nian-managed-discovery-v1' -or $state.mode -cne $Mode -or
        -not $state.Contains('current') -or -not $state.Contains('previous') -or -not $state.Contains('pending')) { throw 'Invalid managed output index.' }
    $ids = @()
    foreach ($slot in @('current','previous')) {
        if ($null -ne $state[$slot]) {
            Assert-ManagedRecordedPackage $Root $Mode $state[$slot] ($AllowMissingPrevious -and $slot -ceq 'previous')
            $ids += $state[$slot].id
        }
    }
    if ($null -ne $state.previous -and $null -eq $state.current) { throw 'Previous package without current package.' }
    if ($null -ne $state.previous -and $null -ne $state.pending) { throw 'Pending candidate cannot coexist with two retained packages.' }
    if ($null -ne $state.pending) {
        if ($state.pending -isnot [string] -or $state.pending -cnotmatch '^[a-f0-9]{32}$') { throw 'Invalid pending reservation.' }
        $ids += $state.pending
    }
    if (@($ids | Select-Object -Unique).Count -ne $ids.Count) { throw 'Duplicate managed package identity.' }
    $allowed = @('.nian-managed-discovery-v1','retention.json') + @($ids | ForEach-Object { 'test-' + $_ })
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $allowed -cnotcontains $item.Name) { throw 'Unknown or linked managed output.' }
        if ($item.Name -clike 'test-*' -and -not $item.PSIsContainer) { throw 'Managed package must be a directory.' }
    }
    return $state
}

function Clear-NianManagedMissingPrevious {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot,
          [Parameter(Mandatory)][ValidateSet('discovery','fileless','diagnostics')][string]$Mode,
          [Parameter(Mandatory)]$Lock)
    Assert-ManagedLock $Lock $ProjectRoot
    $root = Get-ManagedModeRoot $ProjectRoot $Mode
    $state = Get-ManagedIndex $root $Mode $true
    if ($null -eq $state.previous -or $null -ne $state.pending) { throw 'No missing previous package can be retired.' }
    $previous = Join-Path $root ('test-' + $state.previous.id)
    Assert-ManagedPath $previous
    if (Test-Path -LiteralPath $previous) { throw 'Previous package still exists; it cannot be retired.' }
    # This only updates the index; no candidate or profile is removed.
    $state.previous = $null
    Write-ManagedIndex $root $state
}

function Clear-NianManagedMissingPending {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot,
          [Parameter(Mandatory)][ValidateSet('discovery','fileless','diagnostics')][string]$Mode,
          [Parameter(Mandatory)]$Lock)
    Assert-ManagedLock $Lock $ProjectRoot
    $root = Get-ManagedModeRoot $ProjectRoot $Mode
    $state = Get-ManagedIndex $root $Mode
    if ($null -eq $state.pending) { throw 'No missing pending package can be retired.' }
    $pending = Join-Path $root ('test-' + $state.pending)
    Assert-ManagedPath $pending
    if (Test-Path -LiteralPath $pending) { throw 'Pending package still exists; it cannot be retired.' }
    # This only updates the index; no candidate or profile is removed.
    $state.pending = $null
    Write-ManagedIndex $root $state
}

function Start-NianManagedDiscovery {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot,
          [Parameter(Mandatory)][ValidateSet('discovery','fileless','diagnostics')][string]$Mode,
          [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId,
          [Parameter(Mandatory)]$Lock)
    Assert-ManagedLock $Lock $ProjectRoot
    $root = Get-ManagedModeRoot $ProjectRoot $Mode
    if (-not (Test-Path -LiteralPath $root)) {
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $root))
        [void](New-Item -ItemType Directory -Path $root -ErrorAction Stop)
        $marker = Join-Path $root '.nian-managed-discovery-v1'
        $stream = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.Encoding]::UTF8.GetBytes('nian-managed-discovery-v1:' + $Mode); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Write-ManagedIndex $root ([ordered]@{schema='nian-managed-discovery-v1';mode=$Mode;current=$null;previous=$null;pending=$null})
    }
    $state = Get-ManagedIndex $root $Mode
    if ($null -ne $state.pending -or $null -ne $state.previous) { throw 'A candidate is pending or both retained slots are full; inspect the managed output before building.' }
    $target = Join-Path $root ('test-' + $RunId)
    Assert-ManagedPath $target
    if (Test-Path -LiteralPath $target) { throw 'Managed output already exists.' }
    # Reserve durably before the builder creates the output directory. A crash
    # leaves a pending reservation and subsequent builds stop for inspection.
    $state.pending = $RunId
    Write-ManagedIndex $root $state
    return $target
}

function Complete-NianManagedDiscovery {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot,
          [Parameter(Mandatory)][ValidateSet('discovery','fileless','diagnostics')][string]$Mode,
          [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId,
          [Parameter(Mandatory)]$Lock)
    Assert-ManagedLock $Lock $ProjectRoot
    $root = Get-ManagedModeRoot $ProjectRoot $Mode
    $state = Get-ManagedIndex $root $Mode
    if ($state.pending -cne $RunId -or $null -ne $state.previous) { throw 'Managed candidate does not match the pending reservation.' }
    $target = Join-Path $root ('test-' + $RunId)
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'Pending candidate is missing.' }
    Assert-ManagedTree $target
    $status = Join-Path $target 'test-build-status.json'
    $archive = Join-Path $target (Get-ManagedArchiveName $Mode)
    foreach ($file in @($status,$archive)) {
        Assert-ManagedPath $file
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Candidate status or archive is missing.' }
    }
    $manifest = Read-ManagedJson $status
    if ($null -eq $manifest -or $manifest.testingOnly -isnot [bool] -or $manifest.testingOnly -ne $true -or $manifest.channel -cne 'discovery-portable-v1' -or $manifest.runId -cne $RunId -or
        ($manifest.Contains('filelessIsolatedTest') -and $manifest['filelessIsolatedTest'] -isnot [bool]) -or
        ($manifest.Contains('pairingDiagnostics') -and $manifest['pairingDiagnostics'] -isnot [bool]) -or
        [bool]$manifest['filelessIsolatedTest'] -ne ($Mode -ne 'discovery') -or [bool]$manifest['pairingDiagnostics'] -ne ($Mode -eq 'diagnostics')) {
        throw 'Candidate status does not match the managed mode.'
    }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($manifest.archiveSha256 -cne $archiveHash) { throw 'Candidate archive hash mismatch.' }
    Assert-ManagedPackageContents $target $Mode $manifest
    $statusHash = (Get-FileHash -LiteralPath $status -Algorithm SHA256).Hash
    $policy = Join-Path $ProjectRoot 'scripts/discovery_portable_policy.py'
    $baseline = Join-Path $PSScriptRoot 'discovery_portable_policy.py'
    $pin = 'CD32103F07FF0568C8DC44C5E5125ED6067C460FDE2CE1AD85C1073CAB96ADA4'
    if ((Get-PolicyContentHash $policy) -cne $pin -or (Get-PolicyContentHash $baseline) -cne $pin) { throw 'Discovery policy differs from the reviewed version.' }
    $policyBytesHash = (Get-FileHash -LiteralPath $policy -Algorithm SHA256).Hash
    $python = (Get-Command python -ErrorAction Stop).Source
    $bundleName = if ($Mode -ceq 'discovery') { '年年互联-发现便携测试' } else { '年年互联-免文件便携测试' }
    $policyOutput = & $python -B $policy --bundle (Join-Path $target $bundleName) --source $ProjectRoot --manifest $status
    if ($LASTEXITCODE -ne 0) { throw 'Final discovery policy failed; the candidate remains pending.' }
    if ((Get-PolicyContentHash $policy) -cne $pin -or (Get-PolicyContentHash $baseline) -cne $pin -or
        (Get-FileHash -LiteralPath $policy -Algorithm SHA256).Hash -cne $policyBytesHash -or
        (Get-FileHash -LiteralPath $status -Algorithm SHA256).Hash -cne $statusHash -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $archiveHash) { throw 'Candidate changed during final policy verification.' }
    Assert-ManagedPackageContents $target $Mode $manifest
    $entry = [ordered]@{id=$RunId;manifestHash=$statusHash;archiveHash=$archiveHash}
    $state.previous = $state.current
    $state.current = $entry
    $state.pending = $null
    Write-ManagedIndex $root $state
}

Export-ModuleMember -Function Enter-NianManagedDiscoveryLock,Exit-NianManagedDiscoveryLock,Start-NianManagedDiscovery,Complete-NianManagedDiscovery,Clear-NianManagedMissingPrevious,Clear-NianManagedMissingPending
