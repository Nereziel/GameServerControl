# GameServerControl

Self-hosted game-server control plane with an ASP.NET Core agent, a Windows WPF client, and a Cloudflare Worker web dashboard.

The agent manages dedicated servers on Windows, Linux, and macOS. It can install Steam dedicated servers with SteamCMD, discover existing installations, start/stop/restart processes, create backups, schedule maintenance, edit supported game configuration, manage selected mod ecosystems, use RCON where implemented, and send Discord lifecycle notifications.

This fork is maintained at `Nereziel/GameServerControl` and carries additional deployment hardening plus RuneScape: Dragonwilds support.

## Supported games

Current presets include:

- Valheim
- RuneScape: Dragonwilds
- Satisfactory
- Palworld
- ARK: Survival Ascended
- ARK: Survival Evolved
- Rust
- V Rising
- Conan Exiles
- Squad
- Soulmask
- 7 Days to Die
- Terraria
- Don't Starve Together
- Project Zomboid
- Minecraft Java
- Windrose

Not every game has every integration. Install/start/stop/update/backup support is broader than config editing, RCON, or mod-marketplace support.

## Architecture

For a private LAN/Tailscale deployment the desktop client can connect directly to an agent.

For Internet-facing web administration, this fork uses a Cloudflare Worker as the public frontend and API boundary:

```text
Browser
  -> Cloudflare Worker (static dashboard + /api proxy)
  -> HTTPS VPS gateway
  -> Tailscale
  -> GameServerControl Agent
  -> dedicated game server
```

The GameServerControl bearer token is stored as a Cloudflare Worker secret and is not exposed to browser JavaScript. The VPS gateway is expected to validate a second gateway secret before forwarding requests over Tailscale. The GSC agent port itself should not be publicly forwarded.

See [`worker/README.md`](worker/README.md) for the Worker/gateway contract.

## Windows install

A release must exist before using the one-line installer, because the installer downloads the release artifacts rather than building from source.

Run from an elevated PowerShell:

```powershell
iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1 | iex
```

The installer:

- installs the .NET 8 Desktop Runtime when required,
- downloads the Windows agent and client from the latest `Nereziel/GameServerControl` release,
- preserves `appsettings.json`, `servers.json`, and `tokens.json` during upgrades,
- creates `appsettings.json` from the shipped template on first install,
- installs a private SteamCMD copy for reliable game updates,
- registers and starts the Windows service,
- creates the desktop-client shortcut,
- prints the persisted first-run API token.

Client only:

```powershell
iwr https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install-client.ps1 | iex
```

## Linux install

```bash
curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
```

The Linux installer uses a dedicated `gsc` service account by default and installs a private SteamCMD copy.

## macOS install

```bash
curl -fsSL https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/macos/install-from-release.sh | bash
```

The macOS agent runs as a user-scoped LaunchAgent.

## Agent defaults and security

The default listener is intentionally loopback-only:

```text
http://127.0.0.1:5099
```

Change `Agent:Bind` only when the agent must be reachable directly on a LAN or Tailnet. Do not bind it to a public-facing interface without an appropriate private network/reverse-proxy design.

On first boot the placeholder API token is replaced with a cryptographically random token and persisted to `appsettings.json`. Additional Admin/ReadOnly tokens can be managed through the token API/client. Write operations require an Admin token.

Optional HTTPS uses a generated self-signed certificate whose SANs are derived from the current host rather than a fixed Tailscale address.

## Cloudflare Worker dashboard

The Worker project lives in `worker/` and uses Node.js 24 + Wrangler.

```bash
cd worker
npm install
npm run dev
```

Deployment requires these Worker secrets:

```text
GATEWAY_BASE_URL
GATEWAY_SECRET
WINDOWS_AGENT_TOKEN
```

The initial Worker dashboard supports status polling and the common Start, Stop, Restart, Backup, and Update actions for the Windows node. The backend route format is `/api/windows/...`; adding an Ubuntu node later follows the same model with a separate agent token and VPS route.

Cloudflare Access should protect the Worker/custom domain before it is used as a public administration panel.

## RuneScape: Dragonwilds

Dragonwilds dedicated-server support uses SteamCMD App ID `4019830`.

Windows preset:

```text
Executable: RSDragonwilds.exe
Arguments:  -log -NewConsole
Saves:      RSDragonwilds\Saved\Savegames
Config:     RSDragonwilds\Saved\Config\WindowsServer\DedicatedServer.ini
Log:        RSDragonwilds\Saved\Logs\RSDragonwilds.log
```

The config editor handles these values while preserving unknown keys in the INI file:

```text
OwnerId
ServerName
DefaultWorldName
AdminPassword
WorldPassword
```

Stop Dragonwilds before changing its dedicated-server configuration because the game can rewrite the file while running.

## SteamCMD installation and update

Create Server uses `SteamCmdManager`, which can locate or download SteamCMD automatically. Release installers also establish a stable SteamCMD path so an existing/discovered server can use the Update action immediately.

After a new Steam installation completes, the agent verifies that the expected server executable actually exists before registering the server. This prevents a stale game preset from being reported as a successful install.

## Discovery

The agent scans Steam libraries and common server roots. On Windows these include locations such as:

```text
C:\GameServers
D:\GameServers
C:\Servers
D:\Servers
```

Linux/macOS have corresponding common server paths. Already configured installations are marked so they are not accidentally registered twice.

## Backups

Bare-metal backups are ZIP archives of each server's configured save directories. Backup retention is configurable and defaults to keeping the newest ten archives. Restore creates a pre-restore safety backup before replacing save files.

Hyper-V-hosted servers use VM checkpoints instead of ZIP backups.

## Mods

Mod support is game-specific rather than pretending every ecosystem behaves the same way. Current code contains managers/integrations for Valheim/BepInEx/Thunderstore, Satisfactory, Palworld curated sources, and ARK Workshop scenarios. Games without a registered manager return an explicit unsupported reason instead of silently pretending installation succeeded.

Dragonwilds currently has no dedicated mod-manager integration in this fork.

## RCON

RCON is implemented behind a pluggable interface. Palworld/Source-style RCON is currently integrated; other games can add their own dialect without changing the dashboard contract.

## Multiple instances

Bare-metal process detection prefers an exact executable path. If multiple same-name server processes exist and an exact path cannot be established, the agent refuses to guess rather than targeting an arbitrary instance.

## CI and releases

GitHub Actions builds/publishes Windows and Linux artifacts on normal CI. The release workflow additionally builds macOS artifacts and attaches the platform packages to tagged GitHub releases.

GitHub Actions use Node 24-compatible action versions. Cloudflare Worker validation uses Node.js 24 and performs a Wrangler dry-run build.

Dependabot is intentionally conservative:

- NuGet minor/patch updates are grouped.
- NuGet major updates are ignored until an explicit framework migration is planned.
- Worker npm minor/patch updates are grouped; npm major updates are ignored.
- GitHub Actions updates are grouped and still have to pass CI.

## Development

Requires .NET 8 SDK.

```bash
dotnet restore
dotnet build -c Release
```

The desktop client targets `net8.0-windows` and is Windows-only. The agent targets `net8.0` and is cross-platform.

## Adding a game

For a basic Windows preset, add it to:

```text
src/GameServerControl.Client/ViewModels/GamePresets.cs
```

For discovery, add the platform executable names to:

```text
src/GameServerControl.Agent/Discovery/ServerDiscoveryService.cs
```

If the game has an editable settings file, implement `IGameConfig` and register it through the config factory/schema extension model. If it has a unique mod ecosystem, implement `IModManager`. If it uses a non-standard RCON protocol, implement `IGameRcon`.

The built-in agent Web UI is retained as a private/local fallback. The public web direction for this fork is the Worker dashboard under `worker/`.

## Repository layout

```text
src/
  GameServerControl.Shared/   shared models and schemas
  GameServerControl.Agent/    ASP.NET Core agent/service
  GameServerControl.Client/   Windows WPF dashboard
deploy/
  windows/                    Windows release installers
  linux/                      systemd installers/service
  macos/                      LaunchAgent installer
  docker/                     container deployment files
worker/                       Cloudflare Worker + static public dashboard
.github/workflows/            CI and release workflows
```

## License

MIT. See `LICENSE`.

Original project and branding credit remain with TK-ECLIPSE. This repository is a fork maintained by Nereziel for its deployment-specific changes.
