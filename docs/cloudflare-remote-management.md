# Cloudflare WebUI: independent Agents behind VPS Tailscale proxy

**Current architecture:** Cloudflare Worker + Cloudflare Access + Cloudflare D1 (administration), one VPS running Caddy (HTTPS/WebSocket forwarding only), and an independent GameServerControl Agent on each PC. The existing `GameServerControl.Relay` continues to handle **game traffic only** and is not an administration coordinator.

There is **no `cloudflared` requirement** on either PC or on the VPS. Agents have no public Internet listeners. Every remote administration request goes:

```text
Browser ──HTTPS──> Cloudflare Access ──> Worker static WebUI
                                                |
                                      encrypted D1 host registry
                                                |
                                      fixed VPS public HTTPS hostname
                                                |
                                 authenticated Caddy reverse proxy
                                       /               \
                                      /                 \
                          Tailscale to PC1         Tailscale to PC2
                          Agent API/SignalR       Agent API/SignalR
                          local WebUI             local WebUI

Game players ──public TCP/UDP──> VPS GameServerControl.Relay
                                    └──Tailscale──> game server on PC1 or PC2
```

Cloudflare Worker does **not** send requests through PC1 to reach PC2. If PC1 is powered down, the Worker still serves the dashboard, marks PC1 offline and connects directly to PC2 through the VPS proxy.

## 1. Set up the forwarding-only VPS

The VPS needs a public DNS name and HTTPS listener, e.g. `gsc-gateway.example.com`. This is a **separate** hostname from the human-facing Worker panel, e.g. `panel.example.com`. Point the gateway DNS name to the VPS public IP; DNS-only is simplest. Install Caddy on the VPS (or add equivalent rules to an existing TLS reverse proxy).

1. Copy [the example Caddyfile](../deploy/relay/admin-gateway/Caddyfile.example) to the VPS configuration. Replace the gateway hostname with your real domain. Replace `100.100.10.1` and `100.100.10.2` with the **actual Tailscale IPv4** addresses of PC1 and PC2.
2. Generate a random gateway secret on the VPS with `openssl rand -hex 32`. Put it into a root-owned, restricted systemd EnvironmentFile (`GSC_ADMIN_GATEWAY_KEY=<secret>`), readable by Caddy. The Cloudflare Worker uses the **same value** as `GATEWAY_SHARED_KEY`, stored as a Worker secret.
3. Configure `caddy.service` to read the EnvironmentFile. Validate with `caddy validate --config /etc/caddy/Caddyfile` before reloading the service.
4. The site has only a few explicit routes: `/gsc-agent/pc1/*`, `/gsc-agent/pc2/*`, etc. Every request must contain the gateway secret in `X-GSC-Proxy-Key`; requests without it return HTTP 403. Caddy strips that header before forwarding to Agent. Caddy supports WebSocket upgrades, required for the live console.
5. The authenticated check `GET /_gsc_gateway/health` returns HTTP 200; **without the secret it must return HTTP 403**. The deployment workflow tests both.
6. Open only HTTPS/TLS ports necessary for the gateway on the VPS. Keep the existing `GameServerControl.Relay` TCP/UDP rules separate. Never expose the Agent API port 5099 publicly.

Example systemd override for the existing Caddy service:

```ini
[Service]
EnvironmentFile=/etc/caddy/gsc-admin-gateway.env
```

The sample Caddyfile is a configuration **example** and is not deployed automatically. Host IDs in the Caddyfile must exactly match the IDs added later in the WebUI. Adding a new PC requires a new VPS forwarding route (reload Caddy) plus adding that PC in the Cloudflare UI.

The shared gateway secret restricts access to your forwarding proxy; each destination Agent additionally requires its own bearer token and enforces Admin/ReadOnly. Keep that secret out of source code and out of proxy access logs.

## 2. Give each Agent a Tailscale listener

Each PC retains the local loopback UI `http://127.0.0.1:5099`. You can add a **second** restricted listener in Agent configuration:

```json
{
  "Agent": {
    "Bind": "http://127.0.0.1:5099",
    "BindTailscale": "http://100.100.10.1:5099"
  }
}
```

Use that PC's *actual* Tailscale address. `Agent:BindTailscale` is optional; the Agent rejects non-Tailscale IPv4, an arbitrary port or a public interface. Restart the Agent after changing its listener. Restrict host firewall rules so that **only the VPS Tailscale address** can reach the Agent's Tailscale listener on TCP 5099. Do not bind Agent to `0.0.0.0` to make it reachable.

On every PC, create **two distinct Agent managed tokens** in its local WebUI: one `Admin`, one `ReadOnly`. Avoid using the bootstrap token for cross-host automation.

## 3. Provision Cloudflare

The Cloudflare Worker serves the existing responsive Agent WebUI as static assets and uses the **Cloudflare Access** login. It stores a list of PCs in Cloudflare D1, with per-PC tokens encrypted by AES-256-GCM using a *separate* Worker encryption-key secret.

1. Create a D1 database named `gameservercontrol-hosts`, for example using `wrangler d1 create gameservercontrol-hosts`. Copy its UUID into the GitHub Actions secret `GSC_CF_HOSTS_DB_ID`. The Worker deployment action applies `migrations/0001_registered_hosts.sql`.
2. Create two DNS hostnames on your Cloudflare-managed domain: `panel.<domain>` (Worker Custom Domain) and `gateway.<domain>` (HTTPS endpoint on VPS).
3. Protect the exact panel hostname with a Cloudflare Access application and an allow policy for intended users. Note its audience (`AUD`) and team domain (`https://<team>.cloudflareaccess.com`). The deployment action checks that Access protection is already in place, then deploys. `workers.dev` and preview URLs remain disabled.
4. Set the required **GitHub Actions secrets** (values remain private, never committed):

| Secret | Purpose |
|---|---|
| `CLOUDFLARE_ACCOUNT_ID` | Cloudflare account |
| `CLOUDFLARE_API_TOKEN` | Scoped Worker deploy, D1 migrations and Access-app read permissions |
| `GSC_CF_HOSTS_DB_ID` | Existing D1 database UUID |
| `GSC_CF_HOSTS_ENCRYPTION_KEY` | 32 random bytes encoded as base64 (`openssl rand -base64 32`) |
| `GSC_CF_GATEWAY_SHARED_KEY` | Random secret identical to VPS Caddy `GSC_ADMIN_GATEWAY_KEY` |
| `GSC_CF_ACCESS_ADMIN_EMAILS` | Comma-separated allowlist for Cloudflare users permitted to administer servers and register PCs |

5. Run GitHub Actions **Deploy Cloudflare panel** with `panel_hostname`, `gateway_origin`, `access_team_domain` and `access_policy_aud`. No per-PC Agent tokens are deployment secrets: add them through the authenticated WebUI instead.

The Worker also needs runtime variables `GATEWAY_ORIGIN`, `GATEWAY_SHARED_KEY`, `HOSTS_ENCRYPTION_KEY`, `ACCESS_TEAM_DOMAIN`, `ACCESS_POLICY_AUD`, `ACCESS_ADMIN_EMAILS`, plus the `HOSTS_DB` D1 binding. The action configures them when it deploys.

The deploy workflow performs a positive HTTPS gateway health probe **and** a negative probe without credentials. It does not need PC1 or PC2 to be online to deploy the initial panel.

## 4. Register and manage computers

Open `https://panel.<domain>` and log in through Cloudflare Access.

- The starting view is **All computers**, even when none have been registered.
- Click **+ Connect computer**. Enter a stable lowercase ID that matches the VPS Caddy route (`pc1`, `pc2`, etc.), a display name and that computer's dedicated Admin and ReadOnly Agent tokens.
- Cloudflare tests each token's real `/api/auth/me` role **through the VPS** before encrypting and saving the host. If the route is missing/offline, registration fails safely.
- The dashboard reads both Agents independently. Choose one PC or a game server to manage starts, stops, backups, logs, configs, installs or console.
- When one Agent goes offline, only that PC is shown offline. The other PC is still controllable. Local UI keeps working regardless of Cloudflare.
- Removing a PC deletes its central credentials and record. It **does not** uninstall Agent, delete games or stop processes. Revoke remote managed tokens locally when they are no longer needed.

## Security boundary and limits

- Worker validates the Cloudflare Access JWT (issuer, audience, signature) and maps logged-in users to Admin or ReadOnly according to the explicit admin email allowlist. It checks state-changing requests for same-origin CSRF and forwards the correct per-host credential. ReadOnly cannot mutate remote state.
- Worker never accepts an arbitrary upstream URL from a browser. Its target hostname comes **only** from the `GATEWAY_ORIGIN` secret, with a syntactically restricted host ID/path. VPS Caddy resolves the host ID to a preconfigured Tailscale IP. No request-controlled destination IP or unrestricted proxy is allowed.
- Worker does not expose D1 credentials, VPS gateway key or host tokens to the browser. Tokens are encrypted separately from the D1 database using a Worker secret. Rotating the encryption key requires a credential migration or re-registering the PCs.
- VPS sees forwarded request headers and Agent bearer credentials while handling the request but does not persist them; do not enable verbose sensitive header logging. TLS protects Cloudflare→VPS, Tailscale protects VPS→PC. Agent and Caddy each enforce their own authentication boundaries.
- Limit of 20 registered PCs and 200 displayed game servers per PC. No host-to-host failover is necessary to keep online PCs manageable.

**This is the target deployment procedure, not a claim that it has been deployed to your VPS or Cloudflare account.** A real-network smoke test is still required.

For local-only Multi-Agent management without Cloudflare, see [Multi-Agent management](multi-agent-management.md).
