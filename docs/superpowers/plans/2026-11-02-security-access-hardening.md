# Security and Access Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden local and Cloudflare administration with role-preserving cloud proxying, hashed managed tokens, auditable identities and explicit ReadOnly UX.

**Architecture:** Keep Agent bearer-token authorization authoritative. Cloudflare Access authenticates humans, while the Worker maps verified email identities to a role and proxies with a matching Agent token. Managed Agent tokens are stored only as hashes; audit metadata is tied to authenticated token identities.

**Tech Stack:** .NET 8 / ASP.NET Core / SignalR, JavaScript Cloudflare Worker + `jose`, Node test runner, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-security-access-hardening.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Never commit secrets or real credentials.
- Preserve local/offline Agent management.
- Cloudflare remains optional and does not become the source of truth.
- ReadOnly must be enforced on the server; UI restrictions are only supplementary.
- Existing `Agent:ApiToken` continues to work as the legacy Admin bootstrap token.

## Review Focus

- Legacy plaintext `tokens.json` migration must preserve authentication while removing reusable plaintext.
- ReadOnly Access users must not gain mutation capability through HTTP or SignalR/WebSocket paths.
- Missing cloud role configuration must fail closed for writes rather than silently treating users as Admin.
- Spoofed gateway/remote-user headers from ordinary Agent clients must not become trusted audit identity.
- Token/audit persistence must not weaken Linux file permissions or make request handling fail when pruning cannot run.

---

### Task 1: Managed token hashing and migration

**Files:**
- Modify: `src/GameServerControl.Agent/Auth/TokenRegistry.cs`
- Create: `tools/GameServerControl.SecurityCheck/GameServerControl.SecurityCheck.csproj`
- Create: `tools/GameServerControl.SecurityCheck/Program.cs`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- `TokenRegistry.Lookup(string)` remains the authentication lookup.
- `TokenRegistry.Create(string,string,TokenRole)` returns a one-time plaintext token response while persisted state stores only hash/prefix.
- Existing plaintext `Token` JSON records are migrated automatically.

- [ ] Write the failing SecurityCheck that asserts create/lookup/reload/migration and absence of plaintext in `tokens.json`.
- [ ] Run the check in CI and verify RED on current storage format.
- [ ] Implement SHA-256 persisted token hashes, fixed-time comparison, legacy migration and owner-only Unix file mode.
- [ ] Run SecurityCheck and existing builds until GREEN.

### Task 2: Cloudflare role-preserving gateway

**Files:**
- Modify: `src/GameServerControl.Cloud/src/index.js`
- Modify: `src/GameServerControl.Cloud/test/gateway-status.test.mjs`
- Modify: `src/GameServerControl.Cloud/wrangler.jsonc`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- `resolveAccessRole(payload, env) -> 'Admin' | 'ReadOnly'`
- Admin membership comes from verified `payload.email` matching `ACCESS_ADMIN_EMAILS`.
- Proxy uses `AGENT_ADMIN_TOKEN` for Admin and `AGENT_READONLY_TOKEN` for ReadOnly.
- ReadOnly state-changing HTTP requests return 403 before origin fetch.
- `/gateway/status` returns `user` and `role`, never Agent tokens.

- [ ] Add failing Worker tests for role mapping, token selection and ReadOnly write rejection.
- [ ] Run `npm test` and verify RED.
- [ ] Implement fail-closed role mapping and separate Agent token selection.
- [ ] Update dry-run CI secrets with non-secret placeholders for the new required keys.
- [ ] Run Worker tests/build/dry-run until GREEN.

### Task 3: Agent identity endpoint and audit hardening

**Files:**
- Modify: `src/GameServerControl.Agent/Program.cs`
- Modify: `src/GameServerControl.Agent/Auth/AuditLogger.cs`
- Modify: `src/GameServerControl.Agent/appsettings.json.example`
- Create: `scripts/validate-security-access.mjs`
- Modify: `.github/workflows/build.yml`

**Interfaces:**
- `GET /api/auth/me` returns authenticated token id/name/role.
- Audit entries include token id/name/role.
- `x-gameservercontrol-remote-user` is recorded only when current token id appears in `Agent:CloudGateway:TrustedTokenIds` and gateway header is `cloudflare-worker`.
- `Agent:AuditRetentionDays` defaults to 90 and is clamped to 1-3650.

- [ ] Add a failing source/security contract for identity endpoint, trusted-token gating, retention and SignalR Admin protection.
- [ ] Run it and verify RED on missing pieces.
- [ ] Implement the endpoint and audit hardening.
- [ ] Add the contract to Windows and Linux CI.
- [ ] Run contract and builds until GREEN.

### Task 4: ReadOnly-aware Web UI and documentation

**Files:**
- Modify: `src/GameServerControl.Agent/WebUi/core.js`
- Modify: `src/GameServerControl.Agent/WebUi/index.html`
- Modify: `README.md`
- Modify existing WebUI validation contract if required.

**Interfaces:**
- UI stores current auth metadata from `/api/auth/me`.
- Current role is visible in the top bar.
- Obvious creation/install/update/token mutation controls are disabled in ReadOnly mode.
- API/Hub authorization remains authoritative.

- [ ] Extend WebUI validation first so role metadata/read-only affordances are required.
- [ ] Verify RED.
- [ ] Implement minimal role badge and mutation-control disabling without changing local login behavior.
- [ ] Document creating dedicated `cloud-admin` and `cloud-readonly` Agent tokens, Worker secrets, `ACCESS_ADMIN_EMAILS`, and trusted token ids.
- [ ] Run JS validation and full CI until GREEN.

### Task 5: Final verification

**Files:** none expected.

- [ ] Confirm `main` points to the tested commit.
- [ ] Confirm Windows and Linux jobs, Agent/Relay builds, Cloud Worker tests/build/dry-run and security contracts all complete successfully.
- [ ] Review the final diff for accidental secrets or token values before declaring 6/7 complete.
