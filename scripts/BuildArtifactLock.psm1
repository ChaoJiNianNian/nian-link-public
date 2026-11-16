#requires -Version 7.0
Set-StrictMode -Version Latest

# A single project-local lock coordinates all isolated package builders.
function Enter-ToolboxBuildLock {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot)
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [IO.Directory]::Exists($root)) { throw 'The project directory does not exist.' }
    $cursor = $root
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build locks cannot use a linked project path.' }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant())))
    $mutex = [Threading.Mutex]::new($false, 'Local\NianLink.Build.' + $digest)
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another operation is using this project build workspace; try again after it finishes.' }
        return [pscustomobject]@{ Mutex=$mutex; ProcessId=$PID; ThreadId=[Threading.Thread]::CurrentThread.ManagedThreadId; Released=$false }
    } catch {
        if (-not $acquired) { $mutex.Dispose() }
        throw
    }
}

function Exit-ToolboxBuildLock {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Lock)
    if ($Lock.ProcessId -ne $PID -or $Lock.ThreadId -ne [Threading.Thread]::CurrentThread.ManagedThreadId -or $Lock.Mutex -isnot [Threading.Mutex]) {
        throw 'The build lock must be released by its owning process and thread.'
    }
    if ($Lock.Released) { return }
    $Lock.Mutex.ReleaseMutex()
    $Lock.Released = $true
    $Lock.Mutex.Dispose()
}

Export-ModuleMember -Function Enter-ToolboxBuildLock,Exit-ToolboxBuildLock
