# Security and Access Hardening Design

## Goal

Harden GameServerControl administration without breaking local/offline management. The Agent remains the source of truth. Cloudflare is an optional remote access layer, not an authorization authority that can bypass Agent permissions.

## Trust boundaries

- **Local/Tailscale browser -> Agent**: authenticated with a GameServerControl bearer token carrying `Admin` or `ReadOnly`.
- **Remote browser -> Cloudflare Access -> Worker**: Cloudflare Access proves the human identity. The Worker maps that verified identity to `Admin` or `ReadOnly` and selects a matching Agent token.
- **Worker -> protected Tunnel origin -> Agent**: authenticated twice: Cloudflare Access service credentials protect the origin hostname and the selected Agent bearer token enforces the role again at the Agent.
- **Game traffic through VPS Relay** remains separate from administration and is out of scope for this change.

## Authorization model

- `Admin`: reads and mutations, including lifecycle, config changes, backup restore/delete, mod changes, token management and console input.
- `ReadOnly`: GET/read operations, live status/log/console subscription, but no API mutations and no interactive console input.
- SignalR mutating methods must enforce `Admin` themselves; HTTP method middleware is not sufficient for WebSockets.
- Add `/api/auth/me` so clients can discover the authenticated token identity and role without exposing token material.

## Cloud role mapping

The Worker validates the Access JWT issuer and application audience. The verified standard `email` claim is compared case-insensitively against `ACCESS_ADMIN_EMAILS`, a comma/semicolon/newline-separated allowlist. Matching users are `Admin`; every other successfully authenticated Access user is `ReadOnly`.

The Worker requires two Agent secrets:

- `AGENT_ADMIN_TOKEN`
- `AGENT_READONLY_TOKEN`

It selects the matching token for every proxied HTTP/WebSocket request. State-changing HTTP requests from a ReadOnly identity are rejected at the Worker before reaching the origin. The Agent still enforces the token role as defense in depth.

`AGENT_API_TOKEN` is retained only as a deprecated compatibility input for health checks during migration; it must never silently grant write access to an identity classified as ReadOnly.

## Token storage

Managed tokens in `tokens.json` must not be stored as reusable plaintext bearer credentials. New tokens are generated with 32 random bytes, returned to the caller once, and persisted as:

- SHA-256 hash of the high-entropy token,
- non-secret display prefix,
- id/name/role/created timestamp.

Existing plaintext `Token` entries are migrated on load to hash + prefix and the file is rewritten atomically. Lookup hashes the presented token and uses fixed-time hash comparison. On Unix, the token file is restricted to owner read/write permissions where supported.

The legacy `Agent:ApiToken` remains supported for backward compatibility; it remains configuration-owned and should be treated as an Admin bootstrap token.

## Audit integrity and retention

Mutation audit entries include authenticated token id/name/role. Cloud remote-user metadata is accepted only when the authenticated Agent token id is explicitly listed in `Agent:CloudGateway:TrustedTokenIds`; arbitrary clients cannot label themselves as the trusted cloud gateway merely by sending headers.

Audit logs default to 90 days retention, configurable with `Agent:AuditRetentionDays` (1-3650). Pruning is best-effort and never blocks a request.

## Web UI

The UI loads `/api/auth/me` during boot and displays the current role. In ReadOnly mode, obvious top-level mutation controls are disabled/hidden where practical. Backend authorization remains authoritative even if a UI control is missed.

For the cloud-hosted UI, `/gateway/status` returns the verified remote identity and mapped role; no Agent bearer token is ever emitted to browser JavaScript.

## Verification

- A real .NET security check creates/reloads/migrates managed tokens and proves plaintext tokens are absent from `tokens.json`.
- Worker unit tests prove Admin/ReadOnly mapping, ReadOnly write rejection and correct Agent-token selection.
- A source contract verifies `/api/auth/me`, SignalR Admin enforcement, audit trusted-token gating and CI execution.
- Existing Windows/Linux builds and Cloud Worker dry-run must remain green.
