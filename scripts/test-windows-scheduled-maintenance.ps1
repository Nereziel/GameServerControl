# Windows-only end-to-end smoke test:
# real scheduled task (LocalSystem) -> published Agent CLI -> LocalMachine DPAPI
# -> authenticated HTTP POST to a temporary loopback listener.
# Does not start a real game server or use any production credentials.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) { throw 'Scheduled maintenance smoke test requires Windows.' }
if (-not $env:RUNNER_TEMP) { throw 'Run from the ephemeral Windows CI runner.' }

$id = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$folder = Join-Path $env:RUNNER_TEMP "gsc-schedule-e2e-$id"
$agentDir = Join-Path $folder 'Agent'
$taskName = "GSC-Scheduled-Smoke-$id"
$listener = $null
$client = $null
$createdTask = $false
New-Item -ItemType Directory -Path $agentDir -Force | Out-Null

try {
    & dotnet publish src/GameServerControl.Agent/GameServerControl.Agent.csproj -c Release -r win-x64 --self-contained false -p:UseAppHost=true -o $agentDir
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish test Agent.' }
    $agentExe = Join-Path $agentDir 'GameServerControl.Agent.exe'
    if (-not (Test-Path -LiteralPath $agentExe)) { throw 'Published Agent executable missing.' }

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port

    $tokenBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $token = [Convert]::ToBase64String($tokenBytes)
    $entropy = [Text.Encoding]::UTF8.GetBytes('GameServerControl:Secret:v1')
    $protected = [Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes($token),
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    $envelope = 'enc:v1:dpapi:' + [Convert]::ToBase64String($protected)
    @{
        Agent = @{
            ApiToken = $envelope
            Bind = "http://127.0.0.1:$port"
            DataDir = $folder
        }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $agentDir 'appsettings.json') -Encoding utf8

    # Task Scheduler must be able to run the published binary as LocalSystem.
    $action = '"' + $agentExe + '" --scheduled-maintenance backup alpha'
    $start = (Get-Date).AddMinutes(15).ToString('HH:mm', [Globalization.CultureInfo]::InvariantCulture)
    & schtasks.exe /create /f /tn $taskName /tr $action /sc ONCE /st $start /ru SYSTEM /rl HIGHEST | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not register the Windows scheduled task.' }
    $createdTask = $true

    $xml = (& schtasks.exe /query /tn $taskName /xml | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the scheduled task XML.' }
    if ($xml.Contains($token) -or $xml.Contains($envelope)) {
        throw 'Scheduled task exposed an authentication credential.'
    }
    if (-not $xml.Contains('--scheduled-maintenance')) {
        throw 'Scheduled task does not run the dedicated Agent callback.'
    }

    $incoming = $listener.AcceptTcpClientAsync()
    & schtasks.exe /run /tn $taskName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows Task Scheduler could not start the test task.' }

    if (-not $incoming.Wait([TimeSpan]::FromSeconds(60))) {
        throw 'The real scheduled task never contacted the Agent loopback listener.'
    }
    $client = $incoming.GetAwaiter().GetResult()
    $client.ReceiveTimeout = 15000
    $stream = $client.GetStream()
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII)
    $requestLine = $reader.ReadLine()
    $auth = $null
    for ($i = 0; $i -lt 100; $i++) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { throw 'HTTP request closed before headers completed.' }
        if ($line.Length -eq 0) { break }
        if ($line.StartsWith('Authorization:', [StringComparison]::OrdinalIgnoreCase)) {
            $auth = $line.Substring('Authorization:'.Length).Trim()
        }
    }
    $correct = $requestLine -eq 'POST /api/servers/alpha/backup HTTP/1.1' -and
        $auth -eq ('Bearer ' + $token)

    $response = if ($correct) {
        "HTTP/1.1 200 OK`r`nContent-Length: 0`r`nConnection: close`r`n`r`n"
    } else {
        "HTTP/1.1 401 Unauthorized`r`nContent-Length: 0`r`nConnection: close`r`n`r`n"
    }
    $bytes = [Text.Encoding]::ASCII.GetBytes($response)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    $client.Close()
    $client = $null
    if (-not $correct) { throw 'The scheduled task sent an unauthenticated or incorrect request.' }

    $service = New-Object -ComObject 'Schedule.Service'
    $service.Connect()
    $registeredTask = $service.GetFolder('\').GetTask($taskName)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 300
        $state = [int]$registeredTask.State
        $lastRun = $registeredTask.LastRunTime
    } while (($state -eq 4 -or $lastRun.Year -lt 2000) -and [DateTime]::UtcNow -lt $deadline)
    if ([int]$registeredTask.LastTaskResult -ne 0) {
        throw "Scheduled Agent CLI returned a non-zero result code: $($registeredTask.LastTaskResult)"
    }
    Write-Host 'Windows scheduled task -> DPAPI -> authenticated Agent loopback HTTP smoke test OK.'
}
finally {
    if ($null -ne $client) { $client.Close() }
    if ($null -ne $listener) { $listener.Stop() }
    if ($createdTask) {
        & schtasks.exe /delete /f /tn $taskName 2>$null | Out-Null
    }
    if (Test-Path -LiteralPath $folder) {
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
    }
}
