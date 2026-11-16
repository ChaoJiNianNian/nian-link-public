param(
  [string]$Candidate,
  [string]$CoreCandidate,
  [string]$CacheRoot
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Candidate) -or [string]::IsNullOrWhiteSpace($CoreCandidate) -or [string]::IsNullOrWhiteSpace($CacheRoot)) {
  throw 'Explicit isolated research paths are required: Candidate, CoreCandidate and CacheRoot.'
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
$coreCandidate = Assert-Isolated $CoreCandidate
$source = Join-Path $candidate 'source'
$coreBuild = Join-Path $coreCandidate 'build-02'
foreach ($path in @($source, $coreBuild)) {
  $null = Assert-Isolated $path
  if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw 'Isolated source or build is missing.' }
}
Assert-NoReparseTree $source
Assert-NoReparseTree $coreBuild
$run = Assert-Isolated (Join-Path $work ('native-fence-qt-test-' + [guid]::NewGuid().ToString('N')))
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
if ($LASTEXITCODE -ne 0) { throw 'Fixture configuration failed.' }
& cmake --build $run --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $run 'native-fence-qt.exe'))
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
try {
  $endpoint = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
  if ($endpoint -ne "nian-fence-harness-$($process.Id)" -and $endpoint -ne "nian-deskflow-control-$($process.Id)") { throw 'Fixture did not produce its own PID-specific endpoint.' }
  function New-Channel {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $endpoint, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect(3000)
    $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 1024, $true); $writer.AutoFlush = $true
    return [PSCustomObject]@{ Pipe = $pipe; Reader = $reader; Writer = $writer; LastAction = '' }
  }
  function Send-Frame($channel, $request) { $channel.LastAction = [string]$request.action; $channel.Writer.WriteLine(($request | ConvertTo-Json -Compress)) }
  function Read-Frame($channel) {
    try { $line = $channel.Reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult() }
    catch { throw "Timed out or failed reading the response after $($channel.LastAction): $($_.Exception.Message)" }
    if ($null -eq $line) { throw 'Unexpected fixture disconnection.' }
    return ($line | ConvertFrom-Json)
  }
  function Assert-Response($frame, [string]$requestId, [bool]$ok, [string]$context) {
    if ($frame.version -isnot [long] -or $frame.version -ne 2 -or $frame.requestId -isnot [string] -or $frame.requestId -cne $requestId -or $frame.ok -isnot [bool] -or $frame.ok -ne $ok) {
      throw "$context returned an uncorrelated or invalid response."
    }
  }
  function Close-Channel($channel) { $channel.Writer.Dispose(); $channel.Reader.Dispose(); $channel.Pipe.Dispose() }
  $one = New-Channel; $two = New-Channel
  try {
    Send-Frame $two @{ version=1; requestId='a'*32; action='switch-if-current'; target='Peer'; expectedActive='Host'; expectedRevision=0 }
    $old = Read-Frame $two
    if ($old.ok -or $old.error -ne 'research_v1_disabled') { throw 'RED: v1 switch was not rejected.' }
    if ($endpoint -ne "nian-fence-harness-$($process.Id)") { throw 'Research endpoint name was not isolated.' }
    Send-Frame $two @{ version=1; requestId=('a'*31+'0'); action='switch'; target='Host' }
    if ((Read-Frame $two).error -ne 'research_v1_disabled') { throw 'Unconditional v1 switching was not rejected.' }
    Send-Frame $two @{ version=1; requestId=('a'*30+'00'); action='state' }
    if (-not (Read-Frame $two).ok) { throw 'Research mode unexpectedly rejected the synthetic state read.' }
    $inc = '1'*32; $operation = '2'*32
    # A complete JSON object without its newline is still an incomplete frame.
    # Wait for a test-only signal from the real Qt readClient buffer, not a sleep.
    $partialOperation = '6'*32
    $partial = @{ version=2; requestId='d'*32; action='fence-begin'; target='Host'; operationId=$partialOperation; incarnation=$inc }
    $one.LastAction = 'partial fence-begin'
    $one.Writer.Write(($partial | ConvertTo-Json -Compress))
    $buffered = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult()
    if ($buffered -ne 'PARTIAL_BUFFERED') { throw 'The incomplete frame was not observed in the Qt buffer.' }
    Write-Output 'Observed partial frame in Qt buffer.'
    Send-Frame $two @{ version=2; requestId=('d'*30+'04'); action='fence-seal'; target='Host'; operationId=$partialOperation; incarnation=$inc }
    $partialSeal = Read-Frame $two
    Assert-Response $partialSeal ('d'*30+'04') $true 'Partial-frame Seal'
    if ($partialSeal.result -ne 'sealed' -or $partialSeal.syntheticBegins -ne 0) { throw 'Partial-frame operation was not sealed before completion.' }
    $one.Writer.WriteLine()
    $partialBegin = Read-Frame $one
    # Only emit fixed synthetic-value categories, never arbitrary pipe contents.
    $observedVersion = if ($partialBegin.version -is [long] -and $partialBegin.version -eq 2) { '2' } elseif ($partialBegin.version -is [long] -and $partialBegin.version -eq 1) { '1' } else { 'other' }
    $observedRequest = if ($partialBegin.requestId -is [string] -and $partialBegin.requestId -ceq ('d'*32)) { 'd-times-32' } elseif ($partialBegin.requestId -is [string] -and $partialBegin.requestId -ceq ('f'*32)) { 'f-times-32' } else { 'other' }
    $observedOk = if ($partialBegin.ok -is [bool] -and $partialBegin.ok) { 'true' } elseif ($partialBegin.ok -is [bool]) { 'false' } else { 'other' }
    Write-Output "Late partial response fields: version=$observedVersion, requestId=$observedRequest, ok=$observedOk."
    $observedResult = if ($partialBegin.result -ceq 'rejected') { 'rejected' } elseif ($partialBegin.result -ceq 'active') { 'active' } else { 'other' }
    $observedBegins = if ($partialBegin.syntheticBegins -is [long] -and $partialBegin.syntheticBegins -eq 0) { '0' } elseif ($partialBegin.syntheticBegins -is [long] -and $partialBegin.syntheticBegins -eq 1) { '1' } else { 'other' }
    Write-Output "Observed late partial Begin: result=$observedResult, begins=$observedBegins."
    Assert-Response $partialBegin ('d'*32) $false 'Late partial Begin'
    if ($partialBegin.result -ne 'rejected' -or $partialBegin.syntheticBegins -ne 0) { throw 'Late partial Begin was not rejected.' }
    Write-Output 'PASS: buffered partial Begin rejected after synthetic seal.'
    Send-Frame $two @{ version=2; requestId=('b'*31+'4'); action='fence-seal'; target='Host'; operationId=$partialOperation; incarnation=$inc }
    $wrongIdentitySeal = Read-Frame $two
    Assert-Response $wrongIdentitySeal ('b'*31+'4') $false 'Different identity Seal'
    if ($wrongIdentitySeal.result -ne 'rejected') { throw 'A different synthetic identity reused the partial-frame operation.' }
    Send-Frame $two @{ version=2; requestId=('d'*30+'05'); action='fence-seal'; target='Host'; operationId=$partialOperation; incarnation=('4'*32) }
    $wrongIncarnationSeal = Read-Frame $two
    Assert-Response $wrongIncarnationSeal ('d'*30+'05') $false 'Different incarnation Seal'
    if ($wrongIncarnationSeal.result -ne 'rejected') { throw 'A different incarnation reused the partial-frame operation.' }
    Send-Frame $one @{ version=2; requestId='a'*32; action='fence-begin'; target='Host'; operationId=$operation; incarnation=$inc }
    $held = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult()
    if ($held -ne 'BEGIN_HELD') { throw 'The completed Begin frame was not held before enqueue.' }
    Send-Frame $two @{ version=2; requestId=('a'*31+'1'); action='fence-seal'; target='Host'; operationId=$operation; incarnation=$inc }
    $sealed = Read-Frame $two
    if ($sealed.version -ne 2 -or $sealed.result -ne 'sealed') { throw 'Seal was not processed by the real event queue.' }
    $begin = Read-Frame $one
    if ($begin.version -ne 2 -or $begin.result -ne 'rejected' -or $begin.syntheticBegins -ne 0) { throw 'The delayed Begin was not rejected.' }
    Send-Frame $two @{ version=2; requestId=('a'*31+'2'); action='fence-begin'; target='Host'; operationId=$operation; incarnation=$inc }
    if ((Read-Frame $two).result -ne 'rejected') { throw 'Replayed Begin must remain rejected.' }
    Send-Frame $two @{ version=2; requestId='b'*32; action='fence-seal'; target='Host'; operationId=$operation; incarnation=$inc }
    if ((Read-Frame $two).result -ne 'rejected') { throw 'A different synthetic identity reused an operation.' }
    Send-Frame $two @{ version=2; requestId=('a'*31+'3'); action='fence-seal'; target='Host'; operationId=$operation; incarnation=$inc; principal='forged' }
    $bad = Read-Frame $two
    if ($bad.ok -or $bad.error -ne 'unknown_field') { throw 'Untrusted identity field was not rejected.' }
    Send-Frame $two @{ version=2; requestId=('a'*30+'08'); action='fence-seal'; target='Host'; operationId=('0'*32); incarnation=$inc }
    if ((Read-Frame $two).error -ne 'invalid_fence_fields') { throw 'Empty operation identifier was accepted.' }
    Send-Frame $two @{ version=2; requestId=('a'*30+'09'); action='fence-seal'; target='Host'; operationId=$operation; incarnation=('4'*32) }
    if ((Read-Frame $two).result -ne 'rejected') { throw 'An old incarnation was accepted.' }
    Send-Frame $two @{ version=2; requestId=('a'*30+'10'); action='fence-seal'; target='Peer'; operationId=$operation; incarnation=$inc }
    if ((Read-Frame $two).result -ne 'rejected') { throw 'An operation was rebound to a different target.' }
    $second = '3'*32
    Send-Frame $one @{ version=2; requestId=('a'*31+'4'); action='fence-begin'; target='Host'; operationId=$second; incarnation=$inc }
    $held = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult()
    if ($held -ne 'BEGIN_HELD') { throw 'Second Begin did not reach held state.' }
    Close-Channel $one; $one = $null
    Send-Frame $two @{ version=2; requestId=('a'*31+'5'); action='fence-seal'; target='Host'; operationId=$second; incarnation=$inc }
    if ((Read-Frame $two).result -ne 'sealed') { throw 'Seal after peer disconnect failed.' }
    Send-Frame $two @{ version=2; requestId=('a'*31+'6'); action='fence-begin'; target='Host'; operationId=$second; incarnation=$inc }
    $afterDisconnect = Read-Frame $two
    if ($afterDisconnect.result -ne 'rejected' -or $afterDisconnect.syntheticBegins -ne 0) { throw 'A disconnected Begin was executed.' }
    $caseOperation = 'a'*32
    Send-Frame $two @{ version=2; requestId=('a'*30+'11'); action='fence-begin'; target='Host'; operationId=$caseOperation; incarnation=$inc }
    $held = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(4)).GetAwaiter().GetResult()
    if ($held -ne 'BEGIN_HELD') { throw 'Case-normalization Begin did not reach held state.' }
    $one = New-Channel
    Send-Frame $one @{ version=2; requestId=('a'*30+'12'); action='fence-seal'; target='Host'; operationId=$caseOperation.ToUpperInvariant(); incarnation=$inc }
    if ((Read-Frame $one).result -ne 'sealed') { throw 'Uppercase seal was not normalized.' }
    if ((Read-Frame $two).result -ne 'rejected') { throw 'An equivalent lowercase operation bypassed the seal.' }
    $ordered = '5'*32
    Send-Frame $two @{ version=2; requestId='d'*32; action='fence-begin'; target='Host'; operationId=$ordered; incarnation=$inc }
    $active = Read-Frame $two
    if ($active.result -ne 'active' -or $active.syntheticBegins -ne 1) { throw 'Ordered Begin did not execute exactly once.' }
    Send-Frame $two @{ version=2; requestId=('d'*31+'1'); action='fence-seal'; target='Host'; operationId=$ordered; incarnation=$inc }
    $orderedSeal = Read-Frame $two
    if ($orderedSeal.result -ne 'sealed' -or $orderedSeal.syntheticReturns -ne 1) { throw 'Ordered Seal did not execute exactly once.' }
    Send-Frame $two @{ version=2; requestId=('d'*31+'2'); action='fence-seal'; target='Host'; operationId=$ordered; incarnation=$inc }
    $repeatSeal = Read-Frame $two
    if ($repeatSeal.result -ne 'sealed' -or $repeatSeal.syntheticReturns -ne 1) { throw 'Repeated Seal repeated its synthetic return.' }
    Send-Frame $two @{ version=2; requestId=('d'*31+'3'); action='fence-begin'; target='Host'; operationId=$ordered; incarnation=$inc }
    $repeatBegin = Read-Frame $two
    if ($repeatBegin.result -ne 'rejected' -or $repeatBegin.syntheticBegins -ne 1) { throw 'Ordered sealed Begin was replayed.' }
    $pendingChannel = New-Channel
    try {
      for ($i=0; $i -lt 8; $i++) {
        $requestId = 'e' + ('{0:x31}' -f $i)
        Send-Frame $pendingChannel @{ version=2; requestId=$requestId; action='fence-begin'; target='Host'; operationId=[guid]::NewGuid().ToString('N'); incarnation=$inc }
      }
      Send-Frame $pendingChannel @{ version=2; requestId=('e' + ('{0:x31}' -f 0)); action='fence-begin'; target='Host'; operationId=[guid]::NewGuid().ToString('N'); incarnation=$inc }
      if ((Read-Frame $pendingChannel).error -ne 'duplicate_request_id') { throw 'Pending duplicate requestId was accepted.' }
      Send-Frame $pendingChannel @{ version=2; requestId=('e' + ('{0:x31}' -f 8)); action='fence-begin'; target='Host'; operationId=[guid]::NewGuid().ToString('N'); incarnation=$inc }
      if ((Read-Frame $pendingChannel).error -ne 'too_many_requests') { throw 'Per-client pending capacity was exceeded.' }
    }
    finally { Close-Channel $pendingChannel }
    for ($i=0; $i -lt 3; $i++) {
      Send-Frame $two @{ version=2; requestId=('a' + ('{0:x31}' -f (100 + $i))); action='fence-seal'; target='Host'; operationId=[guid]::NewGuid().ToString('N'); incarnation=$inc }
      if ((Read-Frame $two).result -ne 'sealed') { throw 'Expected tombstone capacity was unavailable.' }
    }
    Send-Frame $two @{ version=2; requestId=('a'*30+'13'); action='fence-seal'; target='Host'; operationId=[guid]::NewGuid().ToString('N'); incarnation=$inc }
    if ((Read-Frame $two).result -ne 'unconfirmed') { throw 'Full tombstone capacity did not fail closed.' }
    Send-Frame $two @{ version=2; requestId=('a'*31+'7'); action='fixture-exit' }
    if (-not (Read-Frame $two).ok) { throw 'Fixture could not shut down.' }
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'Fixture did not stop normally.' }
    Write-Output 'PASS: synthetic Qt control and EventQueue fence scenarios.'
  }
  finally { if ($one) { Close-Channel $one }; Close-Channel $two }
}
finally {
  if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
  [IO.File]::WriteAllText((Join-Path $run 'fixture.stderr.txt'), $process.StandardError.ReadToEnd())
  $process.Dispose()
}
$normalStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $run 'native-control-default.exe'))
$normalStart.UseShellExecute = $false; $normalStart.CreateNoWindow = $true
$normalStart.RedirectStandardOutput = $true; $normalStart.RedirectStandardError = $true
$normalStart.Environment['DESKTOOLBOX_CONTROL'] = '1'
$normalProcess = [Diagnostics.Process]::Start($normalStart)
try {
  $endpoint = $normalProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
  if ($endpoint -ne "nian-deskflow-control-$($normalProcess.Id)") { throw 'Default fixture did not use its own PID endpoint.' }
  $normal = New-Channel
  try {
    Send-Frame $normal @{ version=1; requestId='c'*32; action='switch-if-current'; target='Peer'; expectedActive='Host'; expectedRevision=0 }
    $normalSwitch = Read-Frame $normal
    if (-not $normalSwitch.ok -or $normalSwitch.activeComputer -ne 'Peer') { throw 'Default v1 path changed.' }
    Send-Frame $normal @{ version=2; requestId=('c'*31+'1'); action='fence-seal'; target='Host'; operationId=('2'*32); incarnation=('1'*32) }
    $normalV2 = Read-Frame $normal
    if ($normalV2.ok -or $normalV2.error -ne 'unsupported_version') { throw 'Default build accepted research v2.' }
    Write-Output 'PASS: default fixture retains v1 and rejects research v2.'
  }
  finally { Close-Channel $normal }
}
finally {
  # This is the exact helper created above, not a Deskflow core or another process.
  if (-not $normalProcess.HasExited) { $normalProcess.Kill(); $normalProcess.WaitForExit() }
  [IO.File]::WriteAllText((Join-Path $run 'default.stderr.txt'), $normalProcess.StandardError.ReadToEnd())
  $normalProcess.Dispose()
}
Write-Output "Isolated output: $run"
