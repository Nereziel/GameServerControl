# GameServerControl Windows one-line bootstrap.
# Run from an elevated PowerShell:
# irm https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/bootstrap.ps1 | iex
#
# The bootstrap fetches the installer and checksum manifest from one stable GitHub Release,
# verifies the installer SHA-256, then executes the verified local file. It never executes
# downloaded installer text with Invoke-Expression.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = ([Security.Principal.WindowsPrincipal] $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'Start PowerShell as Administrator, then run the one-line installer again.'
    }

    $repo = 'Nereziel/GameServerControl'
    $requestedTag = $env:GSC_VERSION
    if ($requestedTag -and $requestedTag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') {
        throw 'GSC_VERSION must be a stable release tag such as v1.1.3.'
    }

    $releaseUrl = if ($requestedTag) {
        "https://api.github.com/repos/$repo/releases/tags/$requestedTag"
    } else {
        "https://api.github.com/repos/$repo/releases/latest"
    }

    $release = Invoke-RestMethod -Uri $releaseUrl -Headers @{ 'User-Agent' = 'GameServerControl-Windows-Bootstrap' }
    $tag = [string]$release.tag_name
    if ($tag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$' -or ($requestedTag -and $tag -ne $requestedTag)) {
        throw 'The resolved GitHub Release does not match the requested stable version.'
    }

    $installerName = 'GameServerControl-Install-Windows.ps1'
    $manifestName = 'SHA256SUMS'
    $installerAssets = @($release.assets | Where-Object { $_.name -ceq $installerName })
    $manifestAssets = @($release.assets | Where-Object { $_.name -ceq $manifestName })
    if ($installerAssets.Count -ne 1 -or $manifestAssets.Count -ne 1) {
        throw "Release $tag must contain exactly one installer and one SHA256SUMS asset."
    }

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("gsc-bootstrap-" + [guid]::NewGuid().ToString('N'))
    $originalVersion = $env:GSC_VERSION
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        $installerPath = Join-Path $tempRoot $installerName
        $manifestPath = Join-Path $tempRoot $manifestName
        Invoke-WebRequest -Uri $installerAssets[0].browser_download_url -OutFile $installerPath -UseBasicParsing
        Invoke-WebRequest -Uri $manifestAssets[0].browser_download_url -OutFile $manifestPath -UseBasicParsing

        $expectedHashes = @(
            foreach ($line in Get-Content -LiteralPath $manifestPath) {
                if ($line -cmatch '^([0-9A-Fa-f]{64})\s+\*?GameServerControl-Install-Windows\.ps1\s*$') {
                    $Matches[1].ToUpperInvariant()
                }
            }
        )
        if ($expectedHashes.Count -ne 1) {
            throw 'SHA256SUMS must contain exactly one checksum for the Windows installer.'
        }

        $expected = $expectedHashes[0]
        $actual = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne $expected) {
            throw 'Windows installer SHA-256 mismatch. No installer code was executed.'
        }

        Write-Host "Verified Windows installer for $tag (SHA-256). Starting installation..." -ForegroundColor Green
        $env:GSC_VERSION = $tag
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath
        if ($LASTEXITCODE -ne 0) {
            throw "Windows installer exited with code $LASTEXITCODE."
        }
    } finally {
        $env:GSC_VERSION = $originalVersion
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
