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

`workers_dev` is disabled in the checked-in Wrangler configuration so the gateway is not accidentally exposed through an unprotected default Workers hostname.

## Cloudflare Tunnel origin

The Tunnel should run on the Agent host and forward only to the local Agent listener:

```text
gsc-origin.example.com -> http://127.0.0.1:5099
```

The Agent does not need an inbound Internet firewall rule. `cloudflared` establishes the connection outbound to Cloudflare.

The origin hostname should not be used as the human-facing panel. Protect it with a Service Auth policy that only accepts the Worker service token.

## Panel application

The human-facing hostname, for example `panel.example.com`, should route to the Worker and be protected by Cloudflare Access with the users/groups allowed to administer the game servers.

The Worker proxies both normal REST calls and `/hubs/status` SignalR WebSocket traffic. The browser therefore uses the same console, live logs, status and server controls remotely as it does locally.

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

Deployment is intentionally not automated yet because the Cloudflare account, domain, Access application, Tunnel hostname and secrets are environment-specific. Once those account details are chosen, the Worker can be connected without changing the Agent's local administration path.
