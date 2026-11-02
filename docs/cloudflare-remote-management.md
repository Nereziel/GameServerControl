# Cloudflare remote management

GameServerControl keeps the Agent as the source of truth. Cloudflare is an optional remote-management layer; local Agent Web UI remains available even if Cloudflare or the public VPS relay is unavailable.

## Architecture

```text
Remote administration
Browser
  -> Cloudflare Access
  -> GameServerControl Cloud Worker
       -> Cloudflare Access service token
       -> Cloudflare Tunnel origin
       -> GameServerControl Agent on 127.0.0.1:5099

Local administration
Browser on LAN / Tailscale
  -> GameServerControl Agent directly

Game traffic
Player
  -> public VPS
  -> GameServerControl.Relay
  -> Tailscale
  -> game server
```

The VPS relay is deliberately independent from remote administration. Losing the VPS therefore does not remove local or Cloudflare administration as long as the Agent host still has Internet access.

## Shared Web UI

`src/GameServerControl.Cloud/scripts/build.mjs` stages the current Agent Web UI into the Cloudflare Worker static-assets directory. There is no separate cloud dashboard to maintain.

The cloud build injects a small adapter which:

- points the existing UI at the same-origin Worker gateway,
- uses only a non-secret placeholder token in the browser,
- labels the session as Cloudflare remote,
- sends sign-out to Cloudflare Access,
- keeps the Agent's normal local login/token flow unchanged.

The Worker replaces the placeholder credentials server-side with the role-appropriate Agent token. The real Agent Admin/ReadOnly tokens never need to be stored in browser storage or sent to browser JavaScript.

The same API path also carries the server config-file workspace. Config files and mod/plugin configs stay on the Agent host; the Worker only transports their text contents and control requests.

## Security boundaries

The Worker only proxies `/api/*` and `/hubs/*`. Static assets are served from Workers Static Assets.

Remote API and SignalR/WebSocket requests require a valid Cloudflare Access JWT for the panel application. The Worker validates the JWT signature, issuer and audience using the Cloudflare Access JWKS endpoint.

The private Agent origin must use a separate Tunnel hostname protected by a Cloudflare Access `Service Auth` policy. The Worker authenticates to that hostname with an Access service token, then authenticates to GameServerControl with an Agent token selected for the resolved Access role.

The checked-in Wrangler configuration contains no production hostnames, tokens or audience IDs. All seven required runtime values are declared as required secrets so a production deploy fails instead of silently using example values:

- `AGENT_ORIGIN` - HTTPS hostname routed by Cloudflare Tunnel to the Agent, for example `https://gsc-origin.example.com`.
- `ACCESS_TEAM_DOMAIN` - Cloudflare Access team domain, for example `https://example.cloudflareaccess.com`.
- `ACCESS_POLICY_AUD` - audience tag for the Access application protecting the panel.
- `AGENT_ADMIN_TOKEN` - GameServerControl Agent token with the Admin role.
- `AGENT_READONLY_TOKEN` - GameServerControl Agent token with the ReadOnly role.
- `ORIGIN_ACCESS_CLIENT_ID` - Access service-token client ID for the private Tunnel origin.
- `ORIGIN_ACCESS_CLIENT_SECRET` - Access service-token client secret for the private Tunnel origin.

`workers_dev` and Worker preview URLs are disabled in the checked-in Wrangler configuration so the gateway is not accidentally exposed through an unprotected default Workers hostname.

For mutating API requests the Worker forwards the authenticated Access user identity to the Agent as audit metadata. Agent audit entries therefore retain both the Agent bearer identity and the remote Access user when the request came through the Cloudflare gateway.

The Worker also rejects cross-origin `POST`, `PUT`, `PATCH` and `DELETE` requests before proxying them to the Agent.

## Cloudflare Tunnel origin

The Tunnel runs on the Agent host and forwards only to the local Agent listener:

```text
gsc-origin.example.com -> http://127.0.0.1:5099
```

The Agent does not need an inbound Internet firewall rule. `cloudflared` establishes the connection outbound to Cloudflare.

The origin hostname is not the human-facing panel. Protect it with a Service Auth policy that only accepts the Worker service token.

### Windows connector bootstrap

For a remotely-managed tunnel, first create the tunnel and its published application in Cloudflare. Copy the tunnel token from **Networking > Tunnels > Add a replica**. Treat the token like a password: anyone holding it can run a connector for that tunnel.

On the Windows Agent host, run an elevated PowerShell session:

```powershell
.\deploy\cloudflare\install-tunnel-windows.ps1 -TunnelToken '<TUNNEL_TOKEN>'
```

The script:

- verifies that the local Agent listener is reachable,
- installs the current Windows amd64 `cloudflared` executable under `Program Files\GameServerControl\cloudflared`,
- installs it as the `cloudflared` Windows service using the remotely-managed tunnel token,
- sets automatic startup,
- verifies that the service reaches the `Running` state.

If a `cloudflared` service already exists, the script refuses to overwrite it. Use `-ReplaceExisting` only when the existing connector is intentionally being replaced by the GameServerControl tunnel.

The remotely-managed Tunnel configuration in Cloudflare must publish the origin hostname to:

```text
http://127.0.0.1:5099
```

Do not configure the Tunnel to reach the Agent through a public/LAN listener when the local loopback listener is available.

## Panel application

The human-facing hostname, for example `panel.example.com`, is a Worker Custom Domain and must be protected by a hostname-based Cloudflare Access application before the deployment workflow is run.

The Access application should allow only the users/groups that administer or view the game servers. The deployment workflow checks that an exact Access application already exists for the requested panel hostname and that its `aud` matches the supplied `access_policy_aud`. This prevents the workflow from creating a new public Worker hostname before Access protection exists.

The Worker proxies both normal REST calls and `/hubs/status` SignalR WebSocket traffic. The browser therefore uses the same console, live logs, status, file/config editor and server controls remotely as it does locally, subject to the resolved Admin/ReadOnly role.

## Authenticated diagnostics

Two Worker diagnostics endpoints exist:

```text
GET /gateway/health
GET /gateway/status
```

`/gateway/health` only reports whether the Worker has all required bindings. It does not contact the Agent.

`/gateway/status` requires the normal panel Cloudflare Access identity. It then performs a server-to-server health probe through the origin Access Service Auth policy and Tunnel to `Agent /api/health` using the ReadOnly Agent token. The response contains only safe status metadata:

```json
{
  "ok": true,
  "mode": "cloudflare",
  "user": "admin@example.com",
  "agent": {
    "reachable": true,
    "status": 200,
    "timestamp": "2026-11-02T10:00:00Z"
  }
}
```

No Agent token or Access service-token credential is returned.

## Build and validation

From the repository root:

```bash
cd src/GameServerControl.Cloud
npm install
npm test
npm run build
```

The build output is generated under `src/GameServerControl.Cloud/dist/` and is intentionally ignored by Git.

For local Worker development, create an uncommitted `src/GameServerControl.Cloud/.dev.vars` containing the seven required runtime values listed above, then run:

```bash
npm run dev
```

`.dev.vars` is ignored by Git.

## Production handoff order

Do the account-specific setup in this order so there is never an intentionally public administration endpoint:

1. Enable Cloudflare Zero Trust for the account if it is not already enabled.
2. Create a remotely-managed Tunnel, publish `gsc-origin.<domain>` to `http://127.0.0.1:5099`, and install the connector on the Windows Agent host.
3. Create an Access Service Token for the private origin.
4. Protect `gsc-origin.<domain>` with a Service Auth Access application/policy that accepts that service token.
5. Test the origin with the service token plus the GameServerControl ReadOnly Agent token.
6. Create the hostname-based Access application for `panel.<domain>` and allow only the intended users.
7. Copy the panel Access application `aud` value.
8. Configure the six GitHub Actions secrets listed below.
9. Run **Deploy Cloudflare panel** with the four non-secret deployment inputs.
10. Sign in to the panel and open `https://panel.<domain>/gateway/status` to verify the complete path.

The public VPS/GameServerControl Relay is not part of these steps.

## Manual production deployment from GitHub Actions

The repository contains `.github/workflows/cloudflare-deploy.yml`. It is intentionally `workflow_dispatch` only: pushes to `main` never publish the panel automatically.

### GitHub Actions secrets

Configure only credentials/account identifiers as repository or environment secrets:

```text
CLOUDFLARE_ACCOUNT_ID
CLOUDFLARE_API_TOKEN
GSC_CF_AGENT_ADMIN_TOKEN
GSC_CF_AGENT_READONLY_TOKEN
GSC_CF_ORIGIN_ACCESS_CLIENT_ID
GSC_CF_ORIGIN_ACCESS_CLIENT_SECRET
```

Do not commit any of these values.

The two Agent secrets must belong to distinct Agent tokens with the matching `Admin` and `ReadOnly` roles. Do not reuse an Admin token for the ReadOnly binding.

The Cloudflare API token is used both by Wrangler and by the deployment preflight. It must be scoped to the intended account/zone and have enough permission to deploy the Worker, manage the selected Worker Custom Domain, and list the Access application protecting the panel. Cloudflare currently requires Worker Editor access plus Zone `Workers Routes Write` when adding or changing a Custom Domain; do not use a global API key when a scoped token is sufficient.

### Workflow inputs

Values that identify the selected deployment are normal manual workflow inputs rather than long-lived GitHub secrets:

```text
panel_hostname       panel.example.com
agent_origin         https://gsc-origin.example.com
access_team_domain   https://your-team.cloudflareaccess.com
access_policy_aud    <panel Access application AUD>
```

The workflow rejects malformed URLs/hostnames and rejects using the same hostname for the panel and private Agent origin.

### What the workflow verifies

Before any Worker custom domain is deployed, the workflow:

1. builds the current shared Agent Web UI,
2. validates all required input and secret values,
3. asks the Cloudflare Access API for an exact application matching `panel_hostname`,
4. verifies the supplied `access_policy_aud` matches that application,
5. calls `${agent_origin}/api/health` using both the origin Access service token and ReadOnly Agent token,
6. aborts if the Tunnel, Service Auth policy, service token or Agent authentication is not working,
7. uploads the seven Worker runtime values as encrypted Worker secrets,
8. runs `wrangler deploy --strict --domain <panel_hostname>`.

Using Wrangler's Custom Domain deployment means Cloudflare creates/manages the Worker DNS record and certificate for the selected panel hostname. The hostname must belong to an active Cloudflare zone and must not already have a conflicting CNAME record.

## What is intentionally left for the real account

No Cloudflare account resources are silently created by a normal repository push. The remaining values are environment-specific and cannot be selected safely from source code alone:

- the Cloudflare account/zone,
- `panel.<domain>`,
- `gsc-origin.<domain>`,
- which administrator/viewer identities are allowed by Access,
- the Tunnel token,
- the origin Access service token,
- the panel Access application AUD,
- the Agent Admin and ReadOnly tokens.

Once those concrete values are chosen, the repository is prepared to verify and deploy the panel without changing or disabling local Agent administration.
