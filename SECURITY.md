# Security

GameServerControl is administrative software for game-server hosts. Anyone with an **Admin** API token can start/stop servers, change configuration, install mods, restore backups and trigger a GameServerControl update. Treat Admin tokens like SSH credentials.

## Recommended deployment

The Agent serves the API and Web UI from the same process. The default listener is intentionally loopback-only:

```text
http://127.0.0.1:5099
```

For remote administration, prefer one of these patterns:

1. **Tailscale/private LAN** — bind the Agent only to the required private interface.
2. **Cloudflare Tunnel/Access or another authenticated reverse proxy** — keep the Agent on loopback/private networking and terminate public HTTPS/authentication at the proxy.

Do not directly expose port 5099 to the public Internet.

## Authentication and roles

All `/api/*` endpoints and the SignalR status hub require bearer-token authentication.

GameServerControl supports:

- **Admin** tokens — read/write administration.
- **ReadOnly** tokens — read access; mutation middleware rejects write methods.

The first boot replaces the placeholder `Agent:ApiToken` with a cryptographically random token and persists it in `appsettings.json`. Additional tokens can be created and revoked from the Web UI.

The Web UI itself is a static shell; API calls remain authenticated. The direct UI stores its selected Agent URL/token only in browser `sessionStorage`, so closing the browser tab/session removes that browser-side credential. Avoid untrusted/shared browser sessions. Cloudflare remote mode does not expose the real Agent bearer token to browser JavaScript; the Worker selects the server-side Agent credential.

Bearer tokens are accepted from the `Authorization` header for normal Agent API traffic. Query-string `access_token` compatibility is restricted to `/hubs/status` because SignalR browser transports may require it; normal `/api/*` routes do not accept bearer credentials from their URL.

The Agent also includes bounded, in-memory authentication-failure throttling for protected `/api` and `/hubs` requests. Repeated failed authentication from the same direct peer address triggers a temporary HTTP 429 response with `Retry-After`. A successful authentication clears that peer's failure state. This is defense in depth, not a replacement for private networking, firewall policy or Cloudflare Access.

## Sensitive files

Protect the Agent directory. In particular:

```text
appsettings.json
servers.json
tokens.json
Logs/audit/
```

`appsettings.json` contains the bootstrap API token and optional provider credentials. `servers.json` may contain RCON/admin passwords and Discord webhooks. These values are currently persisted as plaintext JSON, so filesystem permissions matter.

On Windows the default service runs as LocalSystem because Hyper-V/Task Scheduler integration may require elevated host access. Restrict write access to the installation directory accordingly.

## Audit logging

Authenticated mutations are written to JSON-lines audit logs under `Logs/audit/` unless overridden by `Agent:AuditLogPath`. Review these logs if an unexpected server action occurs.

## HTTPS

For direct private HTTPS, configure:

```json
"BindHttps": "https://<private-address>:5443",
"CertPassword": "use-a-strong-password",
"CertSanHosts": "hostname.example",
"CertSanIps": "100.x.y.z"
```

The Agent can generate a self-signed certificate. For a Cloudflare/Tailscale design it is often simpler to keep Agent HTTP private and terminate TLS at the tunnel/proxy.

## Self-update

On Windows, `/api/update` requires an **Admin** token. The Agent starts an updater helper outside its own installation directory, then the helper stops/replaces/restarts the Agent service. Game-server processes are not intentionally stopped by a GameServerControl application update.

The updater downloads release artifacts and the installer from the configured GitHub repository (`Agent:Repo`). Keep this setting pointed at a repository you trust.

Linux web-triggered self-update is intentionally disabled for now; use the release installer with root privileges.

## External provider credentials

Optional marketplace/provider keys such as the CurseForge API key are stored server-side in `appsettings.json` and are not returned by the normal Web UI API. Do not commit populated configuration files.

## Known limitations

- Server/RCON/password values are not encrypted at rest yet.
- The self-signed HTTPS certificate path is intended for private deployments, not as a substitute for a properly managed public TLS endpoint.
- Hyper-V mode necessarily expands the Agent's host privileges; do not enable/use it when unnecessary.

## Deployment checklist

- [ ] `Agent:ApiToken` is no longer the placeholder.
- [ ] Agent listens only on loopback/private interfaces.
- [ ] Port 5099 is not publicly forwarded.
- [ ] `appsettings.json`, `servers.json` and `tokens.json` have restricted filesystem permissions.
- [ ] Admin and ReadOnly tokens are assigned appropriately.
- [ ] Public browser access, if used, is protected by Cloudflare Access or an equivalent authentication layer.
- [ ] HTTPS is terminated at a trusted proxy/tunnel or configured appropriately for direct private access.
- [ ] Audit logs are writable and periodically reviewed.

## Reporting security issues

Do not open a public issue for an exploitable vulnerability. Use GitHub's private security-advisory mechanism for the repository or contact the maintainer privately.
