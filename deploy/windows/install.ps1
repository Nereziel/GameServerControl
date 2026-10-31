# GameServerControl — Windows installer / updater (one-liner friendly).
#
# Run from an elevated PowerShell:
#   iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex
#
# Release artifacts are self-contained. No preinstalled .NET runtime is required.
#
# Override variables:
#   $env:GSC_REPO       — owner/name (default: Nereziel/GameServerControl)
#   $env:GSC_INSTALL    — install root (default: C:\GameServerControl)
#   $env:GSC_VERSION    — pin a tag like v1.0.8 (default: latest)

#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    Write-Host ''
    Write-Host 'This installer must run as Administrator.' -ForegroundColor Red
    Write-Host 'Open Terminal/PowerShell as Administrator and run:' -ForegroundColor Yellow
    Write-Host '  iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex' -ForegroundColor Gray
    return
}

$repo       = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'Nereziel/GameServerControl' }
$installRt  = if ($env:GSC_INSTALL) { $env:GSC_INSTALL } else { 'C:\GameServerControl' }
$agentDir   = Join-Path $installRt 'Agent'
$clientDir  = Join-Path $installRt 'Client'
$pinnedTag  = $env:GSC_VERSION
$svcName    = 'GameServerControlAgent'

function Write-Step($msg) { Write-Host ''; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "    -- $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg){ Write-Host "    !! $msg" -ForegroundColor Yellow }

Write-Host ''
Write-Host 'GameServerControl installer (Windows)'
Write-Host '-------------------------------------'
Write-Host "Repository: $repo"
Write-Ok 'Self-contained release — system .NET runtime is not required.'

# ---- Resolve release ----
Write-Step 'Resolving release from GitHub'
$apiUrl = if ($pinnedTag) {
    "https://api.github.com/repos/$repo/releases/tags/$pinnedTag"
} else {
    "https://api.github.com/repos/$repo/releases/latest"
}
$headers = @{ 'User-Agent' = 'gsc-installer' }
try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
} catch {
    throw "Could not resolve a GitHub release from $repo. $($_.Exception.Message)"
}
Write-Ok "Tag: $($release.tag_name)  ·  Published: $($release.published_at)"

function Get-AssetUrl($name) {
    $asset = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $asset) { throw "Release asset not found: $name" }
    $asset.browser_download_url
}

$agentZipUrl  = Get-AssetUrl 'GameServerControl-Agent-windows.zip'
$clientZipUrl = Get-AssetUrl 'GameServerControl-Client-windows.zip'

# ---- Download ----
Write-Step 'Downloading release assets'
$tmpRoot = Join-Path $env:TEMP "gsc-install-$(Get-Random -Maximum 99999)"
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$agentZip  = Join-Path $tmpRoot 'agent.zip'
$clientZip = Join-Path $tmpRoot 'client.zip'
Invoke-WebRequest -Uri $agentZipUrl  -OutFile $agentZip
Invoke-WebRequest -Uri $clientZipUrl -OutFile $clientZip
Write-Ok 'Agent zip'
Write-Ok 'Client zip'

# ---- Stop existing service ----
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Step "Stopping existing $svcName service"
    Stop-Service -Name $svcName -Force
    $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    Write-Ok 'Stopped.'
}

# ---- Install agent and preserve user data ----
Write-Step "Installing agent to $agentDir"
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
    if (-not (Test-Path $appSettingsExample)) {
        throw 'Release artifact is missing appsettings.json.example.'
    }
    Copy-Item $appSettingsExample $appSettings -Force
    Write-Ok 'Created appsettings.json from template.'
}

$svcExePath = Join-Path $agentDir 'GameServerControl.Agent.exe'
if (-not (Test-Path $svcExePath)) {
    throw "Agent executable missing after extraction: $svcExePath"
}
Write-Ok 'Agent unpacked.'

# ---- Private SteamCMD ----
Write-Step 'SteamCMD'
$steamCmdDir = Join-Path $agentDir 'SteamCMD'
$steamCmdExe = Join-Path $steamCmdDir 'steamcmd.exe'
if (-not (Test-Path $steamCmdExe)) {
    New-Item -ItemType Directory -Force -Path $steamCmdDir | Out-Null
    $steamZip = Join-Path $tmpRoot 'steamcmd.zip'
    Invoke-WebRequest -Uri 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile $steamZip
    Expand-Archive -Path $steamZip -DestinationPath $steamCmdDir -Force
}
if (-not (Test-Path $steamCmdExe)) {
    throw "SteamCMD extraction completed but steamcmd.exe is missing: $steamCmdExe"
}
Write-Ok "SteamCMD: $steamCmdExe"

try {
    $cfgObj = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
    if (-not $cfgObj.Agent) { throw 'Agent section missing in appsettings.json' }
    $cfgObj.Agent | Add-Member -NotePropertyName 'SteamCmdPath' -NotePropertyValue $steamCmdExe -Force
    $cfgObj.Agent | Add-Member -NotePropertyName 'Repo' -NotePropertyValue $repo -Force
    $cfgObj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $appSettings -Encoding UTF8
    Write-Ok 'Configured Agent:SteamCmdPath.'
} catch {
    throw "Failed to update $appSettings : $_"
}

# ---- Install client ----
# In-app updates are launched by the WPF client itself. It normally exits immediately,
# but terminate any lingering copy from our own install directory so DLL/EXE locks cannot
# make Remove-Item fail halfway through an otherwise successful update.
$runningClients = @(Get-Process -Name 'GameServerControl' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($clientDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($runningClients.Count -gt 0) {
    Write-Step 'Closing running GameServerControl client'
    foreach ($p in $runningClients) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { Write-Warn2 "Could not stop client PID $($p.Id): $_" }
    }
    Start-Sleep -Milliseconds 500
}

Write-Step "Installing client to $clientDir"
if (Test-Path $clientDir) { Remove-Item $clientDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $clientDir | Out-Null
Expand-Archive -Path $clientZip -DestinationPath $clientDir -Force
$clientExe = Join-Path $clientDir 'GameServerControl.exe'
if (-not (Test-Path $clientExe)) {
    throw "Client executable missing after extraction: $clientExe"
}
Write-Ok 'Client unpacked.'

# ---- Register/update Windows service ----
function Invoke-Sc {
    param([string[]]$ScArgs, [string]$Label)
    $out = & sc.exe @ScArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (sc.exe exit $LASTEXITCODE): $($out -join ' | ')"
    }
    $out
}

$existing = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Step "Refreshing $svcName service"
    Invoke-Sc -ScArgs @('config', $svcName, 'binPath=', "`"$svcExePath`"", 'start=', 'auto') -Label 'sc.exe config' | Out-Null
    Write-Ok 'Service config refreshed.'
} else {
    Write-Step "Registering $svcName Windows service"
    Invoke-Sc -ScArgs @(
        'create', $svcName,
        'binPath=', "`"$svcExePath`"",
        'start=', 'auto',
        'DisplayName=', 'GameServerControl Agent'
    ) -Label 'sc.exe create' | Out-Null
    Write-Ok 'Service registered.'
}

try {
    Invoke-Sc -ScArgs @('description', $svcName, "Self-hosted game-server control agent. github.com/$repo") -Label 'sc.exe description' | Out-Null
    Invoke-Sc -ScArgs @('failure', $svcName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') -Label 'sc.exe failure' | Out-Null
} catch {
    Write-Warn2 "Could not set optional service metadata/recovery: $_"
}

Write-Step "Starting $svcName"
try {
    Start-Service -Name $svcName
    (Get-Service -Name $svcName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
} catch {
    Write-Warn2 "Service failed to start: $($_.Exception.Message)"
}
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne 'Running') {
    Write-Warn2 'Service is not running. Run the agent EXE directly or inspect the Application Event Log for the startup error.'
} else {
    Write-Ok 'Service running.'
}

# ---- Desktop shortcuts + reusable updater helper ----
Write-Step 'Refreshing desktop shortcuts'
$userDesktop = [Environment]::GetFolderPath('DesktopDirectory')
$sh = New-Object -ComObject WScript.Shell

$shortcutPath = Join-Path $userDesktop 'GameServerControl.lnk'
$lnk = $sh.CreateShortcut($shortcutPath)
$lnk.TargetPath = $clientExe
$lnk.WorkingDirectory = $clientDir
$lnk.IconLocation = "$clientExe,0"
$lnk.Description = 'GameServerControl dashboard'
$lnk.Save()
Write-Ok "Shortcut: $shortcutPath"

$updateHelper = Join-Path $installRt 'Update-GameServerControl.ps1'
$updateHelperContent = @'
$ErrorActionPreference = 'Stop'
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argLine
    exit
}
$ProgressPreference = 'SilentlyContinue'
$repo = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'Nereziel/GameServerControl' }
$env:GSC_REPO = $repo
$installer = "https://raw.githubusercontent.com/$repo/main/deploy/windows/install.ps1"
Invoke-WebRequest -Uri $installer -UseBasicParsing | Invoke-Expression
Write-Host ''
Read-Host 'Update finished. Press Enter to close'
'@
Set-Content -LiteralPath $updateHelper -Value $updateHelperContent -Encoding UTF8

$updateShortcutPath = Join-Path $userDesktop 'Update GameServerControl.lnk'
$upd = $sh.CreateShortcut($updateShortcutPath)
$upd.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$upd.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$updateHelper`""
$upd.WorkingDirectory = $installRt
$upd.IconLocation = "$clientExe,0"
$upd.Description = 'Update GameServerControl Agent and Client to the latest release'
$upd.Save()
Write-Ok "Updater shortcut: $updateShortcutPath"

# ---- Surface persisted API token ----
Write-Step 'Reading the persisted API token'
try {
    $cfg = Get-Content -LiteralPath $appSettings -Raw | ConvertFrom-Json
    $apiToken = [string]$cfg.Agent.ApiToken
    if ($apiToken -and $apiToken -ne 'REPLACE_WITH_32_CHARS+' -and $apiToken.Length -ge 16) {
        Write-Host ''
        Write-Host '   ----- AGENT API TOKEN (keep this private) -----' -ForegroundColor Yellow
        Write-Host "   $apiToken"
        Write-Host '   -----------------------------------------------' -ForegroundColor Yellow
    } else {
        Write-Warn2 "API token is still a placeholder. Check $appSettings after the service starts successfully."
    }
} catch {
    Write-Warn2 "Could not read API token from $appSettings : $_"
}

Write-Host ''
Write-Host "Installed $($release.tag_name)."
Write-Host "  Service: $svcName"
Write-Host '  Web UI:  http://localhost:5099/'
Write-Host '  Client:  desktop shortcut'
Write-Host '  Updater: Update GameServerControl desktop shortcut'
Write-Host "  Config:  $appSettings"
Write-Host ''
