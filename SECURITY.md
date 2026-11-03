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

The first boot replaces the placeholder `Agent:ApiToken` with a cryptographically random token and persists an encrypted envelope in `appsettings.json`. Additional managed tokens can be created and revoked from the Web UI; those are stored one-way hashed in `tokens.json` and their reusable plaintext is returned only when created.

The Web UI itself is a static shell; API calls remain authenticated. The direct UI stores its selected Agent URL/token only in browser `sessionStorage`, so closing the browser tab/session removes that browser-side credential. Avoid untrusted/shared browser sessions. Cloudflare remote mode does not expose the real Agent bearer token to browser JavaScript; the Worker selects the server-side Agent credential.

Bearer tokens are accepted from the `Authorization` header for normal Agent API traffic. Query-string `access_token` compatibility is restricted to `/hubs/status` because SignalR browser transports may require it; normal `/api/*` routes do not accept bearer credentials from their URL.

The Agent also includes bounded, in-memory authentication-failure throttling for protected `/api` and `/hubs` requests. Repeated failed authentication from the same direct peer address triggers a temporary HTTP 429 response with `Retry-After`. A successful authentication clears that peer's failure state. This is defense in depth, not a replacement for private networking, firewall policy or Cloudflare Access.

## Browser hardening

The direct Agent UI and Cloudflare gateway send a Content Security Policy that permits executable JavaScript only from same-origin external files and disables inline event-handler attributes (`script-src 'self'` and `script-src-attr 'none'`). The policy also disables object/plugin execution, injected base URLs and framing. Responses include `X-Content-Type-Options: nosniff`, a no-referrer policy and restrictive browser feature permissions.

Web UI actions use delegated event listeners rather than executable HTML attributes. Arguments embedded into generated UI markup are encoded as data rather than JavaScript source before they can be dispatched to a registered action. Keep new UI code compatible with this model; do not add inline `<script>` blocks, `javascript:` URLs or `on*=` event attributes.

Inline CSS remains allowed because the existing UI uses style attributes and runtime style updates. This does not relax the JavaScript execution policy.

## Sensitive files and encrypted-at-rest state

Protect the Agent directory. In particular:

```text
appsettings.json
servers.json
public-relay.json
tokens.json
secret-protection.key   # Linux only
Logs/audit/
```

Reversible Agent-managed credentials are persisted as versioned `enc:v1:` envelopes. This includes the bootstrap API token, certificate password, public-relay token, CurseForge key, Hyper-V guest passwords, server RCON passwords and Discord webhook URLs. Existing plaintext values in `appsettings.json`, `servers.json`, `servers.json.bak` and `public-relay.json` are migrated when the Agent loads them.

On Windows encrypted envelopes use machine-scoped DPAPI. They are intended to be recoverable by the Agent on the same Windows host, not portable to another machine. On Linux the Agent uses AES-GCM with a random 256-bit local master key stored in `secret-protection.key`; the key file is restricted to mode `0600` and its directory to `0700`. Back up that key together with encrypted state if you need disaster recovery or migration to another Linux host.

`tokens.json` is different by design: managed bearer tokens are authentication verifiers, so GameServerControl stores only SHA-256 hashes and a non-secret display prefix rather than reversible ciphertext.

Encryption at rest reduces disclosure from copied/readable state files. It does **not** protect credentials from root/SYSTEM, an attacker that can run code as the Agent service identity, or a compromised running Agent process where decrypted values are necessarily present in memory. Filesystem permissions therefore remain required.

The protection covers Agent-managed state, not arbitrary game/plugin configuration files reachable through the Admin-only files editor. Secrets owned by individual games or plugins remain subject to those products' storage behavior.

On Windows the Agent remains a privileged LocalSystem control service because Hyper-V, service, firewall and updater operations may require elevated host access. Windows BareMetal game processes do **not** inherit that identity: each managed server is launched by its own deterministic `GameServerControlRunner_<hash>` Windows service under the matching virtual service account (`NT SERVICE\GameServerControlRunner_<hash>`). Agent-to-Runner control uses a bounded local named-pipe protocol that does not accept arbitrary executable paths or launch arguments; Runner launch metadata is stored in an Agent-managed per-server manifest. Runner ACL grants are scoped to its payload/state plus that server's game/save paths, and legacy Scheduled Task launchers are disabled during migration. Provisioning is cached by server configuration and pipe transport failures use bounded service recovery rather than falling back to privileged direct process launch. Pipe request/response operations are time-bounded end to end so a connected but unresponsive Runner cannot indefinitely block Agent status work.

Deleting a Windows BareMetal server first stops its managed game through that Runner, removes the Runner service SID from the Runner/game/save ACLs, deletes the per-server Runner service and state, and only then removes the server definition. If that cleanup fails, deletion fails closed and the server definition remains available for retry rather than leaving an unmanaged privileged ACL grant behind.

Linux BareMetal execution remains under the existing unprivileged `gsc` systemd service. Hyper-V guest execution remains a separate privileged control path and is not routed through the Windows BareMetal Runner.

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

Optional marketplace/provider keys such as the CurseForge API key are kept server-side, encrypted at rest after Agent startup, and are not returned by the normal Web UI API. Do not commit populated configuration files.

## Known limitations

- Encrypted-at-rest Agent state still depends on host trust: root/SYSTEM or the running Agent identity can access decrypted credentials.
- Arbitrary game/plugin configuration files are outside the Agent-managed secret-envelope mechanism.
- The self-signed HTTPS certificate path is intended for private deployments, not as a substitute for a properly managed public TLS endpoint.
- Hyper-V mode necessarily expands the Agent's host privileges; do not enable/use it when unnecessary.

## Deployment checklist

- [ ] `Agent:ApiToken` is no longer the placeholder and is stored as an `enc:v1:` envelope.
- [ ] Agent listens only on loopback/private interfaces.
- [ ] Port 5099 is not publicly forwarded.
- [ ] `appsettings.json`, `servers.json`, `public-relay.json`, `tokens.json` and the Linux `secret-protection.key` have restricted filesystem permissions.
- [ ] The Linux `secret-protection.key` is included in protected backups needed for host recovery/migration.
- [ ] On Windows BareMetal, game processes run under per-server `GameServerControlRunner_<hash>` virtual service accounts rather than the LocalSystem Agent identity.
- [ ] Admin and ReadOnly tokens are assigned appropriately.
- [ ] Public browser access, if used, is protected by Cloudflare Access or an equivalent authentication layer.
- [ ] Web UI responses include the restrictive Content Security Policy/security headers.
- [ ] HTTPS is terminated at a trusted proxy/tunnel or configured appropriately for direct private access.
- [ ] Audit logs are writable and periodically reviewed.

## Reporting security issues

Do not open a public issue for an exploitable vulnerability. Use GitHub's private security-advisory mechanism for the repository or contact the maintainer privately.
