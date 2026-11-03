$ErrorActionPreference = 'Stop'

$installerPath = 'deploy/windows/install.ps1'
$source = Get-Content -LiteralPath $installerPath -Raw
$marker = '$updateHelperContent = @'''
$start = $source.IndexOf($marker, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Could not find generated updater helper here-string.' }

$bodyStart = $source.IndexOf("`n", $start)
if ($bodyStart -lt 0) { throw 'Could not find generated updater helper body start.' }
$bodyStart++

$bodyEnd = $source.IndexOf("`n'@", $bodyStart, [StringComparison]::Ordinal)
if ($bodyEnd -lt 0) { throw 'Could not find generated updater helper body end.' }

$helper = $source.Substring($bodyStart, $bodyEnd - $bodyStart)
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseInput($helper, [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) {
    throw "Generated updater helper has PowerShell syntax errors:`n$((($parseErrors | ForEach-Object { $_.Message }) -join "`n"))"
}

if ($helper.Contains('Invoke-Expression', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Generated updater helper must not execute remote script text with Invoke-Expression.'
}
foreach ($required in @('SHA256SUMS', 'Get-FileHash', '$env:GSC_VERSION = $tag', 'GameServerControl-Install-Windows.ps1')) {
    if (-not $helper.Contains($required, [StringComparison]::Ordinal)) {
        throw "Generated updater helper is missing required release-pinning behavior: $required"
    }
}

Write-Host 'Generated Windows updater helper syntax/security contract OK.'

# Bootstrap is the only Windows installer entry point intended for "irm ... | iex".
# It may execute text from main, but must never execute downloaded installer text:
# installer bytes are pinned to one release and verified before powershell.exe -File.
$bootstrapPath = 'deploy/windows/bootstrap.ps1'
$bootstrapSource = Get-Content -LiteralPath $bootstrapPath -Raw
$bootstrapTokens = $null
$bootstrapErrors = $null
$bootstrapAst = [System.Management.Automation.Language.Parser]::ParseInput(
    $bootstrapSource, [ref]$bootstrapTokens, [ref]$bootstrapErrors)
if ($bootstrapErrors.Count -gt 0) {
    throw "Windows bootstrap has PowerShell syntax errors:" + (($bootstrapErrors | ForEach-Object { $_.Message }) -join '; ')
}

foreach ($required in @(
    'releases/latest',
    'releases/tags/$requestedTag',
    'SHA256SUMS',
    'GameServerControl-Install-Windows.ps1',
    'Get-FileHash',
    'if ($actual -ne $expected)',
    '$env:GSC_VERSION = $tag',
    '& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath'
)) {
    if (-not $bootstrapSource.Contains($required, [StringComparison]::Ordinal)) {
        throw "Windows bootstrap is missing a required release-verification step: $required"
    }
}

$unsafeCommands = @($bootstrapAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -in @('Invoke-Expression', 'iex')
}, $true))
if ($unsafeCommands.Count -gt 0) {
    throw 'Windows bootstrap must execute the verified installer file, never downloaded script text with iex.'
}

if ($bootstrapSource.Contains('raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1')) {
    throw 'Windows bootstrap must not download a mutable main-branch installer.'
}

Write-Host 'Windows release-verified one-line bootstrap syntax/security contract OK.'

# Regression: inherited ACL removal must not leave Agent/Runner files with empty ACLs.
# Folder inheritance grants are supplemented by explicit permissions for descendants.
$installerSource = Get-Content -LiteralPath $installerPath -Raw
$explicitFileGrant = "icacls.exe `$Path /grant '*S-1-5-18:F' '*S-1-5-32-544:F' /T /C"
if (([regex]::Matches($installerSource, [regex]::Escape($explicitFileGrant))).Count -ne 2) {
    throw 'Both Agent and Runner must grant SYSTEM/Administrators explicit file permissions.'
}
if (-not $installerSource.Contains('icacls.exe $Path /grant "${serviceAccount}:(RX)" /T /C', [StringComparison]::Ordinal)) {
    throw 'Runner payloads must have explicit read/execute rights for their service account.'
}
Write-Host 'Windows explicit Agent/Runner file ACL regression contract OK.'
