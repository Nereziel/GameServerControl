# GameServerControl — Windows installer / updater (Agent + API + Web UI + optional tray helper).
#
# Run from an elevated PowerShell after downloading GameServerControl-Install-Windows.ps1
# and verifying it against the SHA256SUMS asset from the same GitHub Release.
#
# Release artifacts are self-contained. No preinstalled .NET runtime is required.
#
# Override variables:
#   $env:GSC_REPO       — owner/name (default: Nereziel/GameServerControl)
#   $env:GSC_INSTALL    — install root (default: C:\GameServerControl)
#   $env:GSC_VERSION    — pin a tag like v1.1.0 (default: latest)
#   $env:GSC_SILENT_UPDATE — when set, skip interactive shortcut/token/tray replacement output
#   $env:GSC_FROM_TRAY  — internal: updater was started by the tray helper

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    Write-Host 'This installer must run as Administrator.' -ForegroundColor Red
    Write-Host 'Open an elevated PowerShell and run the verified local release installer with -File.' -ForegroundColor Yellow
    return
}

$repo      = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'Nereziel/GameServerControl' }
$installRt = if ($env:GSC_INSTALL) { $env:GSC_INSTALL } else { 'C:\GameServerControl' }
$agentDir  = Join-Path $installRt 'Agent'
$previousAgentDir = Join-Path $installRt 'Agent.previous'
$runnerDir = Join-Path $installRt 'Runner'
$previousRunnerDir = Join-Path $installRt 'Runner.previous'
$runnerExe = Join-Path $runnerDir 'GameServerControl.Runner.exe'
$trayDir   = Join-Path $installRt 'Tray'
$trayExe   = Join-Path $trayDir 'GameServerControl.Tray.exe'
$legacyClientDir = Join-Path $installRt 'Client'
$pinnedTag = $env:GSC_VERSION
$svcName   = 'GameServerControlAgent'
$silentUpdate = -not [string]::IsNullOrWhiteSpace($env:GSC_SILENT_UPDATE)
$fromTray = -not [string]::IsNullOrWhiteSpace($env:GSC_FROM_TRAY)
$hadPreviousAgent = $false
$hadPreviousRunner = $false
$runningRunnerServiceNames = @()

function Write-Step($msg) { Write-Host ''; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "    -- $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg){ Write-Host "    !! $msg" -ForegroundColor Yellow }

function Get-ExpectedSha256 {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$AssetName
    )
    $found = @()
    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
            $name = $Matches[2].Trim()
            if ($name -eq $AssetName) { $found += $Matches[1].ToUpperInvariant() }
        }
    }
    if ($found.Count -ne 1) {
        throw "SHA256SUMS must contain exactly one checksum for $AssetName; found $($found.Count)."
    }
    return $found[0]
}

function Assert-ReleaseAssetHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AssetName,
        [Parameter(Mandatory = $true)][string]$ManifestPath
    )
    $expected = Get-ExpectedSha256 -ManifestPath $ManifestPath -AssetName $AssetName
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $expected) {
        throw "SHA-256 verification failed for $AssetName. Expected $expected but downloaded $actual."
    }
}

function Set-AgentDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }

    # Stable well-known SIDs avoid localized account-name issues. The managed service runs
    # as LocalSystem, so these correspond to SYSTEM:(OI)(CI)F and
    # BUILTIN\Administrators:(OI)(CI)F and are inherited by secret state files.
    # /inheritance:r removes inherited file ACEs. Inheritable (OI)(CI) grants alone
    # do not guarantee effective access on existing files after /T; explicitly grant
    # SYSTEM and Administrators access on each descendant too.
    $aclOutput = & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed to restrict Agent directory ACL: $($aclOutput -join ' | ')" }
    $fileAclOutput = & icacls.exe $Path /grant '*S-1-5-18:F' '*S-1-5-32-544:F' /T /C 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant Agent file access to SYSTEM and Administrators: $($fileAclOutput -join ' | ')" }
}

function Set-RunnerDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }

    $aclOutput = & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed to restrict Runner directory ACL: $($aclOutput -join ' | ')" }
    $fileAclOutput = & icacls.exe $Path /grant '*S-1-5-18:F' '*S-1-5-32-544:F' /T /C 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Failed to grant Runner file access to SYSTEM and Administrators: $($fileAclOutput -join ' | ')" }

    foreach ($runnerService in @(Get-Service -Name 'GameServerControlRunner_*' -ErrorAction SilentlyContinue)) {
        $serviceAccount = "NT SERVICE\$($runnerService.Name)"
        $grantOutput = & icacls.exe $Path /grant "${serviceAccount}:(OI)(CI)(RX)" /T /C 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Failed to grant Runner payload access to ${serviceAccount}: $($grantOutput -join ' | ')" }
        $fileGrantOutput = & icacls.exe $Path /grant "${serviceAccount}:(RX)" /T /C 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Failed to grant Runner file read/execute access to ${serviceAccount}: $($fileGrantOutput -join ' | ')" }
    }
}

function New-UpdateHealthToken {
    param([Parameter(Mandatory = $true)][string]$Path)
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    [IO.File]::WriteAllText($Path, $token, [Text.Encoding]::ASCII)
    return $token
}

function Get-LocalAgentHealthUrl {
    param([string]$Bind)
    if ([string]::IsNullOrWhiteSpace($Bind)) { $Bind = 'http://127.0.0.1:5099' }
    $candidate = $Bind.Trim()
    if ($candidate -match '^(https?)://(\*|\+|0\.0\.0\.0|\[::\])(?::([0-9]+))?/?$') {
        $scheme = $Matches[1]
        $port = $Matches[3]
        $localHost = if ($scheme -eq 'https') { [Environment]::MachineName.ToLowerInvariant() } else { '127.0.0.1' }
        $candidate = if ($port) { "${scheme}://${localHost}:$port" } else { "${scheme}://${localHost}" }
    }
    try { $uri = [Uri]$candidate } catch { throw "Agent:Bind is not a valid absolute URL: $Bind" }
    if ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https') { throw "Agent:Bind must use http or https: $Bind" }
    $builder = New-Object UriBuilder($uri)
    $builder.Path = '/api/health'
    $builder.Query = ''
    $builder.Fragment = ''
    return $builder.Uri.AbsoluteUri
}

function Test-AgentUpdateHealth {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [int]$TimeoutSeconds = 30,
        [int]$RequiredConsecutiveSuccesses = 3
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $consecutiveSuccesses = 0
    $stableProcessId = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $service = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if (-not $service -or $service.Status -ne 'Running') {
            $consecutiveSuccesses = 0
            $stableProcessId = $null
        } else {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -Method Get -TimeoutSec 3 -Headers @{ Authorization = "Bearer $Token" }
                $body = $response.Content | ConvertFrom-Json
                $version = [string]$response.Headers['X-GameServerControl-Version']
                $processId = [string]$response.Headers['X-GameServerControl-Pid']
                $valid = $response.StatusCode -eq 200 -and $body.ok -eq $true -and $version -eq $ExpectedVersion -and $processId -match '^[1-9][0-9]*$'
                if ($valid) {
                    if ($stableProcessId -eq $processId) { $consecutiveSuccesses += 1 }
                    else { $stableProcessId = $processId; $consecutiveSuccesses = 1 }
                } else {
                    $consecutiveSuccesses = 0
                    $stableProcessId = $null
                }
            } catch {
                $consecutiveSuccesses = 0
                $stableProcessId = $null
            }
        }
        if ($consecutiveSuccesses -ge $RequiredConsecutiveSuccesses) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

Write-Host ''
Write-Host 'GameServerControl installer (Windows)'
Write-Host '-------------------------------------'
Write-Host "Repository: $repo"
Write-Ok 'Web-only administration: Agent + API + browser UI.'
Write-Ok 'Optional tray helper provides quick local service controls.'
Write-Ok 'Self-contained release — system .NET runtime is not required.'

Write-Step 'Resolving release from GitHub'
$apiUrl = if ($pinnedTag) {
    "https://api.github.com/repos/$repo/releases/tags/$pinnedTag"
} else {
    "https://api.github.com/repos/$repo/releases/latest"
}
$headers = @{ 'User-Agent' = 'gsc-installer' }
try { $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers }
catch { throw "Could not resolve a GitHub release from $repo. $($_.Exception.Message)" }
$tag = [string]$release.tag_name
if ([string]::IsNullOrWhiteSpace($tag)) { throw 'Resolved release does not contain a tag name.' }
if ($pinnedTag -and $tag -ne $pinnedTag) { throw "Resolved release tag '$tag' does not match requested tag '$pinnedTag'." }
Write-Ok "Tag: $tag  ·  Published: $($release.published_at)"

$asset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Agent-windows.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Release asset not found: GameServerControl-Agent-windows.zip' }
$runnerAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Runner-windows.zip' } | Select-Object -First 1
if (-not $runnerAsset) { throw 'Release asset not found: GameServerControl-Runner-windows.zip' }
$trayAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Tray-windows.zip' } | Select-Object -First 1
$checksumAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' } | Select-Object -First 1
if (-not $checksumAsset) { throw 'Release asset not found: SHA256SUMS' }
$installTray = (-not $silentUpdate) -and ($null -ne $trayAsset)

Write-Step 'Downloading and verifying Agent + Web UI'
$tmpRoot = Join-Path $env:TEMP "gsc-install-$(Get-Random -Maximum 99999)"
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$checksumsPath = Join-Path $tmpRoot 'SHA256SUMS'
Invoke-WebRequest -Uri $checksumAsset.browser_download_url -OutFile $checksumsPath

$agentZip = Join-Path $tmpRoot 'agent.zip'
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $agentZip
Assert-ReleaseAssetHash -Path $agentZip -AssetName $asset.name -ManifestPath $checksumsPath
Write-Ok 'Agent package downloaded and SHA-256 verified.'

$runnerZip = Join-Path $tmpRoot 'runner.zip'
Invoke-WebRequest -Uri $runnerAsset.browser_download_url -OutFile $runnerZip
Assert-ReleaseAssetHash -Path $runnerZip -AssetName $runnerAsset.name -ManifestPath $checksumsPath
Write-Ok 'Runner package downloaded and SHA-256 verified.'

$trayZip = Join-Path $tmpRoot 'tray.zip'
if ($installTray) {
    Invoke-WebRequest -Uri $trayAsset.browser_download_url -OutFile $trayZip
    Assert-ReleaseAssetHash -Path $trayZip -AssetName $trayAsset.name -ManifestPath $checksumsPath
    Write-Ok 'Tray helper package downloaded and SHA-256 verified.'
} elseif (-not $trayAsset) {
    Write-Warn2 'This release has no Windows tray helper asset; Agent installation will continue without it.'
} elseif ($silentUpdate) {
    Write-Skip 'Silent Agent update: keeping the currently running tray helper untouched.'
}

$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
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

if (Test-Path -LiteralPath $agentDir) {
    Write-Step 'Restricting existing Agent state before upgrade'
    Set-AgentDirectoryAcl -Path $agentDir
    Write-Ok 'Existing Agent directory restricted to SYSTEM and local Administrators.'
}

Write-Step "Installing Agent to $agentDir"
$preserve = @('appsettings.json', 'servers.json', 'tokens.json', 'public-relay.json')
$backupDir = Join-Path $tmpRoot 'preserved'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
foreach ($f in $preserve) {
    $src = Join-Path $agentDir $f
    if (Test-Path $src) {
        Copy-Item $src $backupDir -Force
        Write-Skip "Preserving $f"
    }
}

# Rollback previous Agent payload: keep the complete old directory until the replacement service is healthy.
if (Test-Path $previousAgentDir) { Remove-Item $previousAgentDir -Recurse -Force }
if (Test-Path $agentDir) {
    Move-Item -LiteralPath $agentDir -Destination $previousAgentDir
    $hadPreviousAgent = $true
}
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
Set-AgentDirectoryAcl -Path $agentDir
Expand-Archive -Path $agentZip -DestinationPath $agentDir -Force
foreach ($f in $preserve) {
    $src = Join-Path $backupDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $agentDir $f) -Force }
}

$appSettings = Join-Path $agentDir 'appsettings.json'
$appSettingsExample = Join-Path $agentDir 'appsettings.json.example'
if (-not (Test-Path $appSettings)) {
    if (-not (Test-Path $appSettingsExample)) { throw 'Release artifact is missing appsettings.json.example.' }
    Copy-Item $appSettingsExample $appSettings -Force
    Write-Ok 'Created appsettings.json from template.'
}

$svcExePath = Join-Path $agentDir 'GameServerControl.Agent.exe'
if (-not (Test-Path $svcExePath)) { throw "Agent executable missing after extraction: $svcExePath" }
Write-Ok 'Agent unpacked.'

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

# Remove the retired desktop client on upgrades from pre-1.1 releases.
if (Test-Path $legacyClientDir) {
    Write-Step 'Removing retired desktop client'
    try {
        Get-Process -Name 'GameServerControl' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Remove-Item $legacyClientDir -Recurse -Force
        Write-Ok 'Legacy Client directory removed.'
    } catch { Write-Warn2 "Could not fully remove legacy client: $_" }
}

if ($installTray) {
    Write-Step "Installing tray helper to $trayDir"
    Get-Process -Name 'GameServerControl.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    if (Test-Path $trayDir) { Remove-Item $trayDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $trayDir | Out-Null
    Expand-Archive -Path $trayZip -DestinationPath $trayDir -Force
    if (-not (Test-Path $trayExe)) { throw "Tray executable missing after extraction: $trayExe" }
    Write-Ok "Tray helper: $trayExe"
}

Write-Step 'SteamCMD'
$steamCmdDir = Join-Path $agentDir 'SteamCMD'
$steamCmdExe = Join-Path $steamCmdDir 'steamcmd.exe'
if (-not (Test-Path $steamCmdExe)) {
    New-Item -ItemType Directory -Force -Path $steamCmdDir | Out-Null
    $steamZip = Join-Path $tmpRoot 'steamcmd.zip'
    Invoke-WebRequest -Uri 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile $steamZip
    Expand-Archive -Path $steamZip -DestinationPath $steamCmdDir -Force
}
if (-not (Test-Path $steamCmdExe)) { throw "SteamCMD extraction completed but steamcmd.exe is missing: $steamCmdExe" }
Write-Ok "SteamCMD: $steamCmdExe"

try {
    $cfgObj = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
    if (-not $cfgObj.Agent) { throw 'Agent section missing in appsettings.json' }
    $cfgObj.Agent | Add-Member -NotePropertyName 'SteamCmdPath' -NotePropertyValue $steamCmdExe -Force
    $cfgObj.Agent | Add-Member -NotePropertyName 'Repo' -NotePropertyValue $repo -Force
    $cfgObj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $appSettings -Encoding UTF8
    Write-Ok 'Configured Agent:SteamCmdPath and Agent:Repo.'
} catch { throw "Failed to update $appSettings : $_" }

Write-Step 'Restricting installed Agent state permissions'
Set-AgentDirectoryAcl -Path $agentDir
Write-Ok 'Agent directory restricted to SYSTEM and local Administrators.'

function Invoke-Sc {
    param([string[]]$ScArgs, [string]$Label)
    $out = & sc.exe @ScArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Label failed (sc.exe exit $LASTEXITCODE): $($out -join ' | ')" }
    $out
}

$existing = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Step "Refreshing $svcName service"
    Invoke-Sc -ScArgs @('config', $svcName, 'binPath=', "`"$svcExePath`"", 'start=', 'auto') -Label 'sc.exe config' | Out-Null
} else {
    Write-Step "Registering $svcName Windows service"
    Invoke-Sc -ScArgs @('create', $svcName, 'binPath=', "`"$svcExePath`"", 'start=', 'auto', 'DisplayName=', 'GameServerControl Agent') -Label 'sc.exe create' | Out-Null
}
try {
    Invoke-Sc -ScArgs @('description', $svcName, "GameServerControl Agent/API/Web UI. github.com/$repo") -Label 'sc.exe description' | Out-Null
    Invoke-Sc -ScArgs @('failure', $svcName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') -Label 'sc.exe failure' | Out-Null
} catch { Write-Warn2 "Could not set optional service metadata/recovery: $_" }

# This helper lives outside Agent so the service can replace its own directory safely.
# It resolves a concrete release, verifies the release installer with SHA256SUMS, pins
# GSC_VERSION to that exact tag, and executes only the verified local file.
$updateHelper = Join-Path $installRt 'Update-GameServerControl.ps1'
$updateHelperContent = @'
param([switch]$Silent, [switch]$FromTray, [switch]$ElevatedChild)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

function Start-TrayAgain {
    $trayExe = Join-Path (Split-Path $PSCommandPath -Parent) 'Tray\GameServerControl.Tray.exe'
    if (Test-Path $trayExe) {
        Start-Sleep -Milliseconds 500
        Start-Process -FilePath $trayExe -ArgumentList '--url', 'http://localhost:5099/'
    }
}

function Get-ExpectedSha256 {
    param([string]$ManifestPath, [string]$AssetName)
    $found = @()
    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
            $name = $Matches[2].Trim()
            if ($name -eq $AssetName) { $found += $Matches[1].ToUpperInvariant() }
        }
    }
    if ($found.Count -ne 1) { throw "SHA256SUMS must contain exactly one checksum for $AssetName; found $($found.Count)." }
    return $found[0]
}

function Assert-ReleaseAssetHash {
    param([string]$Path, [string]$AssetName, [string]$ManifestPath)
    $expected = Get-ExpectedSha256 -ManifestPath $ManifestPath -AssetName $AssetName
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $expected) { throw "SHA-256 verification failed for $AssetName." }
}

if (-not $isElevated) {
    $flags = @('-ElevatedChild')
    if ($Silent) { $flags += '-Silent' }
    if ($FromTray) { $flags += '-FromTray' }
    $flagText = $flags -join ' '
    try {
        $p = Start-Process powershell.exe -Verb RunAs -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $flagText"
    } catch {
        if ($FromTray) { Start-TrayAgain }
        exit 1
    }
    $p.WaitForExit()
    if ($FromTray) {
        Start-TrayAgain
    } elseif (-not $Silent) {
        Write-Host ''
        Read-Host 'Update finished. Press Enter to close'
    }
    exit $p.ExitCode
}

if ($Silent) { $env:GSC_SILENT_UPDATE = '1'; Start-Sleep -Seconds 3 }
if ($FromTray) { $env:GSC_FROM_TRAY = '1'; Start-Sleep -Seconds 2 }
$repo = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'Nereziel/GameServerControl' }
$requestedTag = $env:GSC_VERSION
$apiUrl = if ($requestedTag) {
    "https://api.github.com/repos/$repo/releases/tags/$requestedTag"
} else {
    "https://api.github.com/repos/$repo/releases/latest"
}
$headers = @{ 'User-Agent' = 'gsc-updater' }
$release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
$tag = [string]$release.tag_name
if ([string]::IsNullOrWhiteSpace($tag)) { throw 'Resolved release does not contain a tag name.' }
if ($requestedTag -and $tag -ne $requestedTag) { throw "Resolved release tag '$tag' does not match requested tag '$requestedTag'." }

$installerAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Install-Windows.ps1' } | Select-Object -First 1
$checksumAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' } | Select-Object -First 1
if (-not $installerAsset) { throw 'Release asset not found: GameServerControl-Install-Windows.ps1' }
if (-not $checksumAsset) { throw 'Release asset not found: SHA256SUMS' }

$tmpRoot = Join-Path $env:TEMP "gsc-update-$(Get-Random -Maximum 99999)"
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
try {
    $installerPath = Join-Path $tmpRoot 'GameServerControl-Install-Windows.ps1'
    $manifestPath = Join-Path $tmpRoot 'SHA256SUMS'
    Invoke-WebRequest -Uri $installerAsset.browser_download_url -OutFile $installerPath
    Invoke-WebRequest -Uri $checksumAsset.browser_download_url -OutFile $manifestPath
    Assert-ReleaseAssetHash -Path $installerPath -AssetName $installerAsset.name -ManifestPath $manifestPath

    $env:GSC_REPO = $repo
    $env:GSC_VERSION = $tag
    $installRoot = Split-Path $PSCommandPath -Parent
    if (-not $env:GSC_INSTALL) { $env:GSC_INSTALL = $installRoot }

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$installerPath`"")
    $installerProcess = Start-Process powershell.exe -PassThru -Wait -ArgumentList ($arguments -join ' ')
    if ($installerProcess.ExitCode -ne 0) { throw "Verified release installer exited with code $($installerProcess.ExitCode)." }
} finally {
    Remove-Item $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# A tray started from an elevated installer can launch this helper already elevated. In that
# case there is no non-elevated parent waiting to restart it, so restart it here. When an
# ordinary non-elevated parent exists, it owns the restart and this child stays out of the way.
if ($FromTray -and -not $ElevatedChild) {
    Start-TrayAgain
}
'@
Set-Content -LiteralPath $updateHelper -Value $updateHelperContent -Encoding UTF8
Write-Ok "Updater helper: $updateHelper"

$expectedVersion = if ($tag.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $tag.Substring(1) } else { $tag }
$healthUrl = Get-LocalAgentHealthUrl -Bind ([string]$cfgObj.Agent.Bind)
$updateHealthTokenPath = Join-Path $agentDir '.update-health-token'
$updateHealthToken = New-UpdateHealthToken -Path $updateHealthTokenPath
Set-AgentDirectoryAcl -Path $agentDir
Write-Skip 'Prepared restricted health-only update credential.'

Write-Step "Starting $svcName"
try {
    Start-Service -Name $svcName
    (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
} catch { Write-Warn2 "Service start did not reach Running before health verification: $($_.Exception.Message)" }

$healthPassed = Test-AgentUpdateHealth -Url $healthUrl -Token $updateHealthToken -ExpectedVersion $expectedVersion -TimeoutSeconds 30 -RequiredConsecutiveSuccesses 3
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
}

if (-not $silentUpdate) {
    Write-Step 'Refreshing desktop shortcuts'
    $userDesktop = [Environment]::GetFolderPath('DesktopDirectory')
    $legacyShortcut = Join-Path $userDesktop 'GameServerControl.lnk'
    if (Test-Path $legacyShortcut) { Remove-Item $legacyShortcut -Force -ErrorAction SilentlyContinue }

    $webShortcut = Join-Path $userDesktop 'GameServerControl.url'
    @"
[InternetShortcut]
URL=http://localhost:5099/
IconFile=$svcExePath
IconIndex=0
"@ | Set-Content -LiteralPath $webShortcut -Encoding ASCII
    Write-Ok "Web shortcut: $webShortcut"

    $sh = New-Object -ComObject WScript.Shell
    $updateShortcutPath = Join-Path $userDesktop 'Update GameServerControl.lnk'
    $upd = $sh.CreateShortcut($updateShortcutPath)
    $upd.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $upd.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$updateHelper`""
    $upd.WorkingDirectory = $installRt
    $upd.IconLocation = "$svcExePath,0"
    $upd.Description = 'Update GameServerControl Agent/API/Web UI/Tray'
    $upd.Save()
    Write-Ok "Updater shortcut: $updateShortcutPath"

    if (Test-Path $trayExe) {
        Write-Step 'Configuring tray helper autostart'
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        New-Item -Path $runKey -Force | Out-Null
        Set-ItemProperty -Path $runKey -Name 'GameServerControlTray' -Value "`"$trayExe`" --url `"http://localhost:5099/`""
        Write-Ok 'Tray helper will start when this user signs in.'

        if (-not $fromTray) {
            try {
                Get-Process -Name 'GameServerControl.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Process -FilePath $trayExe -ArgumentList '--url', 'http://localhost:5099/'
                Write-Ok 'Tray helper started.'
            } catch { Write-Warn2 "Could not start tray helper now: $_" }
        } else {
            Write-Skip 'Tray update: updater helper will restart the tray helper.'
        }
    }

    Write-Step 'API token access'
    Write-Ok 'Open the tray menu > Show Agent API token to view/copy the local token.'
    Write-Ok 'Open the tray menu > Manage API tokens to create, replace or revoke additional tokens.'
    Write-Skip 'API tokens are not written to installer logs or console output.'
}

try { Remove-Item $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
Write-Host ''
Write-Host "Installed $tag."
Write-Host "  Service: $svcName"
Write-Host '  Web UI:  http://localhost:5099/'
Write-Host "  Config:  $appSettings"
if (Test-Path $trayExe) { Write-Host "  Tray:    $trayExe" }
Write-Host ''

