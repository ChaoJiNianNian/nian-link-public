#requires -Version 7.0
Set-StrictMode -Version Latest

# The caller holds the project build mutex, then the shared cross-session file
# lease, until it finishes using the cache. Profiles and package IDs stay unique.
function Assert-NianFilelessPath([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Linked fileless cache path refused.'
            }
        }
    }
}

function Enter-NianFilelessCacheLease {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'The project root does not exist.' }
    $base = Join-Path $root 'tests/.work/reusable-fileless-build'
    Assert-NianFilelessPath $base
    [void][IO.Directory]::CreateDirectory($base)
    $path = Join-Path $base '.cache-build.lock'
    Assert-NianFilelessPath $path
    $stream = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    return [pscustomobject]@{ ProjectRoot=$root; Stream=$stream; ProcessId=$PID; ThreadId=[Threading.Thread]::CurrentThread.ManagedThreadId }
}

function Assert-NianFilelessCacheLease($Lease, [string]$ProjectRoot) {
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $root 'tests/.work/reusable-fileless-build/.cache-build.lock'))
    if ($null -eq $Lease -or $Lease.ProjectRoot -ine $root -or $Lease.Stream -isnot [IO.FileStream] -or
        -not $Lease.Stream.CanWrite -or $Lease.Stream.Name -ine $path -or $Lease.ProcessId -ne $PID -or
        $Lease.ThreadId -ne [Threading.Thread]::CurrentThread.ManagedThreadId) { throw 'The project fileless cache lease is required.' }
    Assert-NianFilelessPath $path
}

function Exit-NianFilelessCacheLease {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Lease)
    Assert-NianFilelessCacheLease $Lease $Lease.ProjectRoot
    $Lease.Stream.Dispose()
}

function Get-NianDiscoveryBuildProperties {
    [CmdletBinding()]
    param([bool]$FilelessTesting = $false, [bool]$PairingDiagnostics = $false)
    if ($PairingDiagnostics -and -not $FilelessTesting) { throw 'Diagnostics requires the isolated fileless candidate.' }
    if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('DefineConstants', 'Process'))) {
        throw 'The process DefineConstants environment variable must be empty for an isolated discovery build.'
    }
    $fileless = $FilelessTesting.ToString().ToLowerInvariant()
    $diagnostics = $PairingDiagnostics.ToString().ToLowerInvariant()
    return @("-p:NianFilelessPairingCandidate=$fileless", "-p:NianFilelessPairingIsolatedTest=$fileless",
             "-p:NianPairingDiagnostics=$diagnostics", '-p:NianPeerEmergencyResearch=false', '-p:NianFenceResearchIsolatedTest=false')
}

function Get-NianFilelessArtifactCache {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][ValidateSet('windows','mac')][string]$Platform,
        [bool]$PairingDiagnostics = $false,
        [bool]$DiscoveryOnly = $false,
        [Parameter(Mandatory)]$Lease
    )
    if ($Platform -eq 'mac' -and $PairingDiagnostics) { throw 'Mac fileless builds do not support Windows pairing diagnostics.' }
    if ($DiscoveryOnly -and ($Platform -ne 'windows' -or $PairingDiagnostics)) { throw 'Discovery-only cache is restricted to ordinary Windows discovery.' }
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'The project root does not exist.' }
    Assert-NianFilelessCacheLease $Lease $root
    $mode = if ($DiscoveryOnly) { 'windows-discovery' } elseif ($Platform -eq 'mac') { 'mac-fileless' } elseif ($PairingDiagnostics) { 'windows-fileless-diagnostics' } else { 'windows-fileless' }
    $target = Join-Path $root ('tests/.work/reusable-fileless-build/' + $mode)
    $marker = Join-Path $target '.nian-fileless-build-cache'
    $expected = 'nian-fileless-build-cache-v1:' + $mode
    # Refuse a linked target, parent, project, or other ancestor before creating or reading anything.
    for ($cursor = $target; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked build cache path refused.' }
            if (-not $entry.PSIsContainer) { throw 'Build cache path must be a directory.' }
        }
    }
    if (Test-Path -LiteralPath $target) {
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($target)
        while ($pending.Count -gt 0) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked build cache contents refused.' }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf) -or [IO.File]::ReadAllText($marker) -cne $expected) {
            throw 'Existing build cache is unowned or has the wrong configuration.'
        }
    } else {
        [void][IO.Directory]::CreateDirectory($target)
        # CreateNew refuses to replace any unexpected file, even if a competing writer appears.
        $stream = [IO.File]::Open($marker, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes($expected)
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
    }
    return $target
}

function Invoke-NianFilelessPublish {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Dotnet, [Parameter(Mandatory)][string[]]$PublishArgs, [Parameter(Mandatory)]$Lease)
    Assert-NianFilelessCacheLease $Lease $Lease.ProjectRoot
    if ($PublishArgs.Count -lt 4 -or $PublishArgs[0] -cne 'publish') { throw 'Expected an explicit publish command.' }
    # Rebuild the whole project graph even if a copied source has an older timestamp.
    # Keep every publish property/RID/package argument; only omit its output folder.
    $buildArgs = @('build')
    $outputs = 0
    for ($i = 1; $i -lt $PublishArgs.Count; $i++) {
        if ($PublishArgs[$i] -ceq '--output') {
            $outputs++
            $i++
            if ($i -ge $PublishArgs.Count) { throw 'Publish output directory is missing.' }
        } else {
            if ($PublishArgs[$i] -in @('--no-build','--no-restore','--no-incremental')) { throw 'Unsupported publish override.' }
            $buildArgs += $PublishArgs[$i]
        }
    }
    if ($outputs -ne 1) { throw 'Exactly one explicit publish output directory is required.' }
    $buildArgs += '--no-incremental'
    & $Dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Fileless clean rebuild failed; publish was not started.' }
    & $Dotnet @PublishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Fileless publish failed.' }
}
Export-ModuleMember -Function Enter-NianFilelessCacheLease,Exit-NianFilelessCacheLease,Get-NianDiscoveryBuildProperties,Get-NianFilelessArtifactCache,Invoke-NianFilelessPublish
