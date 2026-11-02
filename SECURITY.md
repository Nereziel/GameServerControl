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

On first boot the placeholder `Agent:ApiToken` is replaced in memory with a cryptographically random bootstrap token. Reusable configuration secrets are persisted only through the encrypted local secret store described below. Additional managed tokens can be created and revoked from the Web UI; `tokens.json` stores only one-way token hashes/prefixes, not reusable bearer-token plaintext.

The Web UI itself is a static shell; API calls remain authenticated. The direct UI stores its selected Agent URL/token only in browser `sessionStorage`, so closing the browser tab/session removes that browser-side credential. Avoid untrusted/shared browser sessions. Cloudflare remote mode does not expose the real Agent bearer token to browser JavaScript; the Worker selects the server-side Agent credential.

Bearer tokens are accepted from the `Authorization` header for normal Agent API traffic. Query-string `access_token` compatibility is restricted to `/hubs/status` because SignalR browser transports may require it; normal `/api/*` routes do not accept bearer credentials from their URL.

The Agent also includes bounded, in-memory authentication-failure throttling for protected `/api` and `/hubs` requests. Repeated failed authentication from the same direct peer address triggers a temporary HTTP 429 response with `Retry-After`. A successful authentication clears that peer's failure state. This is defense in depth, not a replacement for private networking, firewall policy or Cloudflare Access.

## Browser hardening

The direct Agent UI and Cloudflare gateway send a Content Security Policy that permits executable JavaScript only from same-origin external files and disables inline event-handler attributes (`script-src 'self'` and `script-src-attr 'none'`). The policy also disables object/plugin execution, injected base URLs and framing. Responses include `X-Content-Type-Options: nosniff`, a no-referrer policy and restrictive browser feature permissions.

Web UI actions use delegated event listeners rather than executable HTML attributes. Arguments embedded into generated UI markup are encoded as data rather than JavaScript source before they can be dispatched to a registered action. Keep new UI code compatible with this model; do not add inline `<script>` blocks, `javascript:` URLs or `on*=` event attributes.

Inline CSS remains allowed because the existing UI uses style attributes and runtime style updates. This does not relax the JavaScript execution policy.

## Sensitive files and encryption at rest

Protect the Agent directory. Important state includes:

```text
appsettings.json
servers.json
tokens.json
public-relay.json
secret.key
secret-state.json
Logs/audit/
```

Reusable Agent configuration credentials, server RCON passwords, Discord webhook URLs and the public-relay bearer token are protected at rest with purpose-bound **AES-GCM** envelopes. Existing plaintext installations are migrated automatically: sensitive `appsettings.json` leaves are replaced with the non-secret `<stored-encrypted>` marker, server/relay state is rewritten encrypted, and runtime consumers receive decrypted values only in Agent memory.

`secret.key` contains the random 256-bit master-key material required to open those envelopes. On Windows the key is wrapped with **DPAPI LocalMachine** before it is written and the Agent directory ACL is restricted to SYSTEM/local Administrators. On Linux the random master key is stored under the service-owned Agent directory with mode `0600` and the parent directory remains `0700`. `secret-state.json` and the other sensitive state files are also restricted to the service identity.

The encryption files are a pair: back up and restore `secret.key` together with encrypted state. Windows and Linux installers preserve `secret.key` and `secret-state.json` across upgrades. If an existing encrypted state cannot be authenticated with its key, the Agent fails closed rather than silently replacing the key and losing access to stored credentials.

This protects copied/offline state files from exposing reusable plaintext by themselves. It does **not** protect secrets from an attacker who already controls LocalSystem/root, local Administrators, or the running Agent service identity and can read both the key and ciphertext. Filesystem ownership and host privilege therefore remain part of the security boundary.

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

A configured certificate password is migrated into encrypted secret state. The Agent can generate a self-signed certificate. For a Cloudflare/Tailscale design it is often simpler to keep Agent HTTP private and terminate TLS at the tunnel/proxy.

## Self-update

On Windows, `/api/update` requires an **Admin** token. The Agent starts an updater helper outside its own installation directory, then the helper stops/replaces/restarts the Agent service. Game-server processes are not intentionally stopped by a GameServerControl application update.

The updater downloads release artifacts and the installer from the configured GitHub repository (`Agent:Repo`). Keep this setting pointed at a repository you trust.

Linux web-triggered self-update is intentionally disabled for now; use the release installer with root privileges.

## External provider credentials

Optional marketplace/provider keys such as the CurseForge API key are migrated into encrypted `secret-state.json` and are not returned by the normal Web UI API. Environment/runtime overrides stay runtime-only and are not copied into persistent encrypted state merely because they are present in `IConfiguration`. Do not commit populated configuration or secret-state files.

## Known limitations

- The self-signed HTTPS certificate path is intended for private deployments, not as a substitute for a properly managed public TLS endpoint.
- Hyper-V mode necessarily expands the Agent's host privileges; do not enable/use it when unnecessary.

## Deployment checklist

- [ ] `Agent:ApiToken` is no longer the placeholder at runtime.
- [ ] Agent listens only on loopback/private interfaces.
- [ ] Port 5099 is not publicly forwarded.
- [ ] `appsettings.json`, `servers.json`, `tokens.json`, `public-relay.json`, `secret.key` and `secret-state.json` have restricted filesystem permissions.
- [ ] `secret.key` is backed up/restored together with encrypted Agent state.
- [ ] Admin and ReadOnly tokens are assigned appropriately.
- [ ] Public browser access, if used, is protected by Cloudflare Access or an equivalent authentication layer.
- [ ] Web UI responses include the restrictive Content Security Policy/security headers.
- [ ] HTTPS is terminated at a trusted proxy/tunnel or configured appropriately for direct private access.
- [ ] Audit logs are writable and periodically reviewed.

## Reporting security issues

Do not open a public issue for an exploitable vulnerability. Use GitHub's private security-advisory mechanism for the repository or contact the maintainer privately.
