# Multi-Agent: manage more than one physical computer

GameServerControl uses **one Agent per physical computer**. A designated **coordinator Agent** holds encrypted connection records for other Agents. The existing WebUI and Cloudflare Worker talk to the coordinator; it proxies authorized requests to each remote Agent.

```
Browser / Cloudflare Access
          |
     Agent on PC 1 (coordinator)
          |       \\
          |        \\ HTTPS or Tailscale private network
     Local servers    Agent on PC 2
                         |
                     Remote servers
```

The coordinator does **not** move, duplicate or run processes from remote computers. Each Agent keeps its own Runner services, Tray helper, game files, update state, backups and local API. Existing installs and configurations are preserved.

## Requirements

- Install GameServerControl Agent on every Windows/Linux host. Only Windows hosts use the optional Tray and Windows Runner.
- Choose which computer is the coordinator, usually your first Agent. Keep it running whenever you need the combined dashboard.
- Make the **remote Agent** reachable from the coordinator over a trusted network. Recommended: Tailscale.
  - On the remote PC, find its Tailscale IPv4 address (`tailscale ip -4`).
  - Set `Agent:Bind` on that PC to `http://<TAILSCALE-IP>:5099` and restart the Agent service. Restrict firewall access to the Tailscale interface / intended coordinator.
  - An origin of the form `http://100.x.x.x:5099` is accepted only in the Tailscale/CGNAT range `100.64.0.0/10`. **Do not** expose this HTTP listener on a public interface; the HTTP transport relies on the encrypted private Tailscale link.
  - Alternatively use a valid, certificate-trusted `https://` Agent or reverse-proxy origin. Self-signed certificates that the coordinator does not trust are intentionally rejected; certificate verification cannot be disabled.
- The remote Agent must offer its normal authenticated `/api/auth/me`, `/api/status`, `/api/servers` and SignalR routes. Register **two distinct remote Agent managed tokens**, one with `Admin` and one with `ReadOnly`. Create these in the **remote Agent's Tokens screen**; do not reuse its bootstrap token.
- The coordinator and remote Agents should run compatible GameServerControl versions.

## Connect PC 2

1. Open the coordinator's WebUI (directly or through its Cloudflare panel).
2. In the sidebar choose **+ Connect computer**.
3. Enter an ID, e.g. `gaming-pc-2`, display name, remote Agent origin (for example `http://100.100.12.34:5099`), and the two dedicated Agent tokens.
4. Click **Connect computer**. The coordinator verifies both credentials against the remote API, including their actual roles, before saving anything.
5. Select **All computers** in the sidebar. The dashboard groups the servers by physical host and shows Running, Offline, CPU, RAM and error markers. If a remote Agent cannot be reached, its host group shows as offline.
6. Click a remote server to open the usual tabs for **Start, Stop, Restart, Console, Config, Backups, Mods, Network and Diagnostics**. The UI routes API and SignalR traffic through the coordinator to that Agent. No remote bearer credential is returned to the browser.
7. Use the **active computer selector** when installing a game or editing Agent-wide settings; these are intentionally scoped to one physical host.

## Authorization and persistence

- The coordinator checks the caller's own `Admin` or `ReadOnly` identity. ReadOnly callers can view hosts but cannot add/remove them or change any remote server state.
- Every remote API request uses the **corresponding remote token role**. The remote Agent enforces its own role restrictions again, including console input.
- Only named, manually registered upstream origins can be reached; the gateway does not accept an arbitrary client URL. TLS certificate validation stays enabled and HTTP redirects are not followed.
- Remote host credentials are stored in `managed-hosts.json` in the coordinator's `Agent:DataDir`. They are encrypted using the existing `SecretProtector` mechanism (Windows DPAPI or Unix AES-GCM with restricted file permissions). Browser responses contain only the host ID and display name.
- Removing a computer from the panel deletes its saved connection but **does not uninstall its Agent or stop any remote game process**. Revoke the dedicated tokens on the remote Agent if that computer should no longer be administered.
- Live history and backups remain local to each Agent. The coordinator does not copy server files or backup archives to another PC.

## Cloudflare panel

The existing Cloudflare Worker still connects to **one** configured `AGENT_ORIGIN`: the coordinator. Host switching happens inside that Agent and works with the existing authenticated Cloudflare Access gateway and the same WebUI. No extra Cloudflare Worker, public API or DNS entry is required per remote computer.

Cloudflare Access still maps identities to the coordinator's `Admin` or `ReadOnly` role. The coordinator then selects the matching remote token. Only the narrow SignalR negotiation POST is allowed for read-only callers; command sending stays Admin-only.

## Limits and operational notes

- Up to 20 remote host records can be configured. The combined dashboard limits each remote snapshot to the first 200 reported game servers.
- The All computers dashboard polls less often than the selected host to avoid unnecessary network traffic.
- Initial enrollment is manual; there is no automatic discovery, unattended credential exchange or public Internet inbound port requirement.
- If the coordinator PC is down, the combined dashboard is unavailable, **but each remote Agent and its local WebUI continue working independently**.
- Before a production deployment, verify host registration, startup/shutdown, ReadOnly access and live remote console across the actual network (including the Cloudflare path if used). Automated CI cannot reproduce your Tailscale network.
