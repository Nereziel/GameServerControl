from pathlib import Path


def replace_once(path, old, new):
    p = Path(path)
    text = p.read_text()
    if new in text:
        return
    if old not in text:
        raise SystemExit(f'expected fragment not found in {path}: {old[:140]!r}')
    p.write_text(text.replace(old, new, 1))

# Build CI: compile/publish/upload the Windows Runner.
replace_once(
    '.github/workflows/build.yml',
    '    name: Windows agent + web UI + tray',
    '    name: Windows agent + runner + web UI + tray')
replace_once(
    '.github/workflows/build.yml',
    '''      - name: Publish agent (win-x64, self-contained)
        run: dotnet publish src/GameServerControl.Agent -c Release -r win-x64 --self-contained true -o publish/Agent-windows

      - name: Publish tray helper (win-x64, self-contained)''',
    '''      - name: Publish agent (win-x64, self-contained)
        run: dotnet publish src/GameServerControl.Agent -c Release -r win-x64 --self-contained true -o publish/Agent-windows

      - name: Publish runner (win-x64, self-contained)
        run: dotnet publish src/GameServerControl.Runner -c Release -r win-x64 --self-contained true -o publish/Runner-windows

      - name: Publish tray helper (win-x64, self-contained)''')
replace_once(
    '.github/workflows/build.yml',
    '''      - name: Upload Windows agent
        uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7
        with:
          name: GameServerControl-Agent-windows
          path: publish/Agent-windows
          if-no-files-found: error

      - name: Upload Windows tray helper''',
    '''      - name: Upload Windows agent
        uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7
        with:
          name: GameServerControl-Agent-windows
          path: publish/Agent-windows
          if-no-files-found: error

      - name: Upload Windows runner
        uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7
        with:
          name: GameServerControl-Runner-windows
          path: publish/Runner-windows
          if-no-files-found: error

      - name: Upload Windows tray helper''')

# Release workflow: version, package, hash and publish Runner independently.
replace_once(
    '.github/workflows/release.yml',
    '    name: Build Windows agent + tray',
    '    name: Build Windows agent + runner + tray')
replace_once(
    '.github/workflows/release.yml',
    '''      - name: Publish tray helper (win-x64, self-contained)
        run: >-
          dotnet publish src/GameServerControl.Tray''',
    '''      - name: Publish runner (win-x64, self-contained)
        run: >-
          dotnet publish src/GameServerControl.Runner -c Release -r win-x64 --self-contained true
          -p:Version=${{ needs.prepare.outputs.version }}
          -p:AssemblyVersion=${{ needs.prepare.outputs.version }}.0
          -p:FileVersion=${{ needs.prepare.outputs.version }}.0
          -p:InformationalVersion=${{ needs.prepare.outputs.version }}
          -o publish/Runner-windows

      - name: Publish tray helper (win-x64, self-contained)
        run: >-
          dotnet publish src/GameServerControl.Tray''')
replace_once(
    '.github/workflows/release.yml',
    '''          $agent = (Get-Item 'publish/Agent-windows/GameServerControl.Agent.exe').VersionInfo.ProductVersion
          $tray = (Get-Item 'publish/Tray-windows/GameServerControl.Tray.exe').VersionInfo.ProductVersion
          if ($agent -ne $expected) { throw "Agent ProductVersion '$agent' does not match release '$expected'." }
          if ($tray -ne $expected) { throw "Tray ProductVersion '$tray' does not match release '$expected'." }
          Write-Host "Verified Agent + Tray version $expected"''',
    '''          $agent = (Get-Item 'publish/Agent-windows/GameServerControl.Agent.exe').VersionInfo.ProductVersion
          $runner = (Get-Item 'publish/Runner-windows/GameServerControl.Runner.exe').VersionInfo.ProductVersion
          $tray = (Get-Item 'publish/Tray-windows/GameServerControl.Tray.exe').VersionInfo.ProductVersion
          if ($agent -ne $expected) { throw "Agent ProductVersion '$agent' does not match release '$expected'." }
          if ($runner -ne $expected) { throw "Runner ProductVersion '$runner' does not match release '$expected'." }
          if ($tray -ne $expected) { throw "Tray ProductVersion '$tray' does not match release '$expected'." }
          Write-Host "Verified Agent + Runner + Tray version $expected"''')
replace_once(
    '.github/workflows/release.yml',
    '''          Compress-Archive -Path publish/Agent-windows/* -DestinationPath GameServerControl-Agent-windows.zip
          Compress-Archive -Path publish/Tray-windows/* -DestinationPath GameServerControl-Tray-windows.zip''',
    '''          Compress-Archive -Path publish/Agent-windows/* -DestinationPath GameServerControl-Agent-windows.zip
          Compress-Archive -Path publish/Runner-windows/* -DestinationPath GameServerControl-Runner-windows.zip
          Compress-Archive -Path publish/Tray-windows/* -DestinationPath GameServerControl-Tray-windows.zip''')
replace_once(
    '.github/workflows/release.yml',
    '''            GameServerControl-Agent-windows.zip
            GameServerControl-Tray-windows.zip''',
    '''            GameServerControl-Agent-windows.zip
            GameServerControl-Runner-windows.zip
            GameServerControl-Tray-windows.zip''')
# The first replacement above updates upload-artifact. Apply again for SHA manifest and release files if needed.
p = Path('.github/workflows/release.yml')
text = p.read_text()
text = text.replace(
    '''            GameServerControl-Agent-windows.zip \\
            GameServerControl-Tray-windows.zip \\''',
    '''            GameServerControl-Agent-windows.zip \\
            GameServerControl-Runner-windows.zip \\
            GameServerControl-Tray-windows.zip \\''',
    1)
# release files block may still have a second Agent+Tray adjacency.
text = text.replace(
    '''            GameServerControl-Agent-windows.zip
            GameServerControl-Tray-windows.zip''',
    '''            GameServerControl-Agent-windows.zip
            GameServerControl-Runner-windows.zip
            GameServerControl-Tray-windows.zip''',
    1)
p.write_text(text)

# Installer variables and ACL helper.
replace_once(
    'deploy/windows/install.ps1',
    '''$agentDir  = Join-Path $installRt 'Agent'
$previousAgentDir = Join-Path $installRt 'Agent.previous'
$trayDir   = Join-Path $installRt 'Tray' ''',
    '''$agentDir  = Join-Path $installRt 'Agent'
$previousAgentDir = Join-Path $installRt 'Agent.previous'
$runnerDir = Join-Path $installRt 'Runner'
$previousRunnerDir = Join-Path $installRt 'Runner.previous'
$runnerExe = Join-Path $runnerDir 'GameServerControl.Runner.exe'
$trayDir   = Join-Path $installRt 'Tray' ''')
replace_once(
    'deploy/windows/install.ps1',
    '''$hadPreviousAgent = $false

function Write-Step''',
    '''$hadPreviousAgent = $false
$hadPreviousRunner = $false
$runningRunnerServiceNames = @()

function Write-Step''')
replace_once(
    'deploy/windows/install.ps1',
    '''function New-UpdateHealthToken {''',
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

# Resolve and verify Runner asset alongside Agent.
replace_once(
    'deploy/windows/install.ps1',
    '''$asset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Agent-windows.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Release asset not found: GameServerControl-Agent-windows.zip' }
$trayAsset =''',
    '''$asset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Agent-windows.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Release asset not found: GameServerControl-Agent-windows.zip' }
$runnerAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Runner-windows.zip' } | Select-Object -First 1
if (-not $runnerAsset) { throw 'Release asset not found: GameServerControl-Runner-windows.zip' }
$trayAsset =''')
replace_once(
    'deploy/windows/install.ps1',
    '''Write-Ok 'Agent package downloaded and SHA-256 verified.'

$trayZip =''',
    '''Write-Ok 'Agent package downloaded and SHA-256 verified.'

$runnerZip = Join-Path $tmpRoot 'runner.zip'
Invoke-WebRequest -Uri $runnerAsset.browser_download_url -OutFile $runnerZip
Assert-ReleaseAssetHash -Path $runnerZip -AssetName $runnerAsset.name -ManifestPath $checksumsPath
Write-Ok 'Runner package downloaded and SHA-256 verified.'

$trayZip =''')

# Stop Agent first so it cannot restart runners, then stop any currently-running per-server runners.
replace_once(
    'deploy/windows/install.ps1',
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

# Install the Runner as a separate rollback-able payload after Agent extraction.
replace_once(
    'deploy/windows/install.ps1',
    '''Write-Ok 'Agent unpacked.'

# Remove the retired desktop client''',
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

# Extend health gate with previously-running Runner service restart, and roll both payloads back together.
replace_once(
    'deploy/windows/install.ps1',
    '''$healthPassed = Test-AgentUpdateHealth -Url $healthUrl -Token $updateHealthToken -ExpectedVersion $expectedVersion -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 3
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
}''',
    '''$healthPassed = Test-AgentUpdateHealth -Url $healthUrl -Token $updateHealthToken -ExpectedVersion $expectedVersion -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 3
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
}''')
