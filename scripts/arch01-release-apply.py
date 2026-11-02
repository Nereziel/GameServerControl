from pathlib import Path

path = Path('deploy/windows/install.ps1')
text = path.read_text()


def replace_once(old: str, new: str):
    global text
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'expected installer fragment not found: {old[:160]!r}')
    text = text.replace(old, new, 1)


replace_once(
    "$agentDir  = Join-Path $installRt 'Agent'\n$previousAgentDir = Join-Path $installRt 'Agent.previous'\n$trayDir   = Join-Path $installRt 'Tray'",
    "$agentDir  = Join-Path $installRt 'Agent'\n$previousAgentDir = Join-Path $installRt 'Agent.previous'\n$runnerDir = Join-Path $installRt 'Runner'\n$previousRunnerDir = Join-Path $installRt 'Runner.previous'\n$runnerExe = Join-Path $runnerDir 'GameServerControl.Runner.exe'\n$trayDir   = Join-Path $installRt 'Tray'")
replace_once(
    "$hadPreviousAgent = $false\n\nfunction Write-Step",
    "$hadPreviousAgent = $false\n$hadPreviousRunner = $false\n$runningRunnerServiceNames = @()\n\nfunction Write-Step")

replace_once(
    "function New-UpdateHealthToken {",
    '''function Set-RunnerDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }

    $aclOutput = & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed to restrict Runner directory ACL: $($aclOutput -join ' | ')" }

    foreach ($runnerService in @(Get-Service -Name 'GameServerControlRunner_*' -ErrorAction SilentlyContinue)) {
        $serviceAccount = "NT SERVICE\\$($runnerService.Name)"
        $grantOutput = & icacls.exe $Path /grant "${serviceAccount}:(OI)(CI)(RX)" /T /C 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Failed to grant Runner payload access to $serviceAccount: $($grantOutput -join ' | ')" }
    }
}

function New-UpdateHealthToken {''')

replace_once(
    '''$asset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Agent-windows.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Release asset not found: GameServerControl-Agent-windows.zip' }
$trayAsset =''',
    '''$asset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Agent-windows.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Release asset not found: GameServerControl-Agent-windows.zip' }
$runnerAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Runner-windows.zip' } | Select-Object -First 1
if (-not $runnerAsset) { throw 'Release asset not found: GameServerControl-Runner-windows.zip' }
$trayAsset =''')
replace_once(
    "Write-Ok 'Agent package downloaded and SHA-256 verified.'\n\n$trayZip =",
    '''Write-Ok 'Agent package downloaded and SHA-256 verified.'

$runnerZip = Join-Path $tmpRoot 'runner.zip'
Invoke-WebRequest -Uri $runnerAsset.browser_download_url -OutFile $runnerZip
Assert-ReleaseAssetHash -Path $runnerZip -AssetName $runnerAsset.name -ManifestPath $checksumsPath
Write-Ok 'Runner package downloaded and SHA-256 verified.'

$trayZip =''')

replace_once(
    '''if ($svc -and $svc.Status -eq 'Running') {
    Write-Step "Stopping existing $svcName service"
    Stop-Service -Name $svcName -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    Write-Ok 'Stopped.'
}

if (Test-Path -LiteralPath $agentDir) {''',
    '''if ($svc -and $svc.Status -eq 'Running') {
    Write-Step "Stopping existing $svcName service"
    Stop-Service -Name $svcName -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    Write-Ok 'Stopped.'
}

$runnerServices = @(Get-Service -Name 'GameServerControlRunner_*' -ErrorAction SilentlyContinue)
$runningRunnerServiceNames = @($runnerServices | Where-Object { $_.Status -eq 'Running' } | ForEach-Object { $_.Name })
if ($runningRunnerServiceNames.Count -gt 0) {
    Write-Step 'Stopping existing per-server Runner services'
    foreach ($runnerServiceName in $runningRunnerServiceNames) {
        Stop-Service -Name $runnerServiceName -Force -ErrorAction Stop
        (Get-Service -Name $runnerServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    Write-Ok "Stopped $($runningRunnerServiceNames.Count) Runner service(s)."
}

if (Test-Path -LiteralPath $agentDir) {''')

replace_once(
    "Write-Ok 'Agent unpacked.'\n\n# Remove the retired desktop client",
    '''Write-Ok 'Agent unpacked.'

Write-Step "Installing low-privilege Runner to $runnerDir"
if (Test-Path $previousRunnerDir) { Remove-Item $previousRunnerDir -Recurse -Force }
if (Test-Path $runnerDir) {
    Set-RunnerDirectoryAcl -Path $runnerDir
    Move-Item -LiteralPath $runnerDir -Destination $previousRunnerDir
    $hadPreviousRunner = $true
}
New-Item -ItemType Directory -Force -Path $runnerDir | Out-Null
Set-RunnerDirectoryAcl -Path $runnerDir
Expand-Archive -Path $runnerZip -DestinationPath $runnerDir -Force
Set-RunnerDirectoryAcl -Path $runnerDir
if (-not (Test-Path $runnerExe)) { throw "Runner executable missing after extraction: $runnerExe" }
Write-Ok 'Runner unpacked and restricted to SYSTEM, Administrators and existing Runner service SIDs.'

# Remove the retired desktop client''')

old_health = '''$healthPassed = Test-AgentUpdateHealth -Url $healthUrl -Token $updateHealthToken -ExpectedVersion $expectedVersion -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 3
Remove-Item -LiteralPath $updateHealthTokenPath -Force -ErrorAction SilentlyContinue
$updateHealthToken = $null

if (-not $healthPassed) {
    if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
        Write-Warn2 'New Agent failed authenticated health/version stability checks. Rolling back the previous payload.'
        Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
        if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
        Move-Item -LiteralPath $previousAgentDir -Destination $agentDir
        try {
            Start-Service -Name $svcName
            (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
            Write-Ok 'Previous Agent payload restored and service restarted.'
        } catch {
            throw "New Agent failed health verification and rollback could not restart the previous service: $($_.Exception.Message)"
        }
        throw 'New Agent failed authenticated health/version stability checks. The previous Agent payload was restored.'
    }
    Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
    throw 'New Agent failed authenticated health/version stability checks. Inspect the Application Event Log or run the Agent EXE directly.'
}

Write-Ok "Authenticated health/version gate passed for $expectedVersion with a stable process."
if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
    Remove-Item $previousAgentDir -Recurse -Force
    Write-Skip 'Removed previous Agent payload after authenticated health verification.'
}'''

new_health = '''$healthPassed = Test-AgentUpdateHealth -Url $healthUrl -Token $updateHealthToken -ExpectedVersion $expectedVersion -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 3
Remove-Item -LiteralPath $updateHealthTokenPath -Force -ErrorAction SilentlyContinue
$updateHealthToken = $null

$runnerRestartPassed = $true
if ($healthPassed -and $runningRunnerServiceNames.Count -gt 0) {
    Write-Step 'Restarting Runner services that were active before update'
    try {
        Set-RunnerDirectoryAcl -Path $runnerDir
        foreach ($runnerServiceName in $runningRunnerServiceNames) {
            Start-Service -Name $runnerServiceName -ErrorAction Stop
            (Get-Service -Name $runnerServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
        }
        Write-Ok "Restarted $($runningRunnerServiceNames.Count) Runner service(s)."
    } catch {
        $runnerRestartPassed = $false
        Write-Warn2 "Runner restart verification failed: $($_.Exception.Message)"
    }
}

if (-not $healthPassed -or -not $runnerRestartPassed) {
    if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
        Write-Warn2 'New Agent/Runner update failed health or Runner restart verification. Rolling back both payloads.'
        Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
        foreach ($runnerService in @(Get-Service -Name 'GameServerControlRunner_*' -ErrorAction SilentlyContinue)) {
            Stop-Service -Name $runnerService.Name -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
        Move-Item -LiteralPath $previousAgentDir -Destination $agentDir

        if (Test-Path $runnerDir) { Remove-Item $runnerDir -Recurse -Force }
        if ($hadPreviousRunner -and (Test-Path $previousRunnerDir)) {
            Move-Item -LiteralPath $previousRunnerDir -Destination $runnerDir
            Set-RunnerDirectoryAcl -Path $runnerDir
        }

        try {
            Start-Service -Name $svcName
            (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
            if ($hadPreviousRunner) {
                foreach ($runnerServiceName in $runningRunnerServiceNames) {
                    Start-Service -Name $runnerServiceName -ErrorAction SilentlyContinue
                }
            }
            Write-Ok 'Previous Agent + Runner payloads restored; services were restarted where applicable.'
        } catch {
            throw "New Agent/Runner update failed and rollback could not restart the previous service set: $($_.Exception.Message)"
        }
        throw 'New Agent/Runner update failed verification. Previous payloads were restored.'
    }
    Stop-Service -Name $svcName -Force -ErrorAction SilentlyContinue
    foreach ($runnerService in @(Get-Service -Name 'GameServerControlRunner_*' -ErrorAction SilentlyContinue)) {
        Stop-Service -Name $runnerService.Name -Force -ErrorAction SilentlyContinue
    }
    throw 'New Agent/Runner update failed verification. Inspect the Application Event Log or run the Agent EXE directly.'
}

Write-Ok "Authenticated health/version gate passed for $expectedVersion with a stable process."
if ($hadPreviousAgent -and (Test-Path $previousAgentDir)) {
    Remove-Item $previousAgentDir -Recurse -Force
    Write-Skip 'Removed previous Agent payload after authenticated health verification.'
}
if ($hadPreviousRunner -and (Test-Path $previousRunnerDir)) {
    Remove-Item $previousRunnerDir -Recurse -Force
    Write-Skip 'Removed Runner.previous after Agent health and Runner restart verification.'
}'''
replace_once(old_health, new_health)

path.write_text(text)
