# GameServerControl

GameServerControl is a self-hosted game-server control panel built around a single **Agent + HTTP API + Web UI**. There is no desktop client requirement: install the Agent on the game-server host and manage it from a browser. Windows can optionally run a small tray helper for local service controls and opening the browser dashboard.

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

Windows tray helper (optional)
   ├─ opens the Web UI
   ├─ shows Agent service status
   ├─ Start / Stop / Restart Agent
   └─ launches GameServerControl updater

Public Linux VPS Relay (optional)
   ├─ receives desired routes from the Agent over Tailscale
   ├─ opens/closes public game forwarding with server lifecycle
   └─ DNAT + FORWARD + MASQUERADE through tailscale0
```

The built-in web dashboard is served by the Agent itself. For remote/public access, keep the Agent private and place Cloudflare Access / Worker / Tunnel or another authenticated reverse proxy in front of it.

## Supported hosts

- Windows Server / Windows 10/11: full Agent feature set, including Hyper-V integration where configured, plus the optional tray helper.
- Linux: Agent, API and Web UI; Hyper-V and Windows tray features are unavailable.
- Linux VPS: optional `GameServerControl.Relay` for exposing game ports from a public VPS through Tailscale to a GameServerControl Agent host.
- Release artifacts are self-contained and do not require a preinstalled .NET runtime.

## Windows install

Run PowerShell as Administrator:

```powershell
iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex
```

Default locations:

```text
C:\GameServerControl\Agent
C:\GameServerControl\Tray
C:\GameServers\<game>
```

The installer:

- downloads the self-contained Agent release,
- installs/configures private SteamCMD,
- registers `GameServerControlAgent` as a Windows service,
- installs the small Windows tray helper when the release contains it,
- registers the tray helper for the current user's Windows sign-in,
- preserves `appsettings.json`, `servers.json` and `tokens.json` during updates,
- creates a desktop shortcut to `http://localhost:5099/`,
- creates an external updater helper so the Agent can safely update itself.

After installation open:

```text
http://localhost:5099/
```

The tray icon can also open the dashboard. Its right-click menu provides Agent status, Start / Stop / Restart, update check, GameServerControl update and tray autostart control. Service-changing actions request administrator approval when needed. Exiting the tray icon does not stop the Agent or any game server.

The first successful start generates and persists an API token in:

```text
C:\GameServerControl\Agent\appsettings.json
```

## Linux install

```bash
curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
```

Then open the Agent's configured HTTP address in a browser.

## Public VPS Relay install

Use this on a public Linux VPS that is connected to the same Tailscale network as the GameServerControl Agent host:

```bash
curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/relay/install-from-release.sh | sudo bash
```

The Relay installer:

- downloads the self-contained `GameServerControl.Relay` release,
- detects the VPS public interface and Tailscale IPv4 address,
- enables IPv4 forwarding,
- installs and enables `gameservercontrol-relay.service`,
- binds the Relay API to the VPS Tailscale address on port `5100` by default,
- generates an API token on first install and preserves it during updates,
- manages only its own `GSC_RELAY_*` iptables chains and does not rewrite `/etc/ufw/before.rules`.

At the end of installation it prints the Relay API URL and token. Configure those on the Agent under `Agent:PublicRelay`, then enable **Public VPS relay** for the desired server in its Network workspace.

When an enabled game server starts, the Agent sends the desired route to the VPS Relay over Tailscale. The Relay creates DNAT/FORWARD/MASQUERADE rules for that server. When the server stops, the Agent removes the route again.

The VPS provider/cloud firewall must still allow the selected public game ports.

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
- Public VPS relay configuration where configured
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

The optional Relay API is also bearer-token protected and is intended to be reachable over Tailscale, not the public Internet.

## Updates

The Agent checks GitHub Releases for a newer version. On Windows, an Admin user can start an update directly from the Web UI. The Agent launches an updater process outside its own install directory, returns the HTTP response, then the external updater stops and replaces the Agent and starts the service again. Running game-server processes are not intentionally stopped by a GameServerControl self-update.

The Windows tray helper can also check GitHub Releases and launch the same updater. When the update starts from the tray, the tray exits so its executable can be replaced, then the non-elevated updater parent starts the new tray helper again after the elevated install finishes.

A manual fallback updater shortcut is also created by the Windows installer.

The Linux Relay can be updated by running the same one-line Relay install command again. Its existing API token is preserved unless explicitly overridden.

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

Current releases contain the Agent/Web application, the optional Linux VPS Relay, and Windows releases additionally contain the tray helper:

```text
GameServerControl-Agent-windows.zip
GameServerControl-Tray-windows.zip
GameServerControl-Agent-linux.tar.gz
GameServerControl-Relay-linux.tar.gz
GameServerControl-Install-Windows.ps1
GameServerControl-Install-Linux.sh
GameServerControl-Install-Relay.sh
```

The former WPF desktop client remains retired; browser control is the supported administration UI. The tray helper is intentionally limited to local quick actions and does not duplicate the Web UI.

## License

MIT. See `LICENSE`.
