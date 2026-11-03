from pathlib import Path

p = Path('README.md')
text = p.read_text()

replacements = {
'''GameServerControl is a self-hosted game-server control panel built around a single **Agent + HTTP API + Web UI**. There is no desktop client requirement: install the Agent on the game-server host and manage it from a browser. Windows can optionally run a small tray helper for local service controls and opening the browser dashboard.''':
'''GameServerControl is a self-hosted game-server control panel built around an **Agent + HTTP API + Web UI**. There is no desktop client requirement: install the Agent on the game-server host and manage it from a browser. On Windows, the privileged Agent acts as the host control service while each BareMetal game server runs through its own low-privilege Runner service identity. Windows can optionally run a small tray helper for local Agent controls and opening the browser dashboard.''',
'''GameServerControl Agent
   ├─ server lifecycle
   ├─ SteamCMD installs / updates
   ├─ backups
   ├─ schedules
   ├─ game configuration
   ├─ live logs / supported consoles
   ├─ mod management
   └─ authenticated API
''':
'''GameServerControl Agent (Windows: privileged control service)
   ├─ host/service/firewall/update control
   ├─ SteamCMD installs / updates
   ├─ backups, schedules and game configuration
   ├─ mod management
   ├─ authenticated API + live status
   └─ Windows BareMetal
        └─ per-server GameServerControl Runner (low-privilege virtual account)
             └─ game process / mods
''',
'''Run PowerShell as Administrator:

```powershell
iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex
```
''':
'''Run PowerShell as Administrator. Download `GameServerControl-Install-Windows.ps1` and `SHA256SUMS` from the **same GitHub release**, verify the installer against the release manifest, then execute the verified local file:

```powershell
$line = Get-Content .\\SHA256SUMS | Where-Object { $_ -match 'GameServerControl-Install-Windows\\.ps1$' } | Select-Object -First 1
if (-not $line) { throw 'Installer hash is missing from SHA256SUMS.' }
$expected = ($line -split '\\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash .\\GameServerControl-Install-Windows.ps1 -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Installer SHA-256 mismatch.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\GameServerControl-Install-Windows.ps1
```

Do not pipe the mutable `main` branch installer directly into `Invoke-Expression`; release updates are intended to stay bound to a specific release and its SHA-256 manifest.
''',
'''C:\\GameServerControl\\Agent
C:\\GameServerControl\\Tray
C:\\GameServers\\<game>
''':
'''C:\\GameServerControl\\Agent
C:\\GameServerControl\\Runner
C:\\GameServerControl\\Tray
C:\\ProgramData\\GameServerControl\\Runners\\<server-hash>
C:\\GameServers\\<game>
''',
'''- downloads the self-contained Agent release,
- installs/configures private SteamCMD,
- registers `GameServerControlAgent` as a Windows service,
- installs the small Windows tray helper when the release contains it,
''':
'''- downloads and SHA-256 verifies the self-contained Agent and Runner release payloads,
- installs/configures private SteamCMD,
- registers `GameServerControlAgent` as the privileged Windows control service,
- installs the shared Runner payload; the Agent provisions one `GameServerControlRunner_<hash>` virtual-account service per Windows BareMetal server when needed,
- installs the small Windows tray helper when the release contains it,
''',
'''The Agent checks GitHub Releases for a newer version. On Windows, an Admin user can start an update directly from the Web UI. The Agent launches an updater process outside its own install directory, returns the HTTP response, then the external updater stops and replaces the Agent and starts the service again. Running game-server processes are not intentionally stopped by a GameServerControl self-update.''':
'''The Agent checks GitHub Releases for a newer version. On Windows, an Admin user can start an update directly from the Web UI. The Agent launches an updater process outside its own install directory, returns the HTTP response, then the external updater replaces the verified Agent and Runner payloads as one update unit. Runner services that were active before the update are stopped for payload replacement and restarted afterward; a failed health/restart gate restores the previous Agent and Runner payloads together.''',
'''Current releases contain the Agent/Web application, the optional Linux VPS Relay, and Windows releases additionally contain the tray helper:

```text
GameServerControl-Agent-windows.zip
GameServerControl-Tray-windows.zip
GameServerControl-Agent-linux.tar.gz
GameServerControl-Relay-linux.tar.gz
GameServerControl-Install-Windows.ps1
GameServerControl-Install-Linux.sh
GameServerControl-Install-Relay.sh
```
''':
'''Current releases contain the Agent/Web application, the optional Linux VPS Relay, and Windows releases additionally contain the low-privilege Runner payload and tray helper:

```text
GameServerControl-Agent-windows.zip
GameServerControl-Runner-windows.zip
GameServerControl-Tray-windows.zip
GameServerControl-Agent-linux.tar.gz
GameServerControl-Relay-linux.tar.gz
GameServerControl-Install-Windows.ps1
GameServerControl-Install-Linux.sh
GameServerControl-Install-Relay.sh
SHA256SUMS
```
'''
}

for old, new in replacements.items():
    if old not in text:
        raise SystemExit(f'expected README fragment not found: {old[:100]!r}')
    text = text.replace(old, new, 1)

p.write_text(text)
