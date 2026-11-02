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

The Worker replaces the placeholder credentials server-side. The real Agent API token never needs to be stored in browser storage or sent to browser JavaScript.

The same API path also carries the server config-file workspace. Config files and mod/plugin configs stay on the Agent host; the Worker only transports their text contents and control requests.

## Security boundaries

The Worker only proxies `/api/*` and `/hubs/*`. Static assets are served from Workers Static Assets.

Remote API and SignalR/WebSocket requests require a valid Cloudflare Access JWT for the panel application. The Worker validates the JWT signature, issuer and audience using the Cloudflare Access JWKS endpoint.

The private Agent origin should be a separate Tunnel hostname protected by a Cloudflare Access `Service Auth` policy. The Worker authenticates to that hostname with an Access service token, then authenticates to GameServerControl with the Agent API token.

Required Worker configuration:

- `AGENT_ORIGIN` - HTTPS hostname routed by Cloudflare Tunnel to the Agent, for example `https://gsc-origin.example.com`.
- `ACCESS_TEAM_DOMAIN` - Cloudflare Access team domain, for example `https://example.cloudflareaccess.com`.
- `ACCESS_POLICY_AUD` - audience tag for the Access application protecting the panel.

Required Worker secrets:

- `AGENT_API_TOKEN`
- `ORIGIN_ACCESS_CLIENT_ID`
- `ORIGIN_ACCESS_CLIENT_SECRET`

`workers_dev` and Worker preview URLs are disabled in the checked-in Wrangler configuration so the gateway is not accidentally exposed through an unprotected default Workers hostname.

For mutating API requests the Worker forwards the authenticated Access user identity to the Agent as audit metadata. Agent audit entries therefore retain both the Agent bearer identity and the remote Access user when the request came through the Cloudflare gateway.

## Cloudflare Tunnel origin

The Tunnel should run on the Agent host and forward only to the local Agent listener:

```text
gsc-origin.example.com -> http://127.0.0.1:5099
```

The Agent does not need an inbound Internet firewall rule. `cloudflared` establishes the connection outbound to Cloudflare.

The origin hostname should not be used as the human-facing panel. Protect it with a Service Auth policy that only accepts the Worker service token.

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

The human-facing hostname, for example `panel.example.com`, should route to the Worker and be protected by Cloudflare Access with the users/groups allowed to administer the game servers.

The Worker proxies both normal REST calls and `/hubs/status` SignalR WebSocket traffic. The browser therefore uses the same console, live logs, status, file/config editor and server controls remotely as it does locally.

## Build and validation

From the repository root:

```bash
cd src/GameServerControl.Cloud
npm install
npm run build
```

The build output is generated under `src/GameServerControl.Cloud/dist/` and is intentionally ignored by Git.

For local Worker development, configure Wrangler development Access identity and local secrets, then run:

```bash
npm run dev
```

Cloudflare account resources are intentionally not created automatically from the repository because the account, zone, hostname, Access policy and secrets are environment-specific. The checked-in Worker and Tunnel bootstrap are ready to be connected once those values are selected, without changing or disabling local Agent administration.
