# GameServerControl — Windows installer / updater (one-liner friendly).
#
# Run from an elevated PowerShell:
#   iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex
#
# What it does (idempotent — re-run to update):
#   1. Ensures .NET 8 Desktop Runtime is present (downloads + installs if missing).
#   2. Hits the GitHub Releases API for the latest release from this fork.
#   3. Downloads GameServerControl-Agent-windows.zip + GameServerControl-Client-windows.zip.
#   4. Stops the GameServerControlAgent service if it's running.
#   5. Extracts agent to C:\GameServerControl\Agent, client to C:\GameServerControl\Client.
#   6. Preserves existing appsettings.json + servers.json + tokens.json (treats them as user data).
#   7. Creates appsettings.json from the shipped example on first install.
#   8. Ensures a private SteamCMD is available and stores Agent:SteamCmdPath.
#   9. Registers + starts the Windows service.
#  10. Creates / refreshes a desktop shortcut pointing at the new client exe.
#  11. Prints the persisted API token from appsettings.json.
#
# Override variables (set before piping into iex):
#   $env:GSC_REPO       — owner/name (default: Nereziel/GameServerControl)
#   $env:GSC_INSTALL    — install root (default: C:\GameServerControl)
#   $env:GSC_VERSION    — pin a specific tag like v1.0.7 (default: latest)

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# ---- Elevation check ----
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    Write-Host ""
    Write-Host "===============================================================" -ForegroundColor Red
    Write-Host "  This installer must run as Administrator." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Right-click Start -> 'Terminal (Admin)' or 'PowerShell (Admin)'," -ForegroundColor Yellow
    Write-Host "  then re-run the install command:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host '    iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex' -ForegroundColor Gray
    Write-Host ""
    Write-Host "  Why: registering a Windows service (sc.exe create) requires elevation." -ForegroundColor Yellow
    Write-Host "===============================================================" -ForegroundColor Red
    return
}

# ---- Configuration ----
$repo       = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'Nereziel/GameServerControl' }
$installRt  = if ($env:GSC_INSTALL) { $env:GSC_INSTALL } else { 'C:\GameServerControl' }
$agentDir   = Join-Path $installRt 'Agent'
$clientDir  = Join-Path $installRt 'Client'
$pinnedTag  = $env:GSC_VERSION
$svcName    = 'GameServerControlAgent'

function Write-Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "    -- $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg){ Write-Host "    !! $msg" -ForegroundColor Yellow }

Write-Host ""
Write-Host "GameServerControl installer (Windows)"
Write-Host "-------------------------------------"
Write-Host "Repository: $repo"

# ---- .NET 8 Desktop Runtime check ----
Write-Step ".NET 8 Desktop Runtime"
$hasDesktop = & dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 8\.'
if (-not $hasDesktop) {
    Write-Warn2 "Missing — downloading Microsoft's bootstrapper…"
    $dotnetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'
    $dotnetExe = Join-Path $env:TEMP 'gsc-dotnet8-desktop.exe'
    Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetExe
    Write-Step "Installing .NET 8 Desktop Runtime (quiet)"
    $p = Start-Process -FilePath $dotnetExe -ArgumentList '/install', '/quiet', '/norestart' -PassThru -Wait
    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) {
        throw ".NET installer exited $($p.ExitCode). Aborting."
    }
    Write-Ok ".NET 8 Desktop Runtime installed."
} else {
    Write-Ok "Already present."
}

# ---- Resolve release ----
Write-Step "Resolving release from GitHub"
$apiUrl = if ($pinnedTag) {
    "https://api.github.com/repos/$repo/releases/tags/$pinnedTag"
} else {
    "https://api.github.com/repos/$repo/releases/latest"
}
$headers = @{ 'User-Agent' = 'gsc-installer' }
try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
} catch {
    throw "Could not resolve a GitHub release from $repo. Create a release first, or set GSC_VERSION to an existing tag. $($_.Exception.Message)"
}
Write-Ok "Tag: $($release.tag_name)  ·  Published: $($release.published_at)"

function Get-AssetUrl($name) {
    $a = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $a) { throw "Release asset not found: $name" }
    return $a.browser_download_url
}

$agentZipUrl  = Get-AssetUrl 'GameServerControl-Agent-windows.zip'
$clientZipUrl = Get-AssetUrl 'GameServerControl-Client-windows.zip'

# ---- Download to temp ----
Write-Step "Downloading release assets"
$tmpRoot = Join-Path $env:TEMP "gsc-install-$(Get-Random -Maximum 99999)"
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$agentZip  = Join-Path $tmpRoot 'agent.zip'
$clientZip = Join-Path $tmpRoot 'client.zip'
Invoke-WebRequest -Uri $agentZipUrl  -OutFile $agentZip;  Write-Ok "Agent zip"
Invoke-WebRequest -Uri $clientZipUrl -OutFile $clientZip; Write-Ok "Client zip"

# ---- Stop existing service if any ----
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Step "Stopping existing $svcName service"
    Stop-Service -Name $svcName -Force
    Start-Sleep -Seconds 2
    Write-Ok "Stopped."
}

# ---- Extract (preserving user data files) ----
Write-Step "Installing agent to $agentDir"
$preserve = @('appsettings.json', 'servers.json', 'tokens.json')
$backupDir = Join-Path $tmpRoot 'preserved'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
foreach ($f in $preserve) {
    $src = Join-Path $agentDir $f
    if (Test-Path $src) { Copy-Item $src $backupDir -Force; Write-Skip "Preserving $f" }
}
if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
Expand-Archive -Path $agentZip -DestinationPath $agentDir -Force
foreach ($f in $preserve) {
    $src = Join-Path $backupDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $agentDir $f) -Force }
}

# Fresh install: the release intentionally ships a template, not a credential-bearing config.
$appSettings = Join-Path $agentDir 'appsettings.json'
$appSettingsExample = Join-Path $agentDir 'appsettings.json.example'
if (-not (Test-Path $appSettings)) {
    if (Test-Path $appSettingsExample) {
        Copy-Item $appSettingsExample $appSettings -Force
        Write-Ok "Created appsettings.json from template."
    } else {
        throw "Fresh install is missing appsettings.json.example; release artifact is incomplete."
    }
}
Write-Ok "Agent unpacked."

# ---- Private SteamCMD ----
# Server creation already knows how to bootstrap SteamCMD itself, but the legacy update path
# reads Agent:SteamCmdPath. Install a private copy up front so updates also work immediately,
# including for servers discovered on disk rather than installed through GSC.
Write-Step "SteamCMD"
$steamCmdDir = Join-Path $agentDir 'SteamCMD'
$steamCmdExe = Join-Path $steamCmdDir 'steamcmd.exe'
if (-not (Test-Path $steamCmdExe)) {
    New-Item -ItemType Directory -Force -Path $steamCmdDir | Out-Null
    $steamZip = Join-Path $tmpRoot 'steamcmd.zip'
    Invoke-WebRequest -Uri 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile $steamZip
    Expand-Archive -Path $steamZip -DestinationPath $steamCmdDir -Force
    if (-not (Test-Path $steamCmdExe)) { throw "SteamCMD extraction completed but steamcmd.exe is missing: $steamCmdExe" }
    Write-Ok "SteamCMD installed to $steamCmdExe"
} else {
    Write-Ok "SteamCMD already present."
}

try {
    $cfgObj = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
    if (-not $cfgObj.Agent) { throw "Agent section missing in appsettings.json" }
    $cfgObj.Agent | Add-Member -NotePropertyName 'SteamCmdPath' -NotePropertyValue $steamCmdExe -Force
    $cfgObj.Agent | Add-Member -NotePropertyName 'Repo' -NotePropertyValue $repo -Force
    $cfgObj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $appSettings -Encoding UTF8
    Write-Ok "Configured Agent:SteamCmdPath."
} catch {
    throw "Failed to update $appSettings with SteamCMD path: $_"
}

Write-Step "Installing client to $clientDir"
if (Test-Path $clientDir) { Remove-Item $clientDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $clientDir | Out-Null
Expand-Archive -Path $clientZip -DestinationPath $clientDir -Force
Write-Ok "Client unpacked."

# ---- Register Windows service ----
$svcExePath = Join-Path $agentDir 'GameServerControl.Agent.exe'
if (-not (Test-Path $svcExePath)) { throw "Agent executable missing after extraction: $svcExePath" }
$existing   = Get-Service -Name $svcName -ErrorAction SilentlyContinue

function Invoke-Sc {
    param([string[]]$ScArgs, [string]$Label)
    $out = & sc.exe @ScArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (sc.exe exit $LASTEXITCODE): $($out -join ' | ')"
    }
    return $out
}

if ($existing) {
    Write-Step "Refreshing $svcName service"
    Invoke-Sc -ScArgs @('config', $svcName, 'binPath=', "`"$svcExePath`"") -Label 'sc.exe config' | Out-Null
    Write-Ok "Service config refreshed."
} else {
    Write-Step "Registering $svcName Windows service"
    Invoke-Sc -ScArgs @('create', $svcName,
        'binPath=', "`"$svcExePath`"",
        'start=', 'auto',
        'DisplayName=', 'GameServerControl Agent') -Label 'sc.exe create' | Out-Null
    try { Invoke-Sc -ScArgs @('description', $svcName, "Self-hosted dashboard for game-server processes (start/stop/backup/update/RCON). github.com/$repo") -Label 'sc.exe description' | Out-Null }
    catch { Write-Warn2 "Couldn't set service description: $_" }
    Write-Ok "Service registered."
}

Write-Step "Starting $svcName"
try { Invoke-Sc -ScArgs @('start', $svcName) -Label 'sc.exe start' | Out-Null }
catch { Write-Warn2 "$_" }
Start-Sleep -Seconds 3

$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "Service '$svcName' is still not registered. Check that PowerShell is elevated."
}
if ($svc.Status -ne 'Running') {
    Write-Warn2 "Service registered but not running (status: $($svc.Status)). Check the Application Event Log and agent configuration."
} else {
    Write-Ok "Service running."
}

# ---- Desktop shortcut ----
Write-Step "Refreshing desktop shortcut"
$userDesktop   = [Environment]::GetFolderPath('DesktopDirectory')
$publicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$lnkPaths = @(
    (Join-Path $userDesktop  'GameServerControl.lnk'),
    (Join-Path $publicDesktop 'GameServerControl.lnk')
)
$sh = New-Object -ComObject WScript.Shell
$clientExe = Join-Path $clientDir 'GameServerControl.exe'
if (-not (Test-Path $clientExe)) { throw "Client executable missing after extraction: $clientExe" }
foreach ($lp in $lnkPaths) {
    if ($lp -like "*\Public\*" -and -not (Test-Path $lp)) { continue }
    $lnk = $sh.CreateShortcut($lp)
    $lnk.TargetPath       = $clientExe
    $lnk.WorkingDirectory = $clientDir
    $lnk.IconLocation     = "$clientExe,0"
    $lnk.Description      = 'GameServerControl dashboard'
    $lnk.Save()
    Write-Ok "Shortcut: $lp"
}
$userLnk = Join-Path $userDesktop 'GameServerControl.lnk'
if (-not (Test-Path $userLnk)) {
    $lnk = $sh.CreateShortcut($userLnk)
    $lnk.TargetPath       = $clientExe
    $lnk.WorkingDirectory = $clientDir
    $lnk.IconLocation     = "$clientExe,0"
    $lnk.Description      = 'GameServerControl dashboard'
    $lnk.Save()
    Write-Ok "Shortcut: $userLnk"
}
Get-Item "$env:LOCALAPPDATA\IconCache.db" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# ---- Surface persisted API token ----
Write-Step "Reading the persisted API token"
try {
    $cfg = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
    $apiToken = [string]$cfg.Agent.ApiToken
    if ($apiToken -and $apiToken -ne 'REPLACE_WITH_32_CHARS+' -and $apiToken.Length -ge 16) {
        Write-Host ""
        Write-Host "   ----- AGENT API TOKEN (keep this private) -----" -ForegroundColor Yellow
        Write-Host "   $apiToken"
        Write-Host "   -----------------------------------------------" -ForegroundColor Yellow
    } else {
        Write-Warn2 "API token is still a placeholder. Check whether the agent service started successfully, then inspect $appSettings"
    }
} catch {
    Write-Warn2 "Could not read API token from $appSettings : $_"
}

Write-Host ""
Write-Host "Installed $($release.tag_name)."
Write-Host ""
Write-Host "  Service: $svcName"
Write-Host "  Web UI:  http://localhost:5099/"
Write-Host "  Client:  desktop shortcut"
Write-Host "  Config:  $agentDir\appsettings.json"
Write-Host ""
