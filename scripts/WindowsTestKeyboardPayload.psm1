#requires -Version 7.0
Set-StrictMode -Version Latest

# These identify the previously reviewed internal-test payload, not its upstream
# provenance or license status. Changing the payload requires a new review.
function Get-WindowsTestEngineManifest {
    @(
        [pscustomobject]@{ path='deskflow-core.exe'; sha256='04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA' }
        [pscustomobject]@{ path='libcrypto-3-x64.dll'; sha256='6ED47166EC5B90ED4FA6B95CEF063A7E95380EC451EF3E76BE7B356422FF996B' }
        [pscustomobject]@{ path='libssl-3-x64.dll'; sha256='134027937006C66800D81C45D831ED34C25422ACD4B6C165E30CA76FAE128438' }
        [pscustomobject]@{ path='Qt6Core.dll'; sha256='0CC62E841959FB8F6D366EA116C6F6BBC273E30F0EF90D8AA2E4ADEB81FD63F8' }
        [pscustomobject]@{ path='Qt6Gui.dll'; sha256='CA9F20D9B59D5BC321A87005B926B853EBCD6A2394823E14D3C8B5FC5702FA8C' }
        [pscustomobject]@{ path='Qt6Network.dll'; sha256='2C2F65C70989646F06D668883C031F06FAC3F8DE1F459278C94CC50DFE89988D' }
        [pscustomobject]@{ path='Qt6Widgets.dll'; sha256='D34EF78B3D3B5506EE8279CE6EFBFC263D773B74860AF76E66B0715346897A3A' }
        [pscustomobject]@{ path='platforms/qoffscreen.dll'; sha256='733D193144FB9C8A00B6D5E96C730668C86AEF16DB63754914D11E135D56E5A2' }
        [pscustomobject]@{ path='platforms/qwindows.dll'; sha256='12577A7C4F2230BBEA53F279573D7E3D1A08D631197855481E1E1580F61BB877' }
    )
}

function Assert-TestEngineUnlinkedPath([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Linked keyboard payload path refused.'
            }
        }
    }
}

function Assert-TestEngineEntries([object[]]$Entries) {
    $seen = @{}
    foreach ($entry in $Entries) {
        $relative = [string]$entry.path
        if ($relative -cnotmatch '^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$' -or
            @($relative.Split('/') | Where-Object { $_ -in @('.', '..') -or $_.EndsWith('.') }).Count -or
            [string]$entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $seen.ContainsKey($relative)) {
            throw 'Invalid or duplicate keyboard payload entry.'
        }
        $seen[$relative] = $true
    }
    if ($seen.Count -eq 0) { throw 'Keyboard payload manifest is empty.' }
}

function Assert-TestEngineItems([string]$Directory, [object[]]$Entries) {
    Assert-TestEngineEntries $Entries
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw 'Keyboard payload directory missing.' }
    Assert-TestEngineUnlinkedPath $Directory
    foreach ($entry in $Entries) {
        $path = Join-Path $Directory $entry.path
        Assert-TestEngineUnlinkedPath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine [string]$entry.sha256) {
            throw 'Keyboard payload missing or differs from the reviewed test sample.'
        }
    }
}

function Copy-TestEngineItems([string]$Source, [string]$Destination, [object[]]$Entries) {
    Assert-TestEngineItems -Directory $Source -Entries $Entries
    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { throw 'Keyboard output directory missing.' }
    Assert-TestEngineUnlinkedPath $Destination
    foreach ($entry in $Entries) {
        $target = Join-Path $Destination $entry.path
        Assert-TestEngineUnlinkedPath $target
        if (Test-Path -LiteralPath $target) { throw 'Existing keyboard payload refused.' }
    }
    foreach ($entry in $Entries) {
        $target = Join-Path $Destination $entry.path
        $parent = [IO.Path]::GetDirectoryName($target)
        [void][IO.Directory]::CreateDirectory($parent)
        Assert-TestEngineUnlinkedPath $parent
        $inputFile = [IO.File]::OpenRead((Join-Path $Source $entry.path))
        try {
            $outputFile = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputFile.CopyTo($outputFile) } finally { $outputFile.Dispose() }
        } finally { $inputFile.Dispose() }
    }
    Assert-TestEngineItems -Directory $Destination -Entries $Entries
}

function Assert-TestEngineArchive([System.Collections.IDictionary]$Entries, [object[]]$PinnedItems) {
    Assert-TestEngineEntries $PinnedItems
    $allowed = @{}
    $markerName = 'Keyboard/settings/Deskflow.conf'
    $allowed[$markerName] = $true
    if (-not $Entries.Contains($markerName)) { throw 'Portable settings marker missing from archive.' }
    $markerStream = $Entries[$markerName].Open()
    try { $markerHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($markerStream)) } finally { $markerStream.Dispose() }
    if ($markerHash -cne '36A9E7F1C95B82FFB99743E0C5C4CE95D83C9A430AAC59F84EF3CBFAB6145068') { throw 'Archived portable settings marker differs from reviewed sample.' }
    foreach ($item in $PinnedItems) {
        $name = 'Keyboard/' + [string]$item.path
        $allowed[$name] = $true
        if (-not $Entries.Contains($name)) { throw 'Pinned keyboard payload missing from archive.' }
        $stream = $Entries[$name].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($hash -ine [string]$item.sha256) { throw 'Archived keyboard payload differs from the reviewed test sample.' }
    }
    foreach ($name in $Entries.Keys) {
        if ([string]$name -like 'Keyboard/*' -and -not $allowed.ContainsKey([string]$name)) {
            throw 'Unexpected keyboard payload in archive.'
        }
    }
}

function Assert-WindowsTestEngineInputs([string]$Directory) {
    Assert-TestEngineItems -Directory $Directory -Entries @(Get-WindowsTestEngineManifest)
}

function Copy-WindowsTestEngineInputs([string]$Source, [string]$Destination) {
    Copy-TestEngineItems -Source $Source -Destination $Destination -Entries @(Get-WindowsTestEngineManifest)
}

function Assert-WindowsTestEngineArchive([System.Collections.IDictionary]$Entries) {
    Assert-TestEngineArchive -Entries $Entries -PinnedItems @(Get-WindowsTestEngineManifest)
}

Export-ModuleMember -Function Assert-WindowsTestEngineInputs, Copy-WindowsTestEngineInputs, Assert-WindowsTestEngineArchive
