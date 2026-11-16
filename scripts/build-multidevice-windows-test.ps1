#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$PackagesDirectory,
    [string]$MacClientDirectory,
    [string]$MacVersion,
    [switch]$DiscoveryPortable,
    [switch]$FilelessTesting,
    [switch]$PairingDiagnostics,
    [switch]$BetaPortable,
    [switch]$ManagedRetention,
    [Parameter(Mandatory)][switch]$TestingOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$useManagedRetention = if ($PSBoundParameters.ContainsKey('ManagedRetention')) { [bool]$ManagedRetention } else { [bool]$DiscoveryPortable }
if (-not $TestingOnly) { throw 'This builds an isolated internal test only.' }
if ($FilelessTesting -and -not $DiscoveryPortable) { throw 'Fileless testing requires the discovery portable candidate.' }
if ($PairingDiagnostics -and -not $FilelessTesting) { throw 'Pairing diagnostics requires an isolated fileless test.' }
if ($BetaPortable -and (-not $FilelessTesting -or $PairingDiagnostics)) { throw 'Beta portable requires the non-diagnostic fileless test candidate.' }
if ($useManagedRetention -and -not $DiscoveryPortable) { throw 'Managed retention is available only for discovery test packages.' }
if ($DiscoveryPortable -and (-not [string]::IsNullOrWhiteSpace($MacClientDirectory) -or -not [string]::IsNullOrWhiteSpace($MacVersion))) { throw 'Discovery portable is Windows-only.' }
$useIsolatedArtifacts = [bool]($FilelessTesting -or ($useManagedRetention -and $DiscoveryPortable))
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$engineRoot = Join-Path $root 'tests/.work/multidevice-native-runtime-01'
$engine = Join-Path $engineRoot 'deskflow-core.exe'
$engineHash = '04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA'
foreach ($path in @($root, $engineRoot, $engine)) {
    if (-not (Test-Path -LiteralPath $path) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Missing or linked input: $path" }
}
if ((Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash -cne $engineHash) { throw 'Candidate keyboard core hash mismatch.' }
Import-Module (Join-Path $PSScriptRoot 'WindowsTestKeyboardPayload.psm1') -ErrorAction Stop
Assert-WindowsTestEngineInputs -Directory $engineRoot
$Dotnet = [IO.Path]::GetFullPath($Dotnet)
$PackagesDirectory = [IO.Path]::GetFullPath($PackagesDirectory)
if (-not (Test-Path -LiteralPath $Dotnet -PathType Leaf) -or -not (Test-Path -LiteralPath $PackagesDirectory -PathType Container)) { throw 'SDK or package cache missing.' }
Import-Module (Join-Path $PSScriptRoot 'SdkBaseline.psm1')
Assert-NianSdk -Dotnet $Dotnet -ProjectRoot $root
Import-Module (Join-Path $PSScriptRoot 'BuildArtifactLock.psm1')
if ($useManagedRetention) {
    Import-Module (Join-Path $PSScriptRoot 'DiscoveryManagedRetention.psm1') -ErrorAction Stop
    $lock = Enter-NianManagedDiscoveryLock -ProjectRoot $root
} else {
    $lock = Enter-ToolboxBuildLock -ProjectRoot $root
}
$cacheLease = $null
try {
    if ($useIsolatedArtifacts) {
        Import-Module (Join-Path $PSScriptRoot 'FilelessArtifactCache.psm1') -ErrorAction Stop
        $discoveryBuildProperties = @(Get-NianDiscoveryBuildProperties -FilelessTesting ([bool]$FilelessTesting) -PairingDiagnostics ([bool]$PairingDiagnostics))
        $cacheLease = Enter-NianFilelessCacheLease -ProjectRoot $root
        $isolatedArtifacts = Get-NianFilelessArtifactCache -ProjectRoot $root -Platform 'windows' -PairingDiagnostics ([bool]$PairingDiagnostics) -DiscoveryOnly ([bool](-not $FilelessTesting)) -Lease $cacheLease
    }
    $macClient = $null
    $macHashes = @{}
    if (-not [string]::IsNullOrWhiteSpace($MacClientDirectory) -or -not [string]::IsNullOrWhiteSpace($MacVersion)) {
        if ([string]::IsNullOrWhiteSpace($MacClientDirectory) -or $MacVersion -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Both a Mac test bundle and canonical version are required.' }
        $macClient = [IO.Path]::GetFullPath($MacClientDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
        $macRoot = [IO.Path]::GetFullPath((Join-Path $root '.artifacts/mac-export'))
        if ([IO.Path]::GetDirectoryName($macClient) -ine $macRoot -or [IO.Path]::GetFileName($macClient) -cnotmatch '^test-[a-f0-9]{32}$') { throw 'Mac bundle must be a dedicated project test directory.' }
        foreach ($path in @($macRoot, $macClient)) {
            if (-not (Test-Path -LiteralPath $path -PathType Container) -or ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Mac bundle directory missing or linked.' }
        }
        $items = @(Get-ChildItem -LiteralPath $macClient -Force)
        if ($items.Count -ne 2 -or @($items | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $_.Name -cnotin @('NianDeskLink-macOS-arm64.zip','manifest.json') }).Count) { throw 'Unexpected Mac bundle files.' }
        $python = (Get-Command python -ErrorAction Stop).Source
        & $python -B (Join-Path $root 'scripts/verify-mac-export-test.py') --directory $macClient --version $MacVersion --multi-device-testing
        if ($LASTEXITCODE -ne 0) { throw 'Mac multi-device test bundle verification failed.' }
        foreach ($name in @('NianDeskLink-macOS-arm64.zip','manifest.json')) { $macHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $macClient $name) -Algorithm SHA256).Hash }
    }
    $runId = [guid]::NewGuid().ToString('N')
    $managedMode = if (-not $useManagedRetention) { $null } elseif ($PairingDiagnostics) { 'diagnostics' } elseif ($FilelessTesting) { 'fileless' } else { 'discovery' }
    $outputRoot = Join-Path $root $(if ($useManagedRetention) { '.artifacts/discovery-portable-managed/' + $managedMode } elseif ($DiscoveryPortable) { '.artifacts/discovery-portable' } else { '.artifacts/multidevice-windows' })
    if ((Test-Path -LiteralPath $outputRoot) -and ((Get-Item -LiteralPath $outputRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked output refused.' }
    $output = Join-Path $outputRoot ('test-' + $runId)
    if ($DiscoveryPortable) {
        Import-Module (Join-Path $PSScriptRoot 'DiscoveryPortablePaths.psm1')
        $dataRoot = 'D:\Projects'
        $profile = Join-Path $dataRoot ('nian-link/test-profiles/discovery-portable/test-' + $runId)
        $discoveryPathParameters = @{ ProjectRoot=$root; OutputRoot=$outputRoot; Output=$output; DataRoot=$dataRoot; Profile=$profile }
        if ($useManagedRetention) { $discoveryPathParameters.ManagedMode = $managedMode }
        Assert-DiscoveryBuildPaths @discoveryPathParameters
    }
    $bundleName = if ($FilelessTesting) { '年年互联-免文件便携测试' } elseif ($DiscoveryPortable) { '年年互联-发现便携测试' } else { '年年互联-多设备测试' }
    $bundle = Join-Path $output $bundleName
    $app = Join-Path $bundle 'App'
    $keyboard = Join-Path $bundle 'Keyboard'
    function Get-DiscoverySourceRows {
        $sourceRows = @()
        foreach ($sourceDir in @('src/NianLink.CandidateDiscovery','src/NianLink.MultiDevice','src/NianLink.DiscoveryPortable','src/DesktopToolbox.DeskLink','src/NianLink.MultiDevice.Shared')) {
            $sourcePath = Join-Path $root $sourceDir
            $items = @(Get-ChildItem -LiteralPath $sourcePath -Force -Recurse | Where-Object { $_.FullName -notmatch '(?i)[\\/](bin|obj)[\\/]' })
            if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Linked source refused.' }
            foreach ($item in @($items | Where-Object { -not $_.PSIsContainer -and $_.Extension -in @('.cs','.csproj','.xaml') } | Sort-Object FullName)) {
                $sourceRows += [ordered]@{ path=[IO.Path]::GetRelativePath($root,$item.FullName).Replace('\','/'); bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
            }
        }
        return ,$sourceRows
    }
    $sourceBefore = if ($DiscoveryPortable) { Get-DiscoverySourceRows } else { @() }
    if ($DiscoveryPortable) {
        if ($useManagedRetention) {
            $reserved = Start-NianManagedDiscovery -ProjectRoot $root -Mode $managedMode -RunId $runId -Lock $lock
            if ([IO.Path]::GetFullPath($reserved) -ine [IO.Path]::GetFullPath($output)) { throw 'Managed reservation path differs.' }
        }
        [void][IO.Directory]::CreateDirectory($outputRoot)
        Assert-DiscoveryBuildPaths @discoveryPathParameters
        [void](New-Item -ItemType Directory -Path $output -ErrorAction Stop)
    }
    [void][IO.Directory]::CreateDirectory($app)
    [void][IO.Directory]::CreateDirectory($keyboard)
    $project = if ($DiscoveryPortable) { 'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj' } else { 'src/NianLink.MultiDevice/NianLink.MultiDevice.csproj' }
    $publishArgs = @('publish', (Join-Path $root $project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--output', $app, '--nologo', '--packages', $PackagesDirectory, '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($DiscoveryPortable) { $publishArgs += @('--source', $PackagesDirectory, '-p:NuGetAudit=false') }
    if ($useIsolatedArtifacts) {
        $bridgeProject = Join-Path $root 'src/DesktopToolbox.DisplayBridge/DesktopToolbox.DisplayBridge.csproj'
        $bridgeParameters = @('-nologo', '-getProperty:TargetDir', '-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=true', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$isolatedArtifacts") + $discoveryBuildProperties
        Push-Location -LiteralPath $root
        try { $builtBridge = (& $Dotnet msbuild $bridgeProject @bridgeParameters | Out-String).Trim() }
        finally { Pop-Location }
        if ($LASTEXITCODE -ne 0 -or -not [IO.Path]::IsPathFullyQualified($builtBridge) -or -not [IO.Path]::GetFullPath($builtBridge).StartsWith([IO.Path]::GetFullPath($isolatedArtifacts) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Could not resolve the isolated DisplayBridge output.' }
        $publishArgs += @('-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$isolatedArtifacts", "-p:NianDisplayBridgeOutputDirectory=$builtBridge") + $discoveryBuildProperties
    }
    Push-Location -LiteralPath $root
    try {
        if ($useIsolatedArtifacts) { Invoke-NianFilelessPublish -Dotnet $Dotnet -PublishArgs $publishArgs -Lease $cacheLease }
        else { & $Dotnet @publishArgs }
    }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Candidate publish failed; partial output retained.' }
    if ($FilelessTesting) {
        Import-Module (Join-Path $PSScriptRoot 'FilelessBuildMetadata.psm1')
        foreach ($check in @(@('NianLink.DiscoveryPortable.dll','NianFilelessPairingCandidate'), @('DesktopToolbox.DeskLink.dll','NianFilelessPairingIsolatedTest'))) {
            Assert-NianBuildMetadata -Assembly (Join-Path $app $check[0]) -Key $check[1] -Value 'true'
        }
        if ($PairingDiagnostics) {
            foreach ($assembly in @('NianLink.DiscoveryPortable.dll','NianLink.CandidateDiscovery.dll')) {
                Assert-NianBuildMetadata -Assembly (Join-Path $app $assembly) -Key 'NianPairingDiagnostics' -Value 'true'
            }
        }
    }
    if ($macClient) {
        $macDestination = Join-Path $app 'MacClient'
        if (Test-Path -LiteralPath $macDestination) { throw 'Unexpected existing Mac client directory.' }
        [void][IO.Directory]::CreateDirectory($macDestination)
        foreach ($name in @('NianDeskLink-macOS-arm64.zip','manifest.json')) {
            $destination = Join-Path $macDestination $name
            Copy-Item -LiteralPath (Join-Path $macClient $name) -Destination $destination
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $macHashes[$name]) { throw 'Copied Mac bundle hash mismatch.' }
        }
        & $python -B (Join-Path $root 'scripts/verify-mac-export-test.py') --directory $macDestination --version $MacVersion --multi-device-testing
        if ($LASTEXITCODE -ne 0) { throw 'Copied Mac multi-device bundle verification failed.' }
    }
    if (-not $useIsolatedArtifacts) { $builtBridge = Join-Path $root 'src/DesktopToolbox.DisplayBridge/bin/Release/net10.0-windows10.0.19041.0/win-x64' }
    foreach ($name in @('DisplayBridge.exe','DisplayBridge.dll','DisplayBridge.deps.json','DisplayBridge.runtimeconfig.json')) {
        $source = Join-Path $builtBridge $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Display bridge missing: $name" }
        if ($useIsolatedArtifacts) {
            for ($cursor = $source; $cursor -and $cursor.Length -ge $isolatedArtifacts.Length; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
                if (-not (Test-Path -LiteralPath $cursor) -or ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Missing or linked isolated DisplayBridge output.' }
                if ($cursor -ieq $isolatedArtifacts) { break }
            }
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $app $name) -Force
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $app $name) -Algorithm SHA256).Hash) { throw "Display bridge copy hash mismatch: $name" }
    }
    Copy-WindowsTestEngineInputs -Source $engineRoot -Destination $keyboard
    if ((Get-FileHash -LiteralPath (Join-Path $keyboard 'deskflow-core.exe') -Algorithm SHA256).Hash -cne $engineHash) { throw 'Copied core hash mismatch.' }
    # Deskflow's Windows portable-mode marker prevents it from reading or writing
    # the installed application's shared settings. The session also requires it.
    $markerSource = Join-Path $root 'vendor/deskflow/original/settings/Deskflow.conf'
    $markerHash = '36A9E7F1C95B82FFB99743E0C5C4CE95D83C9A430AAC59F84EF3CBFAB6145068'
    if (-not (Test-Path -LiteralPath $markerSource -PathType Leaf) -or ((Get-Item -LiteralPath $markerSource -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $markerSource -Algorithm SHA256).Hash -cne $markerHash) { throw 'Portable settings marker is missing or changed.' }
    $settingsDirectory = Join-Path $keyboard 'settings'
    [void][IO.Directory]::CreateDirectory($settingsDirectory)
    Copy-Item -LiteralPath $markerSource -Destination (Join-Path $settingsDirectory 'Deskflow.conf')
    if ($DiscoveryPortable) {
        $packageId = Join-Path $app 'discovery-portable-id.txt'
        if (Test-Path -LiteralPath $packageId) { throw 'Unexpected preexisting package identity.' }
        [IO.File]::WriteAllText($packageId, $runId, [Text.Encoding]::ASCII)
        foreach ($choice in @(@('笔记本-D盘启动.cmd','D'), @('台式机-H盘启动.cmd','H'))) {
            $launcher = '@echo off' + "`r`n" + 'start "" "%~dp0App\NianLink.DiscoveryPortable.exe" "' + $choice[1] + ':\Projects\nian-link\test-profiles\discovery-portable\test-' + $runId + '" "%~dp0Keyboard\deskflow-core.exe" --discovery-portable' + "`r`n"
            [IO.File]::WriteAllText((Join-Path $bundle $choice[0]), $launcher, [Text.Encoding]::ASCII)
        }
        if ($BetaPortable) {
            $betaLauncher = '@echo off' + "`r`n" + 'start "" "%~dp0App\NianLink.DiscoveryPortable.exe" --beta-portable' + "`r`n"
            [IO.File]::WriteAllText((Join-Path $bundle 'Start-NianLink-Beta.cmd'), $betaLauncher, [Text.Encoding]::ASCII)
        }
    } else {
        $launcher = '@echo off' + "`r`n" + 'start "" "%~dp0App\NianLink.MultiDevice.exe" "%LOCALAPPDATA%\NianLinkMultiDeviceCandidate" "%~dp0Keyboard\deskflow-core.exe"' + "`r`n"
        [IO.File]::WriteAllText((Join-Path $bundle '启动多设备测试.cmd'), $launcher, [Text.Encoding]::ASCII)
    }
    $readme = @'
年年互联多设备隔离测试版（仅供本人测试）
双击“启动多设备测试.cmd”。资料保存在本机独立测试目录，不覆盖正式版。
真实键鼠测试前请先暂停旧版共享，避免两个键鼠核心争用；测试后可重新启用旧版。
目前共用显示器自动切屏尚未接入；请勿将此候选作为正式产品分发。
配对文件含凭证，只发给对应的本人设备，不要公开或发到聊天。
'@
    if ($DiscoveryPortable) {
        $readme = $readme.Replace('年年互联多设备隔离测试版', '年年互联发现便携测试版').Replace('双击“启动多设备测试.cmd”。资料保存在本机独立测试目录，不覆盖正式版。', '笔记本双击“笔记本-D盘启动.cmd”，台式机双击“台式机-H盘启动.cmd”。资料只保存在所选盘的专用目录，不覆盖正式版。')
        $readme += "`r`n所选 D 盘或 H 盘须是本机已就绪的固定卷，网络盘、虚拟映射或与 C/F 同卷时拒绝；资料在所选盘的 Projects\nian-link\test-profiles\discovery-portable\test-$runId。`r`n"
    }
    if ($FilelessTesting) { $readme = @"
年年互联免文件配对便携测试版（仅供本人测试）
笔记本使用“笔记本-D盘启动.cmd”，台式机使用“台式机-H盘启动.cmd”。两端资料保存在各自所选固定盘的独立测试目录；请先暂停其他键鼠共享。
请先阅读“使用说明.txt”，按其中的发现、双方指纹核对和免文件配对步骤操作。发现设备不代表配对成功，配对后仍需开始连接。
配对中断或显示结果待确认时，查看“未完成的配对”，保留测试资料并按说明处理；不要自行猜测成功或失败。
本候选尚待跨设备及中断恢复验证，不能作为正式版使用。Mac 需使用单独提供的免文件测试 App。
本次 Windows 测试资料目录：所选盘的 Projects\nian-link\test-profiles\discovery-portable\test-$runId。
"@ }
    if ($BetaPortable) { $readme = @'
年年互联多设备测试版（内部候选）
请双击 Start-NianLink-Beta.cmd。测试资料保存在本机用户的独立 beta 资料目录；不会迁移旧配对。不同构建的资料相互独立，可能需要重新配对。
本包需要 WebView2 Runtime 和与键鼠核心匹配的 Visual C++ 运行库；安装及真实设备验证、第三方许可仍待完成，不能作为正式版。
使用前请先阅读“使用说明.txt”，并正常停止其他键鼠共享程序。测试中断或归还状态不明确时，请保留资料并使用设备本地键鼠检查。
'@ }
    [IO.File]::WriteAllText((Join-Path $bundle '测试说明.txt'), $readme, [Text.UTF8Encoding]::new($true))
    $guidePath = Join-Path $root $(if ($BetaPortable) { 'docs/Windows多设备Beta使用说明.md' } elseif ($FilelessTesting) { 'docs/免文件配对内测使用说明.md' } else { 'docs/USER-GUIDE.md' })
    if (-not (Test-Path -LiteralPath $guidePath -PathType Leaf) -or ((Get-Item -LiteralPath $guidePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'User guide is missing or linked.' }
    [IO.File]::WriteAllText((Join-Path $bundle '使用说明.txt'), [IO.File]::ReadAllText($guidePath, [Text.Encoding]::UTF8), [Text.UTF8Encoding]::new($true))
    if ($FilelessTesting) {
        Import-Module (Join-Path $PSScriptRoot 'WindowsTestNotices.psm1') -ErrorAction Stop
        Add-WindowsTestNotices -ProjectRoot $root -Bundle $bundle
    }
    Assert-WindowsTestEngineInputs -Directory $keyboard
    $archiveName = if ($PairingDiagnostics) { 'NianLink-fileless-diagnostics-windows-internal-test.zip' } elseif ($FilelessTesting) { 'NianLink-fileless-portable-windows-internal-test.zip' } elseif ($DiscoveryPortable) { 'NianLink-discovery-portable-windows-internal-test.zip' } else { 'NianLink-multidevice-windows-internal-test.zip' }
    $archive = Join-Path $output $archiveName
    Compress-Archive -LiteralPath $bundle -DestinationPath $archive -CompressionLevel Optimal
    $rows = Get-ChildItem -LiteralPath $bundle -File -Recurse | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $prefix = [IO.Path]::GetFileName($bundle) + '/'
        $entries = @{}
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected archive path.' }
            $relative = $entry.FullName.Substring($prefix.Length)
            if ($entries.ContainsKey($relative) -or $relative -match '(^|/)\.\.?(/|$)' -or $relative -match '(?i)\.(pem|key|pfx|p12)$') { throw 'Unsafe or duplicate archive item.' }
            $entries[$relative] = $entry
        }
        if ($entries.Count -ne @($rows).Count) { throw 'Archive file count differs from staged files.' }
        foreach ($row in $rows) {
            $entry = $entries[$row.path]
            if ($null -eq $entry -or $entry.Length -ne $row.bytes) { throw "Archive item missing or size mismatch: $($row.path)" }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($hash -cne $row.sha256) { throw "Archive item hash mismatch: $($row.path)" }
        }
        Assert-WindowsTestEngineArchive -Entries $entries
        if ($FilelessTesting) { Assert-WindowsTestNoticesArchive -Entries $entries }
    } finally { $zip.Dispose() }
    $manifest = [ordered]@{testingOnly=$true; realDeviceVerified=$false; sourceRebuildVerified=$false; signed=$false; macClientIncluded=[bool]$macClient; keyboardCoreSha256=$engineHash; archiveSha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash; files=@($rows)}
    if ($DiscoveryPortable) {
        $sourceAfter = Get-DiscoverySourceRows
        if (($sourceBefore | ConvertTo-Json -Depth 5 -Compress) -cne ($sourceAfter | ConvertTo-Json -Depth 5 -Compress)) { throw 'Discovery source changed during candidate build.' }
        $manifest['channel'] = 'discovery-portable-v1'
        $manifest['runId'] = $runId
        $manifest['sources'] = @($sourceAfter)
        if ($FilelessTesting) { $manifest['filelessIsolatedTest'] = $true }
        if ($PairingDiagnostics) { $manifest['pairingDiagnostics'] = $true }
        if ($BetaPortable) { $manifest['betaPortable'] = $true }
    }
    $statusPath = Join-Path $output 'test-build-status.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statusPath -Encoding utf8
    if ($DiscoveryPortable) {
        $python = (Get-Command python -ErrorAction Stop).Source
        & $python -B (Join-Path $root 'scripts/discovery_portable_policy.py') --bundle $bundle --source $root --manifest $statusPath
        if ($LASTEXITCODE -ne 0) { throw 'Discovery portable inventory failed; retain partial output for inspection.' }
    }
    if ($useManagedRetention) { Complete-NianManagedDiscovery -ProjectRoot $root -Mode $managedMode -RunId $runId -Lock $lock }
    Write-Output "Internal Windows test package: $archive"
} finally {
    try { if ($null -ne $cacheLease) { Exit-NianFilelessCacheLease -Lease $cacheLease } }
    finally {
        if ($useManagedRetention) { Exit-NianManagedDiscoveryLock -Lock $lock }
        else { Exit-ToolboxBuildLock -Lock $lock }
    }
}
