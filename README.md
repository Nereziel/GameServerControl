# GameServerControl

GameServerControl is a self-hosted game-server control panel built around an **Agent + HTTP API + Web UI**. There is no desktop client requirement: install the Agent on the game-server host and manage it from a browser. On Windows, the privileged Agent acts as the host control service while each BareMetal game server runs through its own low-privilege Runner service identity. Windows can optionally run a small tray helper for local Agent controls and opening the browser dashboard.

## Architecture

```text
Browser
   │
   ▼
GameServerControl Web UI
   │  same-origin HTTP / SignalR
   ▼
GameServerControl Agent (Windows: privileged control service)
   ├─ host/service/firewall/update control
   ├─ SteamCMD installs / updates
   ├─ backups, schedules and game configuration
   ├─ mod management
   ├─ authenticated API + live status
   └─ Windows BareMetal
        └─ per-server GameServerControl Runner (low-privilege virtual account)
             └─ game process / mods

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

## Windows install / update

Open **PowerShell as Administrator**. Install or upgrade directly to the latest published stable GitHub Release with one command:

```powershell
irm https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/bootstrap.ps1 | iex
```

The bootstrap downloads the **release installer and SHA256SUMS from the same stable release**, verifies the installer's SHA-256, then runs the verified local file. It pins the resolved tag so installer assets cannot silently switch versions during installation. Existing Agent configuration is preserved by the installer.

**Trust boundary:** the one-liner executes the small bootstrap directly from the mutable `main` branch via `iex`. The installer is hash-checked, but the bootstrap itself is **not** independently verified by this command. Use the following manual release-file method instead when you require an integrity-checked entry point:

1. Download `GameServerControl-Install-Windows.ps1` and `SHA256SUMS` from the **same GitHub Release**.
2. Run this in the directory containing both files:

```powershell
$line = Get-Content .\SHA256SUMS | Where-Object { $_ -match 'GameServerControl-Install-Windows\.ps1$' } | Select-Object -First 1
if (-not $line) { throw 'Installer hash is missing from SHA256SUMS.' }
$expected = ($line -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash .\GameServerControl-Install-Windows.ps1 -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Installer SHA-256 mismatch.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\GameServerControl-Install-Windows.ps1
```

Do not run the full mutable `main/deploy/windows/install.ps1` installer through `iex`: the bootstrap above uses release-pinned assets and verifies the actual installer before executing it.

Default locations:

```text
C:\GameServerControl\Agent
C:\GameServerControl\Runner
C:\GameServerControl\Tray
C:\ProgramData\GameServerControl\Runners\<server-hash>
C:\GameServers\<game>
```

The installer:

- downloads and SHA-256 verifies the self-contained Agent and Runner release payloads,
- installs/configures private SteamCMD,
- registers `GameServerControlAgent` as the privileged Windows control service,
- installs the shared Runner payload; the Agent provisions one `GameServerControlRunner_<hash>` virtual-account service per Windows BareMetal server when needed,
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

The Web UI reads the current authenticated identity from `/api/auth/me`. A `ReadOnly` token can inspect status, configuration, backups and supported live output, but mutation controls and interactive commands are disabled. The Agent remains the authorization authority even if a browser control is missed.

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

All `/api/*` endpoints require a bearer token. Tokens can have `Admin` or `ReadOnly` roles. State-changing operations require `Admin`; SignalR console input is also explicitly Admin-only.

Example:

```http
Authorization: Bearer <token>
```

Managed tokens created from the Tokens UI are shown in plaintext only when created. `tokens.json` stores a SHA-256 hash plus a short display prefix rather than a reusable bearer credential. Existing older plaintext managed-token records are migrated to the hashed format when loaded. The legacy bootstrap `Agent:ApiToken` in `appsettings.json` remains supported as an Admin token for backward compatibility.

Do not expose the Agent API directly to the public Internet. For remote administration use Tailscale or an authenticated reverse proxy such as Cloudflare Access.

The optional Relay API is also bearer-token protected and is intended to be reachable over Tailscale, not the public Internet.

### Cloudflare remote administration

Cloudflare remote administration is optional. Local/LAN/Tailscale access continues to talk directly to the same Agent and keeps working independently of the Worker/Tunnel path.

Recommended paths:

```text
Local / Tailscale:
Browser -> Agent Web UI -> Agent

Remote:
Browser -> Cloudflare Access -> GameServerControl Worker -> protected Tunnel origin -> Agent
```

Use **two dedicated managed Agent tokens** for the Worker instead of reusing the bootstrap Admin token:

- `cloud-admin` with role `Admin`
- `cloud-readonly` with role `ReadOnly`

Configure the Agent so only those dedicated token IDs may attach a Cloudflare human identity to audit records:

```json
{
  "Agent": {
    "AuditRetentionDays": 90,
    "CloudGateway": {
      "TrustedTokenIds": "cloud-admin,cloud-readonly"
    }
  }
}
```

Configure these Worker secrets/variables with your own values:

```text
AGENT_ORIGIN
AGENT_ADMIN_TOKEN
AGENT_READONLY_TOKEN
ORIGIN_ACCESS_CLIENT_ID
ORIGIN_ACCESS_CLIENT_SECRET
ACCESS_TEAM_DOMAIN
ACCESS_POLICY_AUD
ACCESS_ADMIN_EMAILS
```

`ACCESS_ADMIN_EMAILS` is a comma/semicolon/newline-separated list of verified Cloudflare Access email identities that should receive the Worker-side `Admin` role. Matching is case-insensitive. Every other successfully authenticated Access user is mapped to `ReadOnly`. If `ACCESS_ADMIN_EMAILS` is empty, the gateway fails closed for writes: all Access users are ReadOnly.

The Worker validates the Access JWT, selects the Agent token that matches the mapped role, strips browser-supplied authorization/gateway identity headers, and adds the protected-origin Access service credentials. ReadOnly users may perform GET requests, SignalR negotiation and the narrow live-log subscription control; normal POST/PUT/PATCH/DELETE operations remain blocked. The Agent repeats role enforcement as defense in depth.

Protect the human-facing panel hostname with Cloudflare Access. Protect the Tunnel origin hostname separately with an Access service-token policy used only by the Worker. Do not make the Agent origin publicly reachable without Access/Tunnel protection.

Audit logs are written under the Agent audit directory (default `Logs/audit`) and retained for 90 days by default. The remote-user header is accepted only when the request is authenticated with an Agent token ID listed in `Agent:CloudGateway:TrustedTokenIds`; sending the same header with an ordinary token does not make it trusted cloud identity.

## Updates

The Agent checks GitHub Releases for a newer version. On Windows, an Admin user can start an update directly from the Web UI. The Agent launches an updater process outside its own install directory, returns the HTTP response, then the external updater replaces the verified Agent and Runner payloads as one update unit. Runner services that were active before the update are stopped for payload replacement and restarted afterward; a failed health/restart gate restores the previous Agent and Runner payloads together.

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

Current releases contain the Agent/Web application, the optional Linux VPS Relay, and Windows releases additionally contain the low-privilege Runner payload and tray helper:

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

The former WPF desktop client remains retired; browser control is the supported administration UI. The tray helper is intentionally limited to local quick actions and does not duplicate the Web UI.

## License

MIT. See `LICENSE`.
