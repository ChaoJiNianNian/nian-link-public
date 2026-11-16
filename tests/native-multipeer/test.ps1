param([string]$CacheRoot = 'D:\NianToolboxBuildCache\pc-toolbox-native')
$ErrorActionPreference = 'Stop'
$project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $project 'tests\.work\multi-native-source-01'
$build = Join-Path $project 'tests\.work\multi-native-harness-01'
Import-Module 'D:\Projects\pc-toolbox\scripts\BuildArtifactLock.psm1'
$lock = Enter-ToolboxBuildLock -ProjectRoot $project
try {
    . 'D:\Projects\pc-toolbox\scripts\native-deskflow-env.ps1' -CacheRoot $CacheRoot
    & cmake -S $PSScriptRoot -B $build -G Ninja -DCMAKE_CXX_COMPILER=cl -DCMAKE_BUILD_TYPE=Release "-DCMAKE_PREFIX_PATH=$CacheRoot\native-qt" "-DDESKFLOW_SOURCE=$source"
    if ($LASTEXITCODE -ne 0) { throw 'Harness configuration failed.' }
    & cmake --build $build --parallel 1
    if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $build 'multi-peer-transport-harness.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DESKTOOLBOX_CONTROL'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $endpoint = "nian-deskflow-control-$($process.Id)"
        if ($process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() -ne $endpoint) { throw 'Unexpected harness endpoint.' }
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $endpoint, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(3000)
            $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
            $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true)
            try {
                $writer.AutoFlush = $true
                function Invoke-TestRequest($request) {
                    $writer.WriteLine(($request | ConvertTo-Json -Compress))
                    $line = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
                    return ($line | ConvertFrom-Json)
                }
                $request = @{ version = 1; requestId = 'a' * 32; action = 'switch-if-current'; target = 'Peer'; expectedActive = 'Host'; expectedRevision = 9 }
                $response = Invoke-TestRequest $request
                if ($response.ok -or $response.error -ne 'focus_changed' -or $response.activeComputer -ne 'Host') { throw 'Wrong revision should fail closed.' }
                $request.expectedRevision = 0
                $request.expectedActive = 'Other'
                $response = Invoke-TestRequest $request
                if ($response.ok -or $response.error -ne 'focus_changed') { throw 'Wrong owner should fail closed.' }
                $request.expectedActive = 'Host'
                $response = Invoke-TestRequest $request
                if (-not $response.ok -or $response.activeComputer -ne 'Peer' -or $response.revision -ne 1) { throw 'Matching switch failed.' }
                $request.target = 'Host'
                $response = Invoke-TestRequest $request
                if ($response.ok -or $response.activeComputer -ne 'Peer') { throw 'Stale switch should fail closed.' }
                $request.expectedRevision = 1.5
                $response = Invoke-TestRequest $request
                if ($response.ok -or $response.error -ne 'invalid_expected_state') { throw 'Noninteger revision accepted.' }
                $response = Invoke-TestRequest @{ version = 1; requestId = 'b' * 32; action = 'state'; expectedRevision = 1 }
                if ($response.ok -or $response.error -ne 'unknown_field') { throw 'Unexpected field accepted.' }
                Write-Output 'PASS: conditional-switch native transport validates state and rejects stale requests. The callback is simulated; no physical input was tested.'
            }
            finally { $writer.Dispose(); $reader.Dispose() }
        }
        finally { $pipe.Dispose() }
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
finally { Exit-ToolboxBuildLock $lock }
