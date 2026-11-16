#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$PackagesDirectory,
    [Parameter(Mandatory)][string]$Version,
    [string]$MacClientDirectory,
    [switch]$TestingOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(-not $TestingOnly){throw 'Only an internal test installer is available until corresponding source and licensing are independently verified.'}
if($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'){throw 'Provide a canonical three-part version.'}
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$Dotnet=[IO.Path]::GetFullPath($Dotnet); $Compiler=[IO.Path]::GetFullPath($Compiler); $PackagesDirectory=[IO.Path]::GetFullPath($PackagesDirectory)
foreach($path in @($Dotnet,$Compiler)){if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Build tool missing: $path"}}
if(-not (Test-Path -LiteralPath $PackagesDirectory -PathType Container)){throw 'The explicit NuGet package cache is missing.'}
$signature=Get-AuthenticodeSignature -LiteralPath $Compiler
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*'){throw 'Unverified Inno Setup compiler.'}
Import-Module (Join-Path $PSScriptRoot 'SdkBaseline.psm1')
Assert-NianSdk -Dotnet $Dotnet -ProjectRoot $root
$release=Join-Path $root "releases/v$Version-test"
if(Test-Path -LiteralPath $release){throw "Refusing to replace existing output: $release"}
# The caller coordinates builds for this project. Mac payloads are optional;
# accepting them requires both a strict project-owned path and full bundle validation.
$macClient=$null; $macHashes=$null; $python=$null
if(-not [string]::IsNullOrWhiteSpace($MacClientDirectory)) {
    $macClient=[IO.Path]::GetFullPath($MacClientDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $macRoot=[IO.Path]::GetFullPath((Join-Path $root '.artifacts/mac-export')).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if([IO.Path]::GetDirectoryName($macClient) -ine $macRoot -or [IO.Path]::GetFileName($macClient) -cnotmatch '^test-[a-f0-9]{32}$'){throw 'MacClientDirectory must be a dedicated test directory under this project.'}
    foreach($path in @($root,(Join-Path $root '.artifacts'),$macRoot,$macClient)) {
        if(-not (Test-Path -LiteralPath $path -PathType Container) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Missing or linked Mac input directory: $path"}
    }
    $contents=@(Get-ChildItem -LiteralPath $macClient -Force)
    if($contents.Count -ne 2 -or @($contents | Where-Object {$_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -cnotin @('NianDeskLink-macOS-arm64.zip','manifest.json')}).Count){throw 'Unexpected or linked Mac client payload.'}
    $python=(Get-Command python -ErrorAction Stop).Source
    & $python -B (Join-Path $root 'scripts/verify-mac-export-test.py') --directory $macClient --version $Version
    if($LASTEXITCODE -ne 0){throw 'Mac test bundle verification failed.'}
    $macHashes=@{}
    foreach($name in @('NianDeskLink-macOS-arm64.zip','manifest.json')) {$macHashes[$name]=(Get-FileHash -LiteralPath (Join-Path $macClient $name) -Algorithm SHA256).Hash}
}
$vendor=Join-Path $root 'vendor/deskflow'
$sourceMetadata=Join-Path $vendor 'metadata/original-manifest.json'
function Assert-Hash([string]$Path,[string]$Expected) {
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf) -or ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Missing or linked file: $Path"}
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $Expected.ToUpperInvariant()){throw "Hash mismatch: $Path"}
}
function Test-SafeRelative([string]$Name){
    if([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 240 -or $Name.Contains('\') -or $Name.Contains(':') -or $Name -match '[\x00-\x1f]' -or @($Name.Split('/') | Where-Object {$_ -eq '' -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ')}).Count){throw "Unsafe manifest path: $Name"}
}
function Assert-Runtime([string]$Directory,[string]$Manifest,[string]$ExpectedHash,[bool]$Original){
    Assert-Hash $Manifest $ExpectedHash
    $runtimeManifest=Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    $known=@{}; foreach($file in $runtimeManifest.files){
        $rel=[string]$file.path; Test-SafeRelative $rel
        if($known.ContainsKey($rel) -or ($rel -match '(?i)(^|/)(profile|\.git)(/|$)|\.(?:key|pem|p12|pfx|db)$') -or (!$Original -and $rel -match '(?i)^settings/')){throw "Unsafe or duplicate runtime item: $rel"}
        if($Original -and $rel -match '(?i)^settings/' -and $rel -cne 'settings/Deskflow.conf'){throw 'Unexpected original settings file.'}
        $known[$rel]=$true; $full=Join-Path $Directory $rel
        for($cursor=$full; $cursor -and $cursor.StartsWith($Directory,[StringComparison]::OrdinalIgnoreCase); $cursor=[IO.Path]::GetDirectoryName($cursor)) {
            if(-not (Test-Path -LiteralPath $cursor) -or (Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Missing or linked runtime path: $cursor"}
        }
        Assert-Hash $full ([string]$file.sha256)
    }
    $all=@(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if(@($all | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count){throw 'Linked runtime content refused.'}
    $internalManifest=if($Original){'package-manifest.json'}else{'toolbox-package-manifest.json'}
    foreach($file in @($all | Where-Object {!$_.PSIsContainer})){
        $rel=[IO.Path]::GetRelativePath($Directory,$file.FullName).Replace('\','/')
        if($rel -ne $internalManifest -and -not $known.ContainsKey($rel)){throw "Unexpected runtime file: $rel"}
    }
    Assert-Hash (Join-Path $Directory $internalManifest) $ExpectedHash
}
Assert-Hash $sourceMetadata '2C5B9EC650DC9F8EB4144F92D3BD25D7BF9356735F2E8A9223131A38A4765E7F'
Assert-Hash (Join-Path $vendor 'metadata/integrated-provenance.json') 'E79189268D860DFAC94184468C7E785DE357EF36795DC7CB32C10420E0BA21A5'
Assert-Hash (Join-Path $vendor 'metadata/0001-current-user-native-control.patch') '4553424FCF97B368C4356D647D5ED25B7E529DABA1B00FE4CEBD6A8F047E5324'
$original=Join-Path $vendor 'original'; $integrated=Join-Path $vendor 'integrated'
Assert-Runtime $original $sourceMetadata '2C5B9EC650DC9F8EB4144F92D3BD25D7BF9356735F2E8A9223131A38A4765E7F' $true
Assert-Runtime $integrated (Join-Path $integrated 'toolbox-package-manifest.json') '2EF19504292F5408D6C5ADC7E405EED4B79BE64D7C8354916E0B3C057ADF378C' $false
$publish=Join-Path $release 'app'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$project=Join-Path $root 'src/NianLink.Windows/NianLink.Windows.csproj'
Push-Location -LiteralPath $root
try { & $Dotnet publish $project -c Release -r win-x64 --self-contained true --output $publish --nologo --packages $PackagesDirectory -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" }
finally { Pop-Location }
if($LASTEXITCODE -ne 0){throw 'Windows publish failed; partial output retained for inspection.'}
foreach($file in @('NianLink.exe','NianLink.dll','DisplayBridge.exe','DisplayBridge.dll','DisplayBridge.runtimeconfig.json','CollaborationUi/index.html','CollaborationUi/mini.js','CollaborationUi/mini.css','CollaborationUi/mini-windows.css','CollaborationUi/assets/lucide.min.js')) {
    if(-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)){throw "Published component missing: $file"}
}
if($macClient) {
    $macDestination=Join-Path $publish 'MacClient'
    if(Test-Path -LiteralPath $macDestination){throw 'Unexpected pre-existing Mac client output.'}
    [void][IO.Directory]::CreateDirectory($macDestination)
    foreach($name in @('NianDeskLink-macOS-arm64.zip','manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $macClient $name) -Destination (Join-Path $macDestination $name)
        Assert-Hash (Join-Path $macDestination $name) $macHashes[$name]
    }
    & $python -B (Join-Path $root 'scripts/verify-mac-export-test.py') --directory $macDestination --version $Version
    if($LASTEXITCODE -ne 0){throw 'Copied Mac test bundle verification failed.'}
}
Copy-Item -LiteralPath $original -Destination (Join-Path $publish 'Deskflow') -Recurse
Copy-Item -LiteralPath $integrated -Destination (Join-Path $publish 'DeskflowIntegrated') -Recurse
$notices=Join-Path $publish 'THIRD-PARTY-NOTICES'; New-Item -ItemType Directory -Path $notices | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'vendor/notices') -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $notices}
Copy-Item -LiteralPath (Join-Path $vendor 'metadata/integrated-provenance.json') -Destination $notices
Copy-Item -LiteralPath (Join-Path $vendor 'metadata/0001-current-user-native-control.patch') -Destination $notices
Set-Content -LiteralPath (Join-Path $publish '测试版说明.txt') -Encoding utf8 -Value '本安装包仅供隔离测试，不代表开源许可合规或正式发布。本测试包不包含 Mac 安装包，Mac 导出入口暂不可用。首次配对需本人核对设备指纹，Mac 授权由用户自行操作。'
$files=@(Get-ChildItem -LiteralPath $publish -File -Recurse | Sort-Object FullName | ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($publish,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
$manifest=[ordered]@{version=$Version;target='win-x64';selfContained=$true;testingOnly=$true;sourceRebuildVerified=$false;macClientIncluded=[bool]$macClient;sdk=$sdk;files=$files}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publish 'release-manifest.json') -Encoding utf8
& $Compiler /Q "/DAppVersion=$Version" "/DPublishDir=$publish" "/DReleaseDir=$release" (Join-Path $root 'installer/NianLink.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed; partial output retained for inspection.'}
$setup=Join-Path $release "NianLink-$Version-win-x64-TEST-Setup.exe"
if(-not (Test-Path -LiteralPath $setup -PathType Leaf)){throw 'Installer output missing.'}
[ordered]@{version=$Version;file=[IO.Path]::GetFileName($setup);bytes=(Get-Item -LiteralPath $setup).Length;sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash;testingOnly=$true;installedDuringBuild=$false;sourceRebuildVerified=$false;macClientIncluded=[bool]$macClient} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'release-summary.json') -Encoding utf8
Write-Output "Test installer ready: $setup"
