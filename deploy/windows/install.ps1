# GameServerControl — Windows installer / updater (Agent + API + Web UI + optional tray helper).
#
# Run from an elevated PowerShell:
#   iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex
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
    Write-Host 'Open PowerShell as Administrator and run:' -ForegroundColor Yellow
    Write-Host '  iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex' -ForegroundColor Gray
    return
}

$repo      = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'Nereziel/GameServerControl' }
$installRt = if ($env:GSC_INSTALL) { $env:GSC_INSTALL } else { 'C:\GameServerControl' }
$agentDir  = Join-Path $installRt 'Agent'
$trayDir   = Join-Path $installRt 'Tray'
$trayExe   = Join-Path $trayDir 'GameServerControl.Tray.exe'
$legacyClientDir = Join-Path $installRt 'Client'
$pinnedTag = $env:GSC_VERSION
$svcName   = 'GameServerControlAgent'
$silentUpdate = -not [string]::IsNullOrWhiteSpace($env:GSC_SILENT_UPDATE)
$fromTray = -not [string]::IsNullOrWhiteSpace($env:GSC_FROM_TRAY)

function Write-Step($msg) { Write-Host ''; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "    -- $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg){ Write-Host "    !! $msg" -ForegroundColor Yellow }

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
Write-Ok "Tag: $($release.tag_name)  ·  Published: $($release.published_at)"

$asset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Agent-windows.zip' } | Select-Object -First 1
if (-not $asset) { throw 'Release asset not found: GameServerControl-Agent-windows.zip' }
$trayAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Tray-windows.zip' } | Select-Object -First 1
$installTray = (-not $silentUpdate) -and ($null -ne $trayAsset)

Write-Step 'Downloading Agent + Web UI'
$tmpRoot = Join-Path $env:TEMP "gsc-install-$(Get-Random -Maximum 99999)"
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$agentZip = Join-Path $tmpRoot 'agent.zip'
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $agentZip
Write-Ok 'Agent package downloaded.'

$trayZip = Join-Path $tmpRoot 'tray.zip'
if ($installTray) {
    Invoke-WebRequest -Uri $trayAsset.browser_download_url -OutFile $trayZip
    Write-Ok 'Tray helper package downloaded.'
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

Write-Step "Installing Agent to $agentDir"
$preserve = @('appsettings.json', 'servers.json', 'tokens.json')
$backupDir = Join-Path $tmpRoot 'preserved'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
foreach ($f in $preserve) {
    $src = Join-Path $agentDir $f
    if (Test-Path $src) {
        Copy-Item $src $backupDir -Force
        Write-Skip "Preserving $f"
    }
}

if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
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
# When called from Tray, a non-elevated parent waits for the elevated installer and then
# starts the new Tray binary again at normal user integrity.
$updateHelper = Join-Path $installRt 'Update-GameServerControl.ps1'
$updateHelperContent = @'
param([switch]$Silent, [switch]$FromTray)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isElevated) {
    $flags = @()
    if ($Silent) { $flags += '-Silent' }
    if ($FromTray) { $flags += '-FromTray' }
    $flagText = $flags -join ' '
    try {
        $p = Start-Process powershell.exe -Verb RunAs -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $flagText"
    } catch { exit 1 }
    $p.WaitForExit()

    if ($FromTray) {
        $trayExe = Join-Path (Split-Path $PSCommandPath -Parent) 'Tray\GameServerControl.Tray.exe'
        if (Test-Path $trayExe) {
            Start-Process -FilePath $trayExe -ArgumentList '--url', 'http://localhost:5099/'
        }
    } elseif (-not $Silent) {
        Write-Host ''
        Read-Host 'Update finished. Press Enter to close'
    }
    exit $p.ExitCode
}

if ($Silent) { $env:GSC_SILENT_UPDATE = '1'; Start-Sleep -Seconds 3 }
if ($FromTray) { $env:GSC_FROM_TRAY = '1'; Start-Sleep -Seconds 2 }
$repo = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'Nereziel/GameServerControl' }
$env:GSC_REPO = $repo
$installer = "https://raw.githubusercontent.com/$repo/main/deploy/windows/install.ps1"
Invoke-WebRequest -Uri $installer -UseBasicParsing | Invoke-Expression
'@
Set-Content -LiteralPath $updateHelper -Value $updateHelperContent -Encoding UTF8
Write-Ok "Updater helper: $updateHelper"

Write-Step "Starting $svcName"
try {
    Start-Service -Name $svcName
    (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
} catch { Write-Warn2 "Service failed to start: $($_.Exception.Message)" }
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne 'Running') { Write-Warn2 'Service is not running. Inspect the Application Event Log or run the Agent EXE directly.' }
else { Write-Ok 'Service running.' }

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
            Write-Skip 'Tray update: non-elevated updater parent will restart the tray helper.'
        }
    }

    Write-Step 'Reading the persisted API token'
    try {
        $cfg = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
        $apiToken = [string]$cfg.Agent.ApiToken
        if ($apiToken -and $apiToken -ne 'REPLACE_WITH_32_CHARS+' -and $apiToken.Length -ge 16) {
            Write-Host ''
            Write-Host '   ----- AGENT API TOKEN (keep this private) -----' -ForegroundColor Yellow
            Write-Host "   $apiToken"
            Write-Host '   -----------------------------------------------' -ForegroundColor Yellow
        } else { Write-Warn2 "API token is still a placeholder. Check $appSettings after startup." }
    } catch { Write-Warn2 "Could not read API token from $appSettings : $_" }
}

try { Remove-Item $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue } catch { }
Write-Host ''
Write-Host "Installed $($release.tag_name)."
Write-Host "  Service: $svcName"
Write-Host '  Web UI:  http://localhost:5099/'
Write-Host "  Config:  $appSettings"
if (Test-Path $trayExe) { Write-Host "  Tray:    $trayExe" }
Write-Host ''
