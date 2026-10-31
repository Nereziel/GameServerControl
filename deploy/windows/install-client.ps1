# GameServerControl — Windows CLIENT-ONLY installer (no Admin needed).
#
# Run from any PowerShell:
#   iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install-client.ps1 | iex
#
# The client release is self-contained; no preinstalled .NET runtime is required.

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$repo       = if ($env:GSC_REPO)    { $env:GSC_REPO }    else { 'Nereziel/GameServerControl' }
$installRt  = if ($env:GSC_INSTALL) { $env:GSC_INSTALL } else { Join-Path $env:LOCALAPPDATA 'GameServerControl' }
$clientDir  = Join-Path $installRt 'Client'
$pinnedTag  = $env:GSC_VERSION

function Write-Step($msg) { Write-Host ''; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }

Write-Host ''
Write-Host 'GameServerControl client installer (no admin required)'
Write-Host '------------------------------------------------------'
Write-Host "Repository: $repo"
Write-Ok 'Self-contained release — system .NET runtime is not required.'

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

$clientAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Client-windows.zip' } | Select-Object -First 1
if (-not $clientAsset) {
    throw "Release $($release.tag_name) is missing GameServerControl-Client-windows.zip."
}

Write-Step 'Downloading client'
$tmpZip = Join-Path $env:TEMP "gsc-client-$([Guid]::NewGuid().ToString('N')).zip"
Invoke-WebRequest -Uri $clientAsset.browser_download_url -OutFile $tmpZip
Write-Ok ("Downloaded (" + [Math]::Round((Get-Item $tmpZip).Length / 1MB, 2) + ' MB)')

Write-Step "Installing client to $clientDir"
if (Test-Path $clientDir) { Remove-Item $clientDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $clientDir | Out-Null
Expand-Archive -Path $tmpZip -DestinationPath $clientDir -Force
Remove-Item $tmpZip -Force

$clientExe = Join-Path $clientDir 'GameServerControl.exe'
if (-not (Test-Path $clientExe)) {
    throw "Client executable missing after extraction: $clientExe"
}
Write-Ok 'Client unpacked.'

Write-Step 'Creating desktop shortcut'
$userDesktop = [Environment]::GetFolderPath('DesktopDirectory')
$lnkPath = Join-Path $userDesktop 'GameServerControl.lnk'
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut($lnkPath)
$lnk.TargetPath = $clientExe
$lnk.WorkingDirectory = $clientDir
$lnk.IconLocation = "$clientExe,0"
$lnk.Description = 'GameServerControl dashboard'
$lnk.Save()
Write-Ok "Shortcut: $lnkPath"

Write-Host ''
Write-Host "Installed $($release.tag_name)."
Write-Host "  Install dir: $clientDir"
Write-Host '  Next:        open the client, Settings, paste your agent URL + API token.'
Write-Host ''
