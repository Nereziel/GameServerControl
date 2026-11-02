[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TunnelToken,

    [string]$AgentUrl = 'http://127.0.0.1:5099',

    [switch]$ReplaceExisting
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell session (Run as Administrator).'
    }
}

function Get-AgentEndpoint([string]$Url) {
    try {
        $uri = [Uri]$Url
    }
    catch {
        throw "AgentUrl is not a valid URL: $Url"
    }

    if ($uri.Scheme -notin @('http', 'https')) {
        throw 'AgentUrl must use http:// or https://.'
    }

    $port = $uri.Port
    if ($port -lt 1 -or $port -gt 65535) {
        throw 'AgentUrl has an invalid port.'
    }

    return @{ Host = $uri.Host; Port = $port }
}

function Test-AgentListener([string]$Url) {
    $endpoint = Get-AgentEndpoint $Url
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($endpoint.Host, $endpoint.Port)
        if (-not $task.Wait(3000)) {
            throw "Timed out connecting to $($endpoint.Host):$($endpoint.Port)."
        }
        if (-not $client.Connected) {
            throw "Nothing is listening on $($endpoint.Host):$($endpoint.Port)."
        }
    }
    finally {
        $client.Dispose()
    }
}

Assert-Administrator

if ($TunnelToken.Length -lt 32) {
    throw 'TunnelToken does not look valid. Copy the token from Cloudflare Networking > Tunnels > Add a replica.'
}

Test-AgentListener $AgentUrl
Write-Host "GameServerControl Agent listener is reachable at $AgentUrl"

$installRoot = Join-Path $env:ProgramFiles 'GameServerControl\cloudflared'
$exePath = Join-Path $installRoot 'cloudflared.exe'
$downloadPath = "$exePath.download"
$downloadUrl = 'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe'

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null

$existing = Get-Service -Name 'cloudflared' -ErrorAction SilentlyContinue
if ($existing) {
    if (-not $ReplaceExisting) {
        throw 'A cloudflared Windows service already exists. Re-run with -ReplaceExisting only if this host should use the GameServerControl tunnel token.'
    }

    Write-Host 'Removing existing cloudflared service...'
    try { Stop-Service -Name 'cloudflared' -Force -ErrorAction SilentlyContinue } catch { }

    $knownCloudflared = Get-Command cloudflared.exe -ErrorAction SilentlyContinue
    if ($knownCloudflared) {
        & $knownCloudflared.Source service uninstall | Out-Host
    }
    else {
        sc.exe delete cloudflared | Out-Host
        Start-Sleep -Seconds 1
    }
}

Write-Host 'Downloading current cloudflared for Windows amd64...'
Invoke-WebRequest -Uri $downloadUrl -OutFile $downloadPath -UseBasicParsing
if (-not (Test-Path $downloadPath) -or (Get-Item $downloadPath).Length -lt 1MB) {
    throw 'Downloaded cloudflared executable is missing or unexpectedly small.'
}
Move-Item -Force $downloadPath $exePath

Write-Host 'cloudflared version:'
& $exePath --version | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw 'cloudflared executable could not be started.'
}

Write-Host 'Installing Cloudflare Tunnel as a Windows service...'
# Cloudflare remotely-managed tunnels use the tunnel token as the connector credential.
# The service install command persists the token in the service configuration. Treat the
# tunnel token like a password and rotate it in Cloudflare if this host is compromised.
& $exePath service install $TunnelToken | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "cloudflared service install failed with exit code $LASTEXITCODE."
}

Set-Service -Name 'cloudflared' -StartupType Automatic
Start-Service -Name 'cloudflared' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$service = Get-Service -Name 'cloudflared' -ErrorAction Stop
if ($service.Status -ne 'Running') {
    throw "cloudflared service is $($service.Status), expected Running. Check Windows Event Viewer and 'sc.exe qc cloudflared'."
}

Write-Host ''
Write-Host 'Cloudflare Tunnel connector is running.'
Write-Host "Local Agent origin: $AgentUrl"
Write-Host ''
Write-Host 'Cloudflare-side route still needs to map the private origin hostname to this local service:'
Write-Host "  gsc-origin.example.com -> $AgentUrl"
Write-Host ''
Write-Host 'Protect that origin hostname with an Access Service Auth policy; do not use it as the human-facing panel hostname.'
