#requires -Version 7.0
Set-StrictMode -Version Latest

# Experimental isolated index; no production builder calls this module.
function Assert-MacManagedPath([string]$Path) {
    for ($p = [IO.Path]::GetFullPath($Path); $p; $p = [IO.Path]::GetDirectoryName($p)) {
        if (Test-Path -LiteralPath $p) {
            if ((Get-Item -LiteralPath $p -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Linked managed path refused.'
            }
        }
    }
}

function Enter-NianManagedMacFilelessLock {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    Import-Module (Join-Path $PSScriptRoot 'BuildArtifactLock.psm1') -ErrorAction Stop
    $projectLock = Enter-ToolboxBuildLock -ProjectRoot $root
    try {
        $base = Join-Path $root '.artifacts/mac-package-managed'
        Assert-MacManagedPath $base
        [void][IO.Directory]::CreateDirectory($base)
        $path = Join-Path $base '.build.lock'
        Assert-MacManagedPath $path
        $stream = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        return [pscustomobject]@{ ProjectRoot=$root; BuildLock=$projectLock; Stream=$stream }
    } catch { Exit-ToolboxBuildLock -Lock $projectLock; throw }
}

function Assert-MacManagedLock($Lock, [string]$ProjectRoot) {
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $root '.artifacts/mac-package-managed/.build.lock'))
    if ($null -eq $Lock -or $Lock.ProjectRoot -ine $root -or $Lock.Stream -isnot [IO.FileStream] -or
        -not $Lock.Stream.CanWrite -or $Lock.Stream.Name -ine $path -or
        $Lock.BuildLock.ProcessId -ne $PID -or $Lock.BuildLock.ThreadId -ne [Threading.Thread]::CurrentThread.ManagedThreadId -or $Lock.BuildLock.Released) {
        throw 'The matching project and managed lease are required.'
    }
    Assert-MacManagedPath $path
}

function Exit-NianManagedMacFilelessLock {
    param([Parameter(Mandatory)]$Lock)
    Assert-MacManagedLock $Lock $Lock.ProjectRoot
    try { $Lock.Stream.Dispose() } finally { Exit-ToolboxBuildLock -Lock $Lock.BuildLock }
}

function Write-MacManagedIndex([string]$Root, $State) {
    $target = Join-Path $Root 'retention.json'
    $next = Join-Path $Root 'retention.json.next'
    Assert-MacManagedPath $target
    Assert-MacManagedPath $next
    $bytes = [Text.Encoding]::UTF8.GetBytes(($State | ConvertTo-Json -Depth 6 -Compress))
    $stream = [IO.File]::Open($next, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($next, $target, $true)
}

function Get-MacManagedTreeHash([string]$Target) {
    Assert-MacManagedPath $Target
    if (-not (Test-Path -LiteralPath $Target -PathType Container)) { throw 'Recorded candidate is missing.' }
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($item in @(Get-ChildItem -LiteralPath $Target -Recurse -Force -ErrorAction Stop)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked candidate content refused.' }
        $relative = [IO.Path]::GetRelativePath($Target, $item.FullName).Replace('\','/')
        if ($relative -match '[\x00-\x1f\x7f]' -or $relative -match '(^|/)(\.|\.\.)(/|$)') { throw 'Unsafe candidate content name.' }
        if ($item.PSIsContainer) { $rows.Add('D:' + $relative) }
        else { $rows.Add('F:' + $relative + ':' + $item.Length + ':' + (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash) }
        if ($rows.Count -gt 20000) { throw 'Candidate has too many items.' }
    }
    $rows.Sort([StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Assert-MacManagedRecord([string]$Root, $Record, [bool]$AllowMissing = $false) {
    if ($Record -isnot [System.Collections.IDictionary] -or $Record.Count -ne 3 -or
        $Record.id -isnot [string] -or $Record.id -cnotmatch '^[a-f0-9]{32}$' -or
        $Record.treeHash -isnot [string] -or $Record.treeHash -cnotmatch '^[A-F0-9]{64}$' -or
        $Record.archiveHash -isnot [string] -or $Record.archiveHash -cnotmatch '^[A-F0-9]{64}$') { throw 'Invalid recorded Mac package.' }
    $target = Join-Path $Root ('test-' + $Record.id)
    Assert-MacManagedPath $target
    if ($AllowMissing -and -not (Test-Path -LiteralPath $target)) { return }
    if ((Get-MacManagedTreeHash $target) -cne $Record.treeHash) { throw 'Recorded Mac package changed.' }
    $archives = @(Get-ChildItem -LiteralPath $target -File -Filter 'NianDeskLink-*-osx-arm64.zip' -Force)
    if ($archives.Count -ne 1 -or (Get-FileHash -LiteralPath $archives[0].FullName -Algorithm SHA256).Hash -cne $Record.archiveHash) { throw 'Recorded Mac archive changed.' }
}

function Assert-MacUniqueJsonElement([System.Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            $key = $property.Name.Normalize([Text.NormalizationForm]::FormC).ToUpperInvariant()
            if (-not $keys.Add($key)) { throw 'Duplicate or ambiguous managed Mac JSON key.' }
            Assert-MacUniqueJsonElement $property.Value
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($value in $Element.EnumerateArray()) { Assert-MacUniqueJsonElement $value }
    }
}

function Assert-MacUniqueJson([string]$Text) {
    $document = [System.Text.Json.JsonDocument]::Parse($Text)
    try { Assert-MacUniqueJsonElement $document.RootElement } finally { $document.Dispose() }
}

function Assert-NianManagedMacUniqueJson {
    param([Parameter(Mandatory)][string]$Text)
    Assert-MacUniqueJson $Text
}

function Get-MacManagedIndex([string]$Root, [bool]$AllowMissingPrevious = $false) {
    $marker = Join-Path $Root '.nian-managed-mac-fileless-v1'
    $index = Join-Path $Root 'retention.json'
    $next = Join-Path $Root 'retention.json.next'
    foreach ($path in @($Root,$marker,$index,$next)) { Assert-MacManagedPath $path }
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or
        [IO.File]::ReadAllText($marker) -cne 'nian-managed-mac-fileless-v1' -or
        -not (Test-Path -LiteralPath $index -PathType Leaf) -or (Test-Path -LiteralPath $next)) {
        throw 'Unowned or interrupted managed Mac index.'
    }
    if ((Get-Item -LiteralPath $index -Force).Length -gt 8MB) { throw 'Managed Mac index is oversized.' }
    $text = Get-Content -LiteralPath $index -Raw -Encoding UTF8
    Assert-MacUniqueJson $text
    $state = $text | ConvertFrom-Json -AsHashtable
    if ($state -isnot [System.Collections.IDictionary] -or $state.Count -ne 4 -or
        $state.schema -cne 'nian-managed-mac-fileless-v1' -or -not $state.Contains('current') -or
        -not $state.Contains('previous') -or -not $state.Contains('pending')) { throw 'Invalid managed Mac index.' }
    foreach ($slot in @('current','previous')) {
        if ($null -ne $state[$slot]) { Assert-MacManagedRecord $Root $state[$slot] ($AllowMissingPrevious -and $slot -ceq 'previous') }
    }
    if ($null -ne $state.previous -and ($null -eq $state.current -or $null -ne $state.pending)) { throw 'Impossible managed Mac state.' }
    if ($null -ne $state.pending -and ($state.pending -isnot [string] -or $state.pending -cnotmatch '^[a-f0-9]{32}$')) { throw 'Invalid pending reservation.' }
    $allowed = @('.nian-managed-mac-fileless-v1','retention.json')
    $ids = @()
    foreach ($slot in @('current','previous')) {
        if ($null -ne $state[$slot]) { $ids += $state[$slot].id }
    }
    if ($null -ne $state.pending) { $ids += $state.pending }
    if (@($ids | Select-Object -Unique).Count -ne $ids.Count) { throw 'Duplicate managed Mac identity.' }
    foreach ($id in $ids) { $allowed += 'test-' + $id }
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $allowed -cnotcontains $item.Name -or
            ($item.Name -clike 'test-*' -and -not $item.PSIsContainer)) { throw 'Unknown or linked managed Mac output.' }
    }
    return $state
}

function Start-NianManagedMacFileless {
    param([Parameter(Mandatory)][string]$ProjectRoot,
          [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId,
          [Parameter(Mandatory)]$Lock)
    Assert-MacManagedLock $Lock $ProjectRoot
    $root = Join-Path $Lock.ProjectRoot '.artifacts/mac-package-managed/fileless'
    Assert-MacManagedPath $root
    if (-not (Test-Path -LiteralPath $root)) {
        [void](New-Item -ItemType Directory -Path $root -ErrorAction Stop)
        $marker = Join-Path $root '.nian-managed-mac-fileless-v1'
        $stream = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.Encoding]::UTF8.GetBytes('nian-managed-mac-fileless-v1'); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Write-MacManagedIndex $root ([ordered]@{schema='nian-managed-mac-fileless-v1';current=$null;previous=$null;pending=$null})
    }
    $state = Get-MacManagedIndex $root
    if ($null -ne $state.pending -or $null -ne $state.previous) { throw 'A Mac candidate is pending or both retained slots are full.' }
    $target = Join-Path $root ('test-' + $RunId)
    Assert-MacManagedPath $target
    if (Test-Path -LiteralPath $target) { throw 'Candidate already exists.' }
    $state.pending = $RunId
    Write-MacManagedIndex $root $state
    return $target
}

function Clear-NianManagedMacMissingPending {
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)]$Lock)
    Assert-MacManagedLock $Lock $ProjectRoot
    $root = Join-Path $Lock.ProjectRoot '.artifacts/mac-package-managed/fileless'
    $state = Get-MacManagedIndex $root
    if ($null -eq $state.pending) { throw 'No pending candidate to retire.' }
    $target = Join-Path $root ('test-' + $state.pending)
    Assert-MacManagedPath $target
    if (Test-Path -LiteralPath $target) { throw 'Pending candidate still exists.' }
    $state.pending = $null
    Write-MacManagedIndex $root $state
}

function Clear-NianManagedMacMissingPrevious {
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)]$Lock)
    Assert-MacManagedLock $Lock $ProjectRoot
    $root = Join-Path $Lock.ProjectRoot '.artifacts/mac-package-managed/fileless'
    $state = Get-MacManagedIndex $root $true
    if ($null -eq $state.previous -or $null -ne $state.pending) { throw 'No missing previous candidate can be retired.' }
    $target = Join-Path $root ('test-' + $state.previous.id)
    Assert-MacManagedPath $target
    if (Test-Path -LiteralPath $target) { throw 'Previous candidate still exists.' }
    $state.previous = $null
    Write-MacManagedIndex $root $state
}

function Get-MacReviewedTextHash([string]$Path) {
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true))
    $text = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
}

function Assert-NianManagedMacVerifierInputs {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $checks = [ordered]@{
        'scripts/verify_mac_fileless_candidate.py' = '500665D533F657782F791C1C33D21C512D53911509061ACE0A928427889F7DAD'
        'src/DesktopToolbox.Mac/build/package_app.py' = 'C65A0F85DC5381D4A9B2DC23424EADDACF9496410B6AA476D2B869738BFE2B74'
        'src/DesktopToolbox.Mac/build/preview_identity.py' = '1D753A5A05B9939E9254C2DB26137A8CF18FE4555639E35B69CBA3D38C3007FD'
    }
    $rawHashes = @{}
    foreach ($relative in $checks.Keys) {
        $path = Join-Path $root $relative
        Assert-MacManagedPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-MacReviewedTextHash $path) -cne $checks[$relative]) { throw 'Reviewed Mac verifier or dependency differs.' }
        $rawHashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    return $rawHashes
}

function Complete-NianManagedMacFileless {
    param([Parameter(Mandatory)][string]$ProjectRoot,
          [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId,
          [Parameter(Mandatory)][ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')][string]$Version,
          [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$BridgeSha256,
          [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$EvidenceSha256,
          [Parameter(Mandatory)]$Lock)
    Assert-MacManagedLock $Lock $ProjectRoot
    $root = Join-Path $Lock.ProjectRoot '.artifacts/mac-package-managed/fileless'
    $state = Get-MacManagedIndex $root
    if ($state.pending -cne $RunId -or $null -ne $state.previous) { throw 'Pending Mac candidate does not match.' }
    $target = Join-Path $root ('test-' + $RunId)
    $rawHashes = Assert-NianManagedMacVerifierInputs -ProjectRoot $Lock.ProjectRoot
    $treeHash = Get-MacManagedTreeHash $target
    $archive = Join-Path $target ("NianDeskLink-$Version-osx-arm64.zip")
    Assert-MacManagedPath $archive
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'Candidate archive is missing.' }
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $python = (Get-Command python -ErrorAction Stop).Source
    $verifier = Join-Path $Lock.ProjectRoot 'scripts/verify_mac_fileless_candidate.py'
    $result = & $python -B $verifier --project-root $Lock.ProjectRoot --candidate $target --version $Version --bridge-sha256 $BridgeSha256 --evidence-sha256 $EvidenceSha256 --managed-fileless
    if ($LASTEXITCODE -ne 0) { throw 'Final Mac candidate verification failed; reservation remains pending.' }
    $afterHashes = Assert-NianManagedMacVerifierInputs -ProjectRoot $Lock.ProjectRoot
    foreach ($relative in $rawHashes.Keys) {
        $path = Join-Path $Lock.ProjectRoot $relative
        Assert-MacManagedPath $path
        if ($afterHashes[$relative] -cne $rawHashes[$relative]) { throw 'Verifier changed during checking.' }
    }
    if ((Get-MacManagedTreeHash $target) -cne $treeHash -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $archiveHash) { throw 'Candidate changed during checking.' }
    $state.previous = $state.current
    $state.current = [ordered]@{ id=$RunId; treeHash=$treeHash; archiveHash=$archiveHash }
    $state.pending = $null
    Write-MacManagedIndex $root $state
}

Export-ModuleMember -Function Enter-NianManagedMacFilelessLock,Exit-NianManagedMacFilelessLock,Assert-NianManagedMacVerifierInputs,Assert-NianManagedMacUniqueJson,Start-NianManagedMacFileless,Clear-NianManagedMacMissingPending,Clear-NianManagedMacMissingPrevious,Complete-NianManagedMacFileless
