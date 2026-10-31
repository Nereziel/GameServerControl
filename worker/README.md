# GameServerControl Cloudflare Worker

This directory contains the public web dashboard and the Cloudflare Worker API proxy.
The browser never receives a GameServerControl bearer token.

## Request path

```text
Browser
  -> Cloudflare Worker /api/windows/*
  -> HTTPS VPS gateway /windows/api/*
  -> Tailscale
  -> Windows GameServerControl Agent /api/*
```

The Worker serves `public/` as static assets. Requests under `/api/*` run Worker code first.
The current UI polls status every five seconds, so SignalR/WebSocket proxying is not required for the first deployment.

## Required Worker secrets

Configure these in Cloudflare Workers > Settings > Variables and Secrets, or with Wrangler:

```bash
npx wrangler secret put GATEWAY_BASE_URL
npx wrangler secret put GATEWAY_SECRET
npx wrangler secret put WINDOWS_AGENT_TOKEN
```

- `GATEWAY_BASE_URL`: public HTTPS origin of the VPS gateway, without a trailing slash.
- `GATEWAY_SECRET`: shared secret accepted only by the VPS gateway.
- `WINDOWS_AGENT_TOKEN`: GameServerControl Admin token for the Windows agent.

Do not put these values in `wrangler.jsonc`, HTML/JavaScript, `.env`, or any committed file.
For local development use `worker/.dev.vars`; it is ignored by Git.

Example local file:

```dotenv
GATEWAY_BASE_URL="https://gsc-api.example.com"
GATEWAY_SECRET="replace-me"
WINDOWS_AGENT_TOKEN="replace-me"
```

## Development

Requires Node.js 24.

```bash
cd worker
npm install
npm run dev
```

## Deploy

```bash
cd worker
npm install
npm run deploy
```

Protect the Worker/custom domain with Cloudflare Access before treating it as an Internet-facing administration panel.

## VPS gateway contract

The Worker currently sends requests in this form:

```text
GET https://<gateway>/windows/api/servers
Authorization: Bearer <WINDOWS_AGENT_TOKEN>
X-GSC-Gateway-Secret: <GATEWAY_SECRET>
X-GSC-Node: windows
```

The VPS must validate `X-GSC-Gateway-Secret`, remove it before proxying, and route `/windows/api/...` over Tailscale to the Windows GSC agent as `/api/...`.
Do not expose the GSC agent port directly to the public Internet.
