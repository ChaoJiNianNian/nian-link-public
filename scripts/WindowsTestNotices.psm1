#requires -Version 7.0
Set-StrictMode -Version Latest

$script:WindowsTestNoticeRows = @(
    @('vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT', 'dotnet-core-10.0.15/LICENSE.TXT', 'D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B'),
    @('vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/THIRD-PARTY-NOTICES.TXT', 'dotnet-core-10.0.15/THIRD-PARTY-NOTICES.TXT', '6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21'),
    @('vendor/notices/dotnet-runtime-10.0.15/microsoft.windowsdesktop.app.runtime.win-x64/LICENSE.txt', 'dotnet-windowsdesktop-10.0.15/LICENSE.txt', 'A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB'),
    @('vendor/notices/webview2-license.txt', 'webview2-sdk-1.0.4258.31/LICENSE.txt', '0AF8F1B807512AAE39C2AC1AA4D0CAE65CABECB6FD554B8439A5162A0D6ECA55'),
    @('vendor/notices/webview2-notice.txt', 'webview2-sdk-1.0.4258.31/NOTICE.txt', '106423785C5B7EBA0A8E61D1837F2132E9C828E20AD530F565D981C1DF60DD90'),
    @('vendor/notices/lucide-1.8.0.txt', 'lucide-1.8.0/LICENSE.txt', 'B495047BD93A9B06913511076F504DABA17D5BBEB3E0650F3BB53A4220329C57'),
    @('vendor/deskflow/metadata/licenses/Deskflow-LICENSE.txt', 'deskflow/LICENSE.txt', 'D8C320FFC0030D1B096AE4732B50D2B811CF95E9A9B7377C1127B2563E0A0388'),
    @('vendor/deskflow/metadata/licenses/LicenseRef-OpenSSL-Exception.txt', 'deskflow/LicenseRef-OpenSSL-Exception.txt', 'EE89F0560EE2E5CA5DEE782611EC18E9BD472D1372B0631DD0537023AE9BF3A9')
)
$script:WindowsTestNoticeReadme = "Internal test package: PARTIAL third-party notices only.`nThese files are not a complete license inventory or release approval.`nDeskflow, Qt, OpenSSL, Windows SDK, runtimes and other components still require review.`nDeskflow's OpenSSL exception applies only to files that explicitly declare the exception; it does not apply to the whole project or other components.`nLucide source content is checked against the reviewed 1.8.0 package with CRLF normalized to LF; see project audit for verification limits.`n"

function Assert-TestNoticeSafePath([string]$Path, [bool]$Leaf) {
    if (-not (Test-Path -LiteralPath $Path -PathType $(if ($Leaf) { 'Leaf' } else { 'Container' }))) { throw 'Required notice input or directory missing.' }
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked notice path refused.' }
    }
}

function Add-WindowsTestNotices {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ProjectRoot, [Parameter(Mandatory)][string]$Bundle)
    $project = [IO.Path]::GetFullPath($ProjectRoot)
    $bundleRoot = [IO.Path]::GetFullPath($Bundle)
    Assert-TestNoticeSafePath $project $false
    Assert-TestNoticeSafePath $bundleRoot $false
    $noticeRoot = Join-Path $bundleRoot 'THIRD-PARTY-NOTICES'
    if (Test-Path -LiteralPath $noticeRoot) { throw 'Existing notice output refused.' }
    $rows = $script:WindowsTestNoticeRows
    foreach ($row in $rows) {
        $source = Join-Path $project $row[0]
        Assert-TestNoticeSafePath $source $true
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $row[2]) { throw 'Notice source does not match the reviewed version.' }
    }
    $core = Join-Path $bundleRoot 'Keyboard/deskflow-core.exe'
    Assert-TestNoticeSafePath $core $true
    if ((Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -cne '04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA') {
        throw 'Deskflow test core does not match the reviewed sample.'
    }
    $app = Join-Path $bundleRoot 'App'
    Assert-TestNoticeSafePath $app $false
    $requiredFrameworks = @(
        @('NianLink.DiscoveryPortable.runtimeconfig.json', @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')),
        @('DisplayBridge.runtimeconfig.json', @('Microsoft.NETCore.App'))
    )
    foreach ($entry in $requiredFrameworks) {
        $path = Join-Path $app $entry[0]
        Assert-TestNoticeSafePath $path $true
        $configuration = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop
        foreach ($name in $entry[1]) {
            $matches = @($configuration.runtimeOptions.includedFrameworks | Where-Object { $_.name -ceq $name })
            if ($matches.Count -ne 1 -or $matches[0].version -cne '10.0.15') { throw 'Runtime version does not match the reviewed notices.' }
        }
    }
    $depsPath = Join-Path $app 'NianLink.DiscoveryPortable.deps.json'
    Assert-TestNoticeSafePath $depsPath $true
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable -ErrorAction Stop
    $webview = @($deps.libraries.Keys | Where-Object { $_ -cmatch '^Microsoft\.Web\.WebView2(?:\.[^/]+)?/' })
    if (-not ($webview -ccontains 'Microsoft.Web.WebView2/1.0.4258.31') -or @($webview | Where-Object { -not $_.EndsWith('/1.0.4258.31', [StringComparison]::Ordinal) }).Count) { throw 'WebView2 version does not match the reviewed notices.' }
    $lucideSource = Join-Path $project 'src/DesktopToolbox.Collaboration.Ui/assets/lucide.min.js'
    $lucidePayload = Join-Path $app 'CollaborationUi/assets/lucide.min.js'
    Assert-TestNoticeSafePath $lucideSource $true
    Assert-TestNoticeSafePath $lucidePayload $true
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $lucideText = $utf8.GetString([IO.File]::ReadAllBytes($lucideSource))
    $lucideHeader = $lucideText.Substring(0, [Math]::Min(256, $lucideText.Length))
    $normalizedLucide = $utf8.GetBytes($lucideText.Replace("`r`n", "`n"))
    $lucideHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalizedLucide))
    if ($lucideHeader -cnotmatch 'lucide v1\.8\.0 - ISC' -or $lucideHash -cne 'AB85225ECC1DAA2033607856F03DD2DABCBC08E2D260A5B97E17C690AA929766' -or (Get-FileHash -LiteralPath $lucideSource -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $lucidePayload -Algorithm SHA256).Hash) { throw 'Lucide payload differs from the reviewed source.' }
    [void][IO.Directory]::CreateDirectory($noticeRoot)
    foreach ($row in $rows) {
        $destination = Join-Path $noticeRoot $row[1]
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath (Join-Path $project $row[0]) -Destination $destination
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne $row[2]) { throw 'Copied notice differs from its verified source.' }
    }
    [IO.File]::WriteAllText((Join-Path $noticeRoot 'README.txt'), $script:WindowsTestNoticeReadme, [Text.UTF8Encoding]::new($true))
}

function Assert-WindowsTestNoticesArchive([System.Collections.IDictionary]$Entries) {
    $allowed = @{}
    foreach ($row in $script:WindowsTestNoticeRows) {
        $name = 'THIRD-PARTY-NOTICES/' + $row[1]
        $allowed[$name] = $true
        if (-not $Entries.Contains($name)) { throw 'Reviewed notice missing from archive.' }
        $stream = $Entries[$name].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($hash -cne $row[2]) { throw 'Archived notice differs from reviewed source.' }
    }
    $readmeName = 'THIRD-PARTY-NOTICES/README.txt'
    $allowed[$readmeName] = $true
    if (-not $Entries.Contains($readmeName)) { throw 'Notice scope statement missing from archive.' }
    $encoding = [Text.UTF8Encoding]::new($true)
    [byte[]]$readmeBytes = $encoding.GetPreamble() + $encoding.GetBytes($script:WindowsTestNoticeReadme)
    $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($readmeBytes))
    $stream = $Entries[$readmeName].Open()
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    if ($hash -cne $expectedHash) { throw 'Archived notice scope statement differs.' }
    foreach ($name in $Entries.Keys) {
        if ([string]$name -like 'THIRD-PARTY-NOTICES/*' -and -not $allowed.ContainsKey([string]$name)) {
            throw 'Unexpected notice in test archive.'
        }
    }
}

Export-ModuleMember -Function Add-WindowsTestNotices, Assert-WindowsTestNoticesArchive
