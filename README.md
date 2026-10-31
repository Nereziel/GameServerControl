# GameServerControl

GameServerControl is a self-hosted game-server control panel built around a single **Agent + HTTP API + Web UI**. There is no desktop client requirement: install the Agent on the game-server host and manage it from a browser.

## Architecture

```text
Browser
   │
   ▼
GameServerControl Web UI
   │  same-origin HTTP / SignalR
   ▼
GameServerControl Agent
   ├─ server lifecycle
   ├─ SteamCMD installs / updates
   ├─ backups
   ├─ schedules
   ├─ game configuration
   ├─ live logs / supported consoles
   ├─ mod management
   └─ authenticated API
```

The built-in web dashboard is served by the Agent itself. For remote/public access, keep the Agent private and place Cloudflare Access / Worker / Tunnel or another authenticated reverse proxy in front of it.

## Supported hosts

- Windows Server / Windows 10/11: full Agent feature set, including Hyper-V integration where configured.
- Linux: Agent, API and Web UI; Hyper-V-specific features are unavailable.
- Release artifacts are self-contained and do not require a preinstalled .NET runtime.

## Windows install

Run PowerShell as Administrator:

```powershell
iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex
```

Default locations:

```text
C:\GameServerControl\Agent
C:\GameServers\<game>
```

The installer:

- downloads the self-contained Agent release,
- installs/configures private SteamCMD,
- registers `GameServerControlAgent` as a Windows service,
- preserves `appsettings.json`, `servers.json` and `tokens.json` during updates,
- creates a desktop shortcut to `http://localhost:5099/`,
- creates an external updater helper so the Agent can safely update itself.

After installation open:

```text
http://localhost:5099/
```

The first successful start generates and persists an API token in:

```text
C:\GameServerControl\Agent\appsettings.json
```

## Linux install

```bash
curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
```

Then open the Agent's configured HTTP address in a browser.

## Web UI

The Web UI is the primary control surface. It supports:

- Create Server with curated per-game defaults
- background install jobs with persistent browser-side job history
- Discover / Add Existing
- Start / Stop / Restart
- SteamCMD server updates
- backups and restore
- schedules
- game-aware configuration
- game-aware capability filtering (for example, RCON is hidden when unsupported)
- live log console where the game exposes a usable log
- RCON/player management where GameServerControl has a supported handler
- mods, marketplace search and mod updates where supported
- API token management
- Agent update checks and Windows self-update

## Game profiles

Per-game defaults and capabilities live in:

```text
src/GameServerControl.Shared/GameProfiles.cs
```

The Agent exposes these profiles through `/api/game-profiles`, and the Web UI consumes that API. This keeps install defaults, supported controls, paths and capabilities in one place instead of duplicating them in frontend code.

Examples of profile-controlled capabilities:

- managed configuration
- mods
- RCON
- player management
- live logs
- console type
- Hyper-V availability
- scheduled-task support
- default install/save/log paths
- whether the game is offered in the automatic installer

## API authentication

All `/api/*` endpoints require a bearer token. Tokens can have Admin or ReadOnly roles. Write operations require Admin.

Example:

```http
Authorization: Bearer <token>
```

Do not expose the Agent API directly to the public Internet. For remote administration use Tailscale or an authenticated reverse proxy such as Cloudflare Access.

## Updates

The Agent checks GitHub Releases for a newer version. On Windows, an Admin user can start an update directly from the Web UI. The Agent launches an updater process outside its own install directory, returns the HTTP response, then the external updater stops and replaces the Agent and starts the service again. Running game-server processes are not intentionally stopped by a GameServerControl self-update.

A manual fallback updater shortcut is also created by the Windows installer.

## Development

Requirements:

- .NET 8 SDK
- Node.js only for Web UI JavaScript syntax validation in CI

Build:

```bash
dotnet restore
dotnet build -c Release
```

Run Agent locally:

```bash
dotnet run --project src/GameServerControl.Agent
```

Default bind:

```text
http://127.0.0.1:5099
```

## Release layout

Current releases contain only the Agent/Web application:

```text
GameServerControl-Agent-windows.zip
GameServerControl-Agent-linux.tar.gz
GameServerControl-Install-Windows.ps1
GameServerControl-Install-Linux.sh
```

The former WPF desktop client has been retired; browser control is the supported UI.

## License

MIT. See `LICENSE`.
