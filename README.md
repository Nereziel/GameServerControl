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

Open **PowerShell as Administrator**. Download `GameServerControl-Install-Windows.ps1` and `SHA256SUMS` from the **same versioned GitHub Release**, then verify the downloaded installer's SHA-256 before execution. Do not pipe mutable `main` branch script text directly into `iex`.

The version-pinned bootstrap is also published as `GameServerControl-Bootstrap-Windows.ps1` in releases and included in the checksum manifest. If you use it, verify its SHA-256 in the same way before executing it with `powershell.exe -File`. The installer preserves existing Agent configuration.

Run the following in the directory containing the release installer and checksum manifest:

```powershell
$line = Get-Content .\SHA256SUMS | Where-Object { $_ -match 'GameServerControl-Install-Windows\.ps1$' } | Select-Object -First 1
if (-not $line) { throw 'Installer hash is missing from SHA256SUMS.' }
$expected = ($line -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash .\GameServerControl-Install-Windows.ps1 -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Installer SHA-256 mismatch.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\GameServerControl-Install-Windows.ps1
```

Do not run installation scripts from the mutable `main` branch through `iex`. Release artifacts are tied to a versioned tag and a SHA-256 manifest. A checksum from the same release provides integrity checking; the GitHub release itself must also be trusted.

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

The tray icon can also open the dashboard. Its right-click menu provides Agent status, Start / Stop / Restart, one contextual update action (check for updates, or install a newer release after confirmation), and tray autostart control. Service-changing actions request administrator approval when needed. Exiting the tray icon does not stop the Agent or any game server.

### Open from Tray without signing in again

When signing into the **local Windows** WebUI (http://localhost:5099),
optionally select **Remember this browser on this Windows PC for 30 days**.
The Tray icon already opens this URL: subsequent clicks automatically restore
the authorized browser session without asking for the API token again.
The first Admin login is still required. **Sign out** forgets this browser.

The browser creates a **non-exportable ECDSA P-256 private key** and keeps it
in its origin-and-port-scoped IndexedDB. The Agent stores **only the public
key and a hash of the original Admin credential** in protected state storage.
Each subsequent opening requires a signed, single-use 30-second challenge,
then receives an eight-hour browser API credential. Remembered browser
registration expires after 30 days. Revoking the original Admin token or
signing out revokes remembered access. No permanent bearer token, refresh
token or remember cookie is stored in localStorage or placed in any URL.
The feature works only on direct localhost, not Tailscale/Cloudflare or
ReadOnly access. Use the same browser profile and leave the checkbox off
on shared Windows accounts.

The first successful start generates and persists an API token in:

```text
C:\GameServerControl\Agent\appsettings.json
```

## Linux install

The Agent and Relay Linux installers verify each downloaded release archive against the
published SHA-256 manifest **before** stopping a service or extracting anything. This
protects archives in transit but does not independently authenticate the mutable
installer script itself. For higher assurance, download and verify the script from a
versioned GitHub Release rather than executing the `main` branch directly.

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

Relay game traffic stays in the Linux kernel's netfilter datapath; the .NET
service manages configuration and health only. Every 30 seconds the Relay
compares a single `iptables-save` ruleset snapshot with its last verified
snapshot instead of spawning separate `iptables -C` checks for every rule.
If the ruleset changes (including after UFW reload), it runs the original
full checks to restore missing managed rules. Route updates still apply
immediately. If `iptables-save` is absent or uses a different backend
(`legacy` vs `nf_tables`), the Relay falls back to the original full
checks. An alternative binary can be configured with
`Relay:IptablesSavePath`.

## Multiple physical computers (Multi-Agent)

Each Windows/Linux Agent independently manages its own game servers and offers a local WebUI. The **Cloudflare Worker** hosts a central panel with an encrypted D1 computer registry and reaches each PC independently over HTTPS through a forwarding-only VPS Caddy proxy and Tailscale. If PC1 is off, the dashboard still manages PC2. No `cloudflared` or main coordinator PC is required for this Cloudflare mode. The VPS **GameServerControl.Relay** stays separate and handles public game TCP/UDP ports only.

The existing **local Agent WebUI** can also coordinate remote Agents using its own encrypted `managed-hosts.json` registry; that local-only view depends on the PC hosting that WebUI.

See [Cloudflare remote management](docs/cloudflare-remote-management.md) for the deployment, D1, VPN and gateway setup, and [local Multi-Agent management](docs/multi-agent-management.md) for direct/local use.

## Web UI

The Web UI is the primary control surface. It supports:

- Create Server with curated per-game defaults
- background install jobs with persistent browser-side job history
- Discover / Add Existing
- Start / Stop / Restart
- SteamCMD server updates
- Optional **Auto-update on Start** and **Auto-update on Restart** per BareMetal Steam game (both off by default). The game must be stopped, and the existing mandatory pre-update safety backup must complete before SteamCMD runs. Failure blocks startup. Restart uses its own preference, not the Start preference.
- A prominently visible **Update** action beside Start / Stop / Restart. Clicking it opens **Backups & automation**, where the **SteamCMD update console** streams incremental progress and the final error (including SteamCMD exit codes). The log is held only in bounded Agent memory and available only to an Admin; game updates are serialized with Start, Stop and Restart.
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

## Network diagnostics and operational alerts

On a game's **Settings > Network** page, select **Run diagnostics** to inspect
configured TCP/UDP listening sockets on the local Agent host and query the
configured VPS Relay API health endpoint. This is a passive host-side check:
it does **not** verify Windows Firewall acceptance, a public forwarding rule,
game-protocol readiness or client connectivity. For Hyper-V servers, guest
socket inspection is not available from the host Agent.

Existing per-server Discord webhook notifications now also warn when an enabled
public Relay fails two consecutive health/sync cycles (roughly one minute) or
when a running game's host volume drops to **5 GiB** free. Alerts are
deduplicated until recovery. Disk alerts reset only after free space reaches
**8 GiB**. No automatic server restart is triggered. No additional webhook
configuration is needed; the existing "only failures and unexpected stops"
setting still applies.

The most recent **60 generic operational events per server** survive Agent
restarts under `Agent:DataDir/diagnostics` (or the Agent content directory
when `Agent:DataDir` is not set). Diagnostic files use hashed server IDs and
never contain webhook URLs, tokens, passwords, game logs or command output.
In-progress operation state remains session-only. Historical CPU/RAM samples
are unchanged and remain in memory.

## Optional off-machine backup copies

GameServerControl supports an **opt-in, integrity-verified** backup mirror to an
already mounted network share or off-machine filesystem. No data is sent to an
external service unless you explicitly configure its destination. By default,
only the local backup location is used.

1. Mount a trusted SMB/NFS share (or another removable/off-machine volume) on
   the computer running the Agent. Ensure the Agent service account can create
   folders and write files there. Avoid links/junctions as destination roots.
   **After mounting**, create a text file named `.gsc-offsite-ready` at the
   share root. GSC refuses to copy if this marker disappears, preventing silent
   writes to the empty local directory when a network mount is disconnected.
2. Set `Agent:OffsiteBackupRoot` in the Agent configuration to the **absolute**
   mount directory. For example:
   ```json
   { "Agent": { "OffsiteBackupRoot": "/mnt/gsc-backups" } }
   ```
   Windows may use a dedicated UNC share path or a mounted volume.
3. Restart the Agent service after updating its configuration. Take a test
   backup and verify both the local and remote files and their metadata.

GSC streams the finished local ZIP and `.meta.json` to the offsite directory,
checks SHA-256, then publishes the completed files. It never overwrites a
different remote copy and it never mirrors partial/non-restorable backups.
If mirroring fails, the local backup remains intact and the operation reports
failure. Before update/restore operations, a mirror failure also prevents the
potentially destructive operation from starting.

The offsite directory is **not automatically pruned** with local backup
retention; this protects independent recovery history. Monitor remote disk
space and periodically perform a manual restore drill. Existing backup archive
size limits and local retention settings are unchanged. Hyper-V VM checkpoint
exports and native R2/S3 API uploads are not supported by this initial mirror.

## Minecraft servers (Java and Bedrock)

Minecraft is **not** installed through SteamCMD. Select **Create server** and choose one
of the dedicated Minecraft profiles. The installer uses the normal GSC asynchronous
installation view, with explicit port, edition, version and license acceptance.

| Profile | Distribution | Default protocol | Server-side extensions |
| --- | --- | --- | --- |
| Minecraft Java – Vanilla | Official Mojang release | TCP 25565 | Vanilla datapacks |
| Minecraft Java – Paper | PaperMC **stable** release | TCP 25565 | Paper plugins in `plugins/` |
| Minecraft Java – Fabric | Fabric loader | TCP 25565 | Fabric mods in `mods/` |
| Minecraft Bedrock – Official | Official Bedrock Dedicated Server | UDP 19132 | Behavior/resource packs |

- **Java runtime:** Install a compatible Java distribution on the game-server host first.
  Configure system-wide `JAVA_HOME` or `PATH` and restart the Agent service so its
  low-privilege Runner can launch that Java executable. The installer does not
  download a JRE or change your system Java installation.
- **Java EULA:** Explicit acceptance is required in the create-server form. Only then
  does GSC write `eula=true` to the new server's `eula.txt`.
- **Bedrock license:** Explicit acceptance is required. The official Bedrock binary
  supports Windows and Ubuntu Linux 22.04+; it does not require Java.
- **Version:** Leave blank for the latest Mojang Java release or an available
  stable Paper build. For Fabric, GSC uses a stable loader and launcher for the
  chosen Minecraft version. Bedrock currently uses the latest official stable
  download only.
- **Mod/plugin marketplace:** Paper and Fabric can browse Modrinth and install
  **compatible, version-pinned** JARs. Downloads are checked against the SHA-512
  published by Modrinth. Server-only plugins/mods need a restart to take effect;
  some Fabric mods also require installation on players' clients.
- **Bedrock add-ons:** Bedrock uses behavior/resource packs, not Java JARs.
  Stop the Bedrock server after its first start, open **Mods**, and upload a
  local `.mcpack` or `.mcaddon` file. GSC safely imports pack manifests,
  copies the pack files, and links them into the active world's behavior/resource
  pack JSON. Script modules are currently excluded. A server restart applies
  the change. You can also edit existing pack settings in **Files**. Cloudflare
  proxy upload limits may apply to large add-on bundles.
- **Worlds/backups:** Java backs up `world/`, `world_nether/`,
  `world_the_end/`; Bedrock backs up `worlds/`. If you change Java
  `level-name`, update the registered save directories to match.
- **Console:** The GSC server workspace uses the Minecraft stdin/stdout
  console; stopping Java/Bedrock sends `stop` first to save world data before a
  forced termination is considered.
- **Updates:** Minecraft does not use the SteamCMD update action. To change a
  server edition or Minecraft version, take a backup and use the official
  vendor's upgrade procedure. The automated in-place Minecraft updater,
  Forge/NeoForge installers and full modpack deployment remain future work.

All downloads use vendor/marketplace HTTPS origins only. GSC does not run arbitrary
installer scripts received from a public URL.

For interrupted installs, cancellation and registration recovery without
re-downloading Minecraft, see [Installation continuity and recovery](docs/installation-recovery.md).

Official documentation: [Minecraft Java server](https://www.minecraft.net/en-us/download/server),
[Bedrock Dedicated Server](https://www.minecraft.net/en-us/download/server/bedrock),
[PaperMC downloads](https://docs.papermc.io/misc/downloads-service/),
[Fabric server setup](https://fabricmc.net/use/server/).

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

The Worker validates the Access JWT, selects the Agent token that matches the mapped role, strips browser-supplied authorization/gateway identity headers, and adds the protected-origin Access service credentials. ReadOnly users may perform GET requests and SignalR negotiation, then subscribe to live logs through the authenticated hub. Log-tail POST controls and other POST/PUT/PATCH/DELETE operations remain Admin-only. The Agent repeats role enforcement as defense in depth.

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
