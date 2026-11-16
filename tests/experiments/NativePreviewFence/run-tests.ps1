param(
  [string]$Candidate,
  [string]$CacheRoot
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Candidate) -or [string]::IsNullOrWhiteSpace($CacheRoot)) {
  throw 'Explicit isolated research paths are required: Candidate and CacheRoot.'
}
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$work = [IO.Path]::GetFullPath((Join-Path $project 'tests\.work'))
function Assert-Isolated([string]$path) {
  $absolute = [IO.Path]::GetFullPath($path)
  $relative = [IO.Path]::GetRelativePath($work, $absolute)
  if ([IO.Path]::IsPathFullyQualified($relative) -or $relative -eq '..' -or $relative.StartsWith('..\') -or $relative -eq '.') { throw 'Path is outside isolated work directory.' }
  for ($current = $absolute; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ((Test-Path -LiteralPath $current) -and (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Linked research path is not allowed.' }
  }
  return $absolute
}
function Assert-NoReparseTree([string]$path) {
  $pending = [Collections.Generic.Stack[string]]::new()
  $pending.Push($path)
  while ($pending.Count -gt 0) {
    $current = $pending.Pop()
    foreach ($item in Get-ChildItem -LiteralPath $current -Force) {
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked research tree is not allowed.' }
      if ($item.PSIsContainer) { $pending.Push($item.FullName) }
    }
  }
}
$candidate = Assert-Isolated $Candidate
$source = Join-Path $candidate 'source\deskflow-081f6478e654a112168879e67a41ce7d7e4ae3af'
$coreBuild = Join-Path $candidate 'build-02'
foreach ($p in @($source, $coreBuild)) { $null = Assert-Isolated $p; if (-not (Test-Path -LiteralPath $p -PathType Container)) { throw "Isolated candidate missing: $p" } }
Assert-NoReparseTree $source
Assert-NoReparseTree $coreBuild
$run = Join-Path $project ('tests\.work\native-preview-fence-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run) | Out-Null
$msvc = Join-Path $CacheRoot 'native-msvc\VC\Tools\MSVC\14.44.35207'
$sdk = Join-Path $CacheRoot 'native-msvc\sdk-common\c'
$sdkLib = Join-Path $CacheRoot 'native-msvc\sdk-x64\c'
$qt = Join-Path $CacheRoot 'native-qt'
$env:PATH = "$msvc\bin\Hostx64\x64;$sdk\bin\10.0.26100.0\x64;$qt\bin;$env:PATH"
$env:INCLUDE = "$msvc\include;$sdk\Include\10.0.26100.0\ucrt;$sdk\Include\10.0.26100.0\shared;$sdk\Include\10.0.26100.0\um;$sdk\Include\10.0.26100.0\winrt"
$env:LIB = "$msvc\lib\x64;$sdkLib\ucrt\x64;$sdkLib\um\x64"
$env:TEMP = $run
$env:TMP = $run
& cmake -S $PSScriptRoot -B $run -G Ninja -DCMAKE_CXX_COMPILER=cl -DCMAKE_BUILD_TYPE=Release "-DCMAKE_PREFIX_PATH=$qt" "-DDESKFLOW_SOURCE=$source" "-DDESKFLOW_BUILD=$coreBuild"
if ($LASTEXITCODE -ne 0) { throw 'Fixture configure failed.' }
& cmake --build $run --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
foreach ($test in @(
  @{ Name='native-fence-unit.exe'; Label='unit'; Expected=0 },
  @{ Name='native-fence-queue.exe'; Label='queue'; Expected=0 },
  @{ Name='native-fence-async-queue.exe'; Label='async-true'; Argument='async-true'; Expected=23 },
  @{ Name='native-fence-async-queue.exe'; Label='async-unknown'; Argument='async-unknown'; Expected=0 },
  @{ Name='native-fence-async-queue.exe'; Label='sync'; Argument='sync'; Expected=0 }
)) {
  $name = $test.Name
  $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $run $name))
  if ($test.Argument) { $info.ArgumentList.Add($test.Argument) }
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $process = [Diagnostics.Process]::Start($info)
  try {
    if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit(); throw "$name timed out." }
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    [IO.File]::WriteAllText((Join-Path $run "$($test.Label).stdout.txt"), $stdout)
    [IO.File]::WriteAllText((Join-Path $run "$($test.Label).stderr.txt"), $stderr)
    Write-Output $stdout
    if ($process.ExitCode -ne $test.Expected) { throw "$($test.Label) returned $($process.ExitCode), expected $($test.Expected)." }
    if ($test.Label -eq 'async-true' -and -not $stdout.Contains('RED synthetic contract misuse:')) { throw 'The expected red counterexample was not reported.' }
  } finally { $process.Dispose() }
}
Write-Output "Isolated output: $run"
