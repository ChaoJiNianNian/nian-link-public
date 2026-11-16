#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OfficialPortableZip,
    [Parameter(Mandatory)][string]$IntegratedRuntimeDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$OfficialPortableZip=[IO.Path]::GetFullPath($OfficialPortableZip)
$IntegratedRuntimeDirectory=[IO.Path]::GetFullPath($IntegratedRuntimeDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$vendor=Join-Path $root 'vendor/deskflow'
$metadata=Join-Path $vendor 'metadata'
$original=Join-Path $vendor 'original'
$integrated=Join-Path $vendor 'integrated'
foreach($path in @($original,$integrated)) { if(Test-Path -LiteralPath $path) { throw "Refusing to replace staged dependency: $path" } }
function Assert-FileHash([string]$Path,[string]$Expected) {
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Missing or linked dependency: $Path"}
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $Expected.ToUpperInvariant()){throw "Dependency hash mismatch: $Path"}
}
function Assert-SafeRelative([string]$Name) {
    if([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 240 -or $Name.Contains('\') -or $Name.Contains(':') -or $Name -match '[\x00-\x1f]' -or
       @($Name.Split('/') | Where-Object {$_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ')}).Count) { throw "Unsafe dependency path: $Name" }
}
function Assert-NoLinks([string]$Path) {
    $item=Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Linked dependency refused: $Path"}
}
foreach($path in @($root,$OfficialPortableZip,$IntegratedRuntimeDirectory)) {
    for($cursor=$path; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) { if(Test-Path -LiteralPath $cursor) {Assert-NoLinks $cursor} }
}
Assert-FileHash $OfficialPortableZip '8D19004CC3625623489D96580C1B58FD13EE78185689907FD4478595F93FAB9A'
$manifestPath=Join-Path $metadata 'original-manifest.json'
Assert-FileHash $manifestPath '2C5B9EC650DC9F8EB4144F92D3BD25D7BF9356735F2E8A9223131A38A4765E7F'
Assert-FileHash (Join-Path $metadata 'integrated-provenance.json') 'E79189268D860DFAC94184468C7E785DE357EF36795DC7CB32C10420E0BA21A5'
Assert-FileHash (Join-Path $metadata '0001-current-user-native-control.patch') '4553424FCF97B368C4356D647D5ED25B7E529DABA1B00FE4CEBD6A8F047E5324'
$integratedManifest=Join-Path $IntegratedRuntimeDirectory 'toolbox-package-manifest.json'
Assert-NoLinks $IntegratedRuntimeDirectory
Assert-FileHash $integratedManifest '2EF19504292F5408D6C5ADC7E405EED4B79BE64D7C8354916E0B3C057ADF378C'
$sourceManifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$buildManifest=Get-Content -LiteralPath $integratedManifest -Raw | ConvertFrom-Json
if($sourceManifest.version -cne '1.27.0' -or $buildManifest.upstreamVersion -cne '1.27.0' -or $buildManifest.controlProtocolVersion -ne 1){throw 'Deskflow version mismatch.'}
$all=@{}; foreach($item in $buildManifest.files) {
    $rel=[string]$item.path; Assert-SafeRelative $rel
    if($rel -match '(?i)(^|/)(settings|profile|\.git)(/|$)|\.(?:key|pem|p12|pfx|db)$' -or $all.ContainsKey($rel)){throw "Private or duplicate runtime path: $rel"}
    $all[$rel]=$item.sha256
    $path=Join-Path $IntegratedRuntimeDirectory $rel
    for($cursor=$path; $cursor -and $cursor.StartsWith([IO.Path]::GetFullPath($IntegratedRuntimeDirectory),[StringComparison]::OrdinalIgnoreCase); $cursor=[IO.Path]::GetDirectoryName($cursor)) { Assert-NoLinks $cursor }
    Assert-FileHash $path ([string]$item.sha256)
}
$prefix='deskflow-1.27.0-win-x64-portable/'
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $OfficialPortableZip).Path)
try {
    $map=@{}; foreach($entry in $zip.Entries){
        if(-not $entry.FullName.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'Unexpected original ZIP root.'}
        if($entry.FullName.EndsWith('/')){continue}
        $relative=$entry.FullName.Substring($prefix.Length); Assert-SafeRelative $relative
        if($map.ContainsKey($relative)){throw 'Duplicate original archive item.'}; $map[$relative]=$entry
    }
    if($map.Count -ne @($sourceManifest.files).Count){throw 'Original archive entry count differs from pinned manifest.'}
    foreach($item in $sourceManifest.files){
        $rel=[string]$item.path; Assert-SafeRelative $rel
        if(-not $map.ContainsKey($rel)){throw "Missing original archive item: $rel"}
        if($rel -ne 'settings/Deskflow.conf' -and $rel -match '(?i)(^|/)(settings|profile)(/|$)|\.(?:key|pem|p12|pfx|db)$'){throw 'Original archive contains unexpected private configuration.'}
        $input=$map[$rel].Open(); try {$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input))} finally {$input.Dispose()}
        if($hash -cne ([string]$item.sha256).ToUpperInvariant()){throw "Original archive hash mismatch: $rel"}
    }
    New-Item -ItemType Directory -Path $original,$integrated | Out-Null
    foreach($item in $sourceManifest.files){
        $rel=[string]$item.path; $out=Join-Path $original $rel; New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force | Out-Null
        if($rel -eq 'settings/Deskflow.conf') {[IO.File]::WriteAllBytes($out,[byte[]]@(0x20))} # public single-space portable marker, not a copied user configuration
        else { $input=$map[$rel].Open(); $output=[IO.File]::Open($out,[IO.FileMode]::CreateNew); try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()} }
        Assert-FileHash $out ([string]$item.sha256)
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $original 'package-manifest.json')
} finally {$zip.Dispose()}
foreach($item in $buildManifest.files){$rel=[string]$item.path; $out=Join-Path $integrated $rel; New-Item -ItemType Directory -Path (Split-Path -Parent $out) -Force | Out-Null; Copy-Item -LiteralPath (Join-Path $IntegratedRuntimeDirectory $rel) -Destination $out; Assert-FileHash $out ([string]$item.sha256)}
Copy-Item -LiteralPath $integratedManifest -Destination (Join-Path $integrated 'toolbox-package-manifest.json')
Write-Output 'Deskflow 1.27.0 assets staged and verified; no private profiles or credentials copied.'
