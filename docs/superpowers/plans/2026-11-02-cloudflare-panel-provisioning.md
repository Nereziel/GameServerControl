# Cloudflare Panel Provisioning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the existing Cloudflare remote-management implementation reproducibly deployable with a chosen panel hostname while failing early when Tunnel, Access, or Agent credentials are wrong.

**Architecture:** Keep the Agent as the source of truth and the VPS as game relay only. A manually dispatched GitHub Actions workflow builds the shared Web UI, verifies the private Tunnel origin with Access service credentials, deploys the Worker to an explicit custom domain, and keeps all credentials in GitHub/Worker secrets.

**Tech Stack:** GitHub Actions, Node.js 24, Wrangler 4, Cloudflare Workers Static Assets, Cloudflare Access, Cloudflare Tunnel, PowerShell.

**Spec:** `docs/cloudflare-remote-management.md`

## Global Constraints

- Work only in `Nereziel/GameServerControl`.
- Never commit Cloudflare API tokens, Agent API tokens, Tunnel tokens, Access service-token secrets, or other credentials.
- Local Agent administration must continue to work without Cloudflare.
- `workers.dev` and preview URLs remain disabled.
- Production deployment remains manual (`workflow_dispatch`), never automatic on push.

## Review Focus

- Invalid or empty panel hostname must fail before Wrangler deploy.
- Panel hostname and private origin hostname must not be the same hostname.
- Origin verification must authenticate through both Cloudflare Access Service Auth and the Agent bearer token.
- Worker deployment must attach only the explicitly requested custom domain.
- CI dry-run must continue to work without production credentials.

---

### Task 1: Parameterize production deployment

**Files:**
- Modify: `.github/workflows/cloudflare-deploy.yml`

**Interfaces:**
- Consumes: GitHub workflow inputs for panel hostname, Agent origin, Access team domain and Access audience; GitHub Actions secrets for Cloudflare and Agent/service credentials.
- Produces: Wrangler deployment on the chosen custom domain with required runtime secrets.

- [ ] Add required `workflow_dispatch` inputs for `panel_hostname`, `agent_origin`, `access_team_domain`, and `access_policy_aud`.
- [ ] Validate hostname/URL shape and reject panel/origin hostname collisions.
- [ ] Verify `${AGENT_ORIGIN}/api/health` using Access service-token headers plus Agent bearer token.
- [ ] Deploy with `wrangler deploy --strict --domain "$PANEL_HOSTNAME" --secrets-file ...`.
- [ ] Confirm the repository Build workflow remains green.

### Task 2: Add authenticated cloud diagnostics

**Files:**
- Modify: `src/GameServerControl.Cloud/src/index.js`

**Interfaces:**
- Consumes: the same Worker runtime bindings used by `/api/*` and `/hubs/*`.
- Produces: `GET /gateway/status`, protected by the panel Access JWT, which checks Agent `/api/health` without revealing credentials.

- [ ] Add a protected gateway status handler.
- [ ] Reuse the existing origin/service-token/Agent-token request path.
- [ ] Return only safe status metadata and authenticated user identity.
- [ ] Confirm Worker bundle dry-run succeeds in CI.

### Task 3: Document the exact handoff

**Files:**
- Modify: `docs/cloudflare-remote-management.md`

**Interfaces:**
- Consumes: Task 1 workflow inputs/secrets and Task 2 diagnostics endpoint.
- Produces: one exact checklist for the later account-specific setup.

- [ ] Document which values are normal workflow inputs and which are GitHub secrets.
- [ ] Document the required order: Tunnel + origin Access first, then panel Access application, then Worker deploy.
- [ ] Document the custom-domain behavior and `/gateway/status` verification endpoint.
- [ ] Document that account resources are still intentionally not created until concrete domain/admin identities are supplied.
